using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests for <see cref="GenerateCodeStep"/>.
/// Covers the success path (non-rework and rework), skip path, and agent-stop path.
///
/// Risk: zero behavioral coverage on the core AI code-generation step.
/// </summary>
public class GenerateCodeStepTests
{
    private static readonly ILogger Logger = new Serilog.LoggerConfiguration().CreateLogger();

    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<IAgentPhaseExecutor> _agentExecution = new();

    public GenerateCodeStepTests()
    {
        _callbacks.Setup(c => c.EmitOutputLine(It.IsAny<string>()));
        _callbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()));
        _callbacks.Setup(c => c.SwapAgentLabel(
            It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _callbacks.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);
        _callbacks.Setup(c => c.NotifyChange());
    }

    private PipelineStepContext BuildContext(PipelineRun? run = null) =>
        new()
        {
            Run = run ?? MakeRun(),
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = _callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = _agentExecution.Object,
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(Logger),
            Logger = Logger,
            Issue = MakeIssue(),
            ParsedIssue = new ParsedIssue { AcceptanceCriteria = [], RequirementsSection = "" }
        };

    private static PipelineRun MakeRun(PipelineRunType runType = PipelineRunType.Implementation) =>
        new()
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "42",
            IssueTitle = "Fix the bug",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = runType,
            WorkspacePath = "/tmp/workspace"
        };

    private static IssueDetail MakeIssue() =>
        new()
        {
            Identifier = "42",
            Title = "Fix the bug",
            Description = "Something is broken",
            Labels = ["bug"]
        };

    // ── Non-rework success path ──────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NonRework_AgentSucceeds_ReturnsContinue()
    {
        // Arrange: no LinkedPullRequest → non-rework path; agent returns true (continue)
        _agentExecution
            .Setup(e => e.ExecuteCodeGenerationAsync(
                It.IsAny<AgentPhaseContext>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(true);

        var step = new GenerateCodeStep();
        var context = BuildContext();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue, "agent returning true means pipeline should continue");
        _agentExecution.Verify(e => e.ExecuteCodeGenerationAsync(
            It.IsAny<AgentPhaseContext>(), It.IsAny<CancellationToken>(), null),
            Times.Once, "non-rework must pass null promptOverride");
    }

    [Fact]
    public async Task ExecuteAsync_NonRework_AgentRequestsStop_ReturnsStop()
    {
        // Arrange: agent returns false → stop
        _agentExecution
            .Setup(e => e.ExecuteCodeGenerationAsync(
                It.IsAny<AgentPhaseContext>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(false);

        var step = new GenerateCodeStep();
        var context = BuildContext();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop, "agent returning false must stop the pipeline");
    }

    // ── Rework path: LinkedPullRequest present ───────────────────────────

    [Fact]
    public async Task ExecuteAsync_ReworkWithReviewComments_BuildsPromptAndDelegates()
    {
        // Arrange: LinkedPullRequest with review comments → rework prompt built and passed
        _agentExecution
            .Setup(e => e.ExecuteCodeGenerationAsync(
                It.IsAny<AgentPhaseContext>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .ReturnsAsync(true);

        var run = MakeRun(PipelineRunType.Review);
        run.LinkedPullRequest = new LinkedPullRequest
        {
            BranchName = "feature/fix",
            IsDraft = false,
            Number = 10,
            Url = "https://github.com/org/repo/pull/10",
            ReviewComments =
            [
                new PullRequestReviewComment
                {
                    Author = "reviewer",
                    Body = "This method is too long",
                    CreatedAt = DateTime.UtcNow,
                    Id = "comment-1",
                    Path = "src/MyService.cs"
                }
            ]
        };

        var step = new GenerateCodeStep();
        var context = BuildContext(run);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        // Rework path must pass a non-null prompt override
        _agentExecution.Verify(e => e.ExecuteCodeGenerationAsync(
            It.IsAny<AgentPhaseContext>(), It.IsAny<CancellationToken>(), It.IsNotNull<string>()),
            Times.Once, "rework path must pass a non-null promptOverride to the agent executor");
    }

    [Fact]
    public async Task ExecuteAsync_ReworkWithNullPrompt_SkipsAndReturnsContinue()
    {
        // Arrange: LinkedPullRequest with no conflicts, no comments, not draft
        // → PromptBuilder.BuildReworkPrompt returns null → skip path
        var run = MakeRun(PipelineRunType.Review);
        run.LinkedPullRequest = new LinkedPullRequest
        {
            BranchName = "feature/no-changes",
            IsDraft = false,
            Number = 11,
            Url = "https://github.com/org/repo/pull/11",
            ReviewComments = [] // no comments → null rework prompt
        };
        // run.MergeConflictFiles defaults to empty — no conflicts

        var step = new GenerateCodeStep();
        var context = BuildContext(run);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // PromptBuilder.BuildReworkPrompt returns null when no comments, no conflicts, not draft
        result.Should().Be(StepResult.Continue, "null rework prompt must skip agent and return Continue");
        _agentExecution.Verify(e => e.ExecuteCodeGenerationAsync(
            It.IsAny<AgentPhaseContext>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()),
            Times.Never, "agent must not be called when rework prompt is null");
    }
}
