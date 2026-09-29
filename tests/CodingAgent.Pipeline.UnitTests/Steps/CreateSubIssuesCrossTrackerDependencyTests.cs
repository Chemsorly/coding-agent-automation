using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Steps;

/// <summary>
/// Integration tests for cross-tracker dependency URL emission in <see cref="CreateSubIssuesStep"/>.
/// Validates that when sub-issues are routed to different trackers, dependencies between them
/// are written as full issue URLs rather than bare #N references.
///
/// Feature: Issue #3156 — cross-repo decomposition dependency bug.
/// Acceptance criteria:
///   AC1 — two sub-issues routed to different trackers, second depending on first: second body
///          contains first's full URL.
///   AC2 — siblings in the same tracker still get #N.
/// </summary>
[Collection("Metrics")]
[Trait("Feature", "027-epic-decomposition-pipeline")]
public class CreateSubIssuesCrossTrackerDependencyTests : IDisposable
{
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<IAgentIssueOperations> _issueOps = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly string _workspacePath;

    public CreateSubIssuesCrossTrackerDependencyTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"cross-tracker-dep-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
            Directory.Delete(_workspacePath, recursive: true);
    }

    // ── File helpers ──────────────────────────────────────────────────────────

    private void WriteSubIssueFile(string filename, string title, string body,
        string[] dependencies, string? targetRepository = null)
    {
        var dir = Path.Combine(_workspacePath, AgentWorkspacePaths.SubIssuesDirectory);
        Directory.CreateDirectory(dir);
        var depsJson = string.Join(", ", dependencies.Select(d => $"\"{d}\""));
        var targetJson = targetRepository is not null ? $",\n    \"targetRepository\": \"{targetRepository}\"" : "";
        var json = $$"""
        {
            "title": "{{title}}",
            "body": "{{body}}",
            "dependencies": [{{depsJson}}],
            "labels": []{{targetJson}}
        }
        """;
        File.WriteAllText(Path.Combine(dir, filename), json);
    }

    // ── Context builders ──────────────────────────────────────────────────────

    private PipelineRun CreateRun(string issueProviderId = "ip-default") => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "100",
        IssueTitle = "Test Epic",
        IssueProviderConfigId = issueProviderId,
        RepoProviderConfigId = "rp-api",
        StartedAt = DateTime.UtcNow,
        RunType = PipelineRunType.Decomposition,
        WorkspacePath = _workspacePath
    };

    /// <summary>
    /// Builds a context with a project containing two templates:
    ///   api   → issue provider "p-api",  repo provider "rp-api"
    ///   web   → issue provider "p-web",  repo provider "rp-web"
    /// The run's executor is "api" (RepoProviderId = "rp-api").
    /// </summary>
    private PipelineStepContext BuildCrossTrackerContext(PipelineRun run)
    {
        return new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp", MaxDecompositionSubIssues = 10 },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = _callbacks.Object,
            IssueOps = _issueOps.Object,
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger,
            ProjectContext = new DecompositionProjectContext
            {
                ProjectName = "CrossTrackerProject",
                Repositories =
                [
                    new RepositoryTarget
                    {
                        TemplateName = "api",
                        Description = "API service",
                        DecompositionEnabled = true,
                        Available = true,
                        IssueProviderId = "p-api",
                        RepoProviderId = "rp-api"
                    },
                    new RepositoryTarget
                    {
                        TemplateName = "web",
                        Description = "Web frontend",
                        DecompositionEnabled = true,
                        Available = true,
                        IssueProviderId = "p-web",
                        RepoProviderId = "rp-web"
                    }
                ]
            }
        };
    }

    // ── AC1: cross-tracker dependency emits full URL ──────────────────────────

    /// <summary>
    /// AC1: Two sub-issues routed to different trackers, the second depending on the first.
    /// The second's body must contain the first's full URL.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CrossTrackerDependency_SecondBodyContainsFirstFullUrl()
    {
        // Arrange — api issue (no deps), web issue depends on api issue by title
        WriteSubIssueFile("01-api.json", "API endpoint", "Implement the endpoint",
            dependencies: [], targetRepository: "api");
        WriteSubIssueFile("02-web.json", "Web page", "Implement the web page",
            dependencies: ["API endpoint"], targetRepository: "web");

        // api issue → created as issue 40 with full URL
        _issueOps.Setup(x => x.CreateIssueForProviderAsync(
                "p-api", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult
            {
                Identifier = "40",
                Url = "https://github.com/acme/api/issues/40"
            });

        // Capture body passed to web provider
        string? capturedWebBody = null;
        _issueOps.Setup(x => x.CreateIssueForProviderAsync(
                "p-web", It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<string>, CancellationToken>(
                (_, _, body, _, _) => capturedWebBody = body)
            .ReturnsAsync(new CreatedIssueResult
            {
                Identifier = "21",
                Url = "https://github.com/acme/web/issues/21"
            });

        var run = CreateRun();
        var context = BuildCrossTrackerContext(run);
        var step = new CreateSubIssuesStep();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
        run.SubIssueResults.Should().HaveCount(2);
        run.SubIssueResults.Should().AllSatisfy(r => r.Success.Should().BeTrue());

        capturedWebBody.Should().NotBeNull("web issue body should have been captured");
        capturedWebBody.Should().Contain(
            "Depends on https://github.com/acme/api/issues/40",
            because: "cross-tracker dependency must be written as the full issue URL, not #40");
        capturedWebBody.Should().NotContain(
            "Depends on #40",
            because: "#40 refers to the wrong tracker from web's perspective");
    }

    // ── AC2: same-tracker dependency emits #N ────────────────────────────────

    /// <summary>
    /// AC2: Two sub-issues routed to the SAME tracker. The dependency must remain #N.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_SameTrackerDependency_SecondBodyContainsShortNumber()
    {
        // Both issues routed to "api" tracker
        WriteSubIssueFile("01-first.json", "First API feature", "First feature body",
            dependencies: [], targetRepository: "api");
        WriteSubIssueFile("02-second.json", "Second API feature", "Second feature body",
            dependencies: ["First API feature"], targetRepository: "api");

        // First api issue → created as 40
        _issueOps.Setup(x => x.CreateIssueForProviderAsync(
                "p-api", "First API feature", It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult
            {
                Identifier = "40",
                Url = "https://github.com/acme/api/issues/40"
            });

        // Capture body passed for second api issue
        string? capturedSecondBody = null;
        _issueOps.Setup(x => x.CreateIssueForProviderAsync(
                "p-api", "Second API feature", It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, IReadOnlyList<string>, CancellationToken>(
                (_, _, body, _, _) => capturedSecondBody = body)
            .ReturnsAsync(new CreatedIssueResult
            {
                Identifier = "41",
                Url = "https://github.com/acme/api/issues/41"
            });

        var run = CreateRun();
        var context = BuildCrossTrackerContext(run);
        var step = new CreateSubIssuesStep();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — same tracker: short form must be used
        capturedSecondBody.Should().NotBeNull();
        capturedSecondBody.Should().Contain(
            "Depends on #40",
            because: "same-tracker dependency must stay as #N");
        capturedSecondBody.Should().NotContain(
            "https://github.com/acme/api/issues/40",
            because: "same-tracker deps must not be written as full URLs");
    }

    // ── Repo epic (no project context): same-tracker default ─────────────────

    [Fact]
    public async Task ExecuteAsync_RepoEpic_NoProjectContext_DependencyUsesShortNumber()
    {
        // Both issues use the run's own tracker (no project context)
        WriteSubIssueFile("01-first.json", "Task A", "Task A body",
            dependencies: []);
        WriteSubIssueFile("02-second.json", "Task B", "Task B body",
            dependencies: ["Task A"]);

        // First issue → issue 10
        _issueOps.Setup(x => x.CreateIssueAsync(
                "Task A", It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreatedIssueResult
            {
                Identifier = "10",
                Url = "https://github.com/acme/main/issues/10"
            });

        string? capturedBody = null;
        _issueOps.Setup(x => x.CreateIssueAsync(
                "Task B", It.IsAny<string>(), It.IsAny<IReadOnlyList<string>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, IReadOnlyList<string>, CancellationToken>(
                (_, body, _, _) => capturedBody = body)
            .ReturnsAsync(new CreatedIssueResult
            {
                Identifier = "11",
                Url = "https://github.com/acme/main/issues/11"
            });

        // No project context — repo epic scenario
        var run = CreateRun(issueProviderId: "p-main");
        var context = new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp", MaxDecompositionSubIssues = 10 },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = _callbacks.Object,
            IssueOps = _issueOps.Object,
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger,
            ProjectContext = null  // repo epic — no project context
        };
        var step = new CreateSubIssuesStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("Depends on #10",
            because: "repo-epic sub-issues are all in the same tracker so #N form is correct");
    }
}
