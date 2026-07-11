using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests for <see cref="WriteProjectContextStep"/>.
/// Verifies .agent/project-context.md generation for cross-repo decomposition.
/// Feature: 029-pipeline-projects
/// </summary>
public class WriteProjectContextStepTests : IDisposable
{
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly string _workspacePath;

    public WriteProjectContextStepTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"write-ctx-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
            Directory.Delete(_workspacePath, recursive: true);
    }

    [Fact]
    public async Task ExecuteAsync_ProjectContextIsNull_ReturnsContinueWithoutWritingFile()
    {
        // Arrange
        var run = CreateRun();
        var context = BuildContext(run, projectContext: null);
        var step = new WriteProjectContextStep();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);
        var agentDir = Path.Combine(_workspacePath, ".agent");
        Directory.Exists(agentDir).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_ProjectContextHasRepositories_GeneratesProjectContextMd()
    {
        // Arrange
        var projectContext = new DecompositionProjectContext
        {
            ProjectName = "MyProject",
            Repositories =
            [
                new RepositoryTarget
                {
                    TemplateName = "backend-api",
                    Description = "Backend REST API service",
                    DecompositionEnabled = true,
                    Available = true,
                    Labels = ["csharp", "dotnet"]
                },
                new RepositoryTarget
                {
                    TemplateName = "frontend-web",
                    Description = "React web frontend",
                    DecompositionEnabled = false,
                    Available = true,
                    Labels = ["typescript", "react"]
                }
            ]
        };

        var run = CreateRun();
        var context = BuildContext(run, projectContext);
        var step = new WriteProjectContextStep();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);

        var filePath = Path.Combine(_workspacePath, ".agent", "project-context.md");
        File.Exists(filePath).Should().BeTrue();

        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Contain("# Project Context");
        content.Should().Contain("**Project:** MyProject");
        content.Should().Contain("### backend-api");
        content.Should().Contain("- **Description:** Backend REST API service");
        content.Should().Contain("- **Decomposition enabled:** True");
        content.Should().Contain("- **Status:** ✓");
        content.Should().Contain("- **Labels:** csharp, dotnet");
        content.Should().Contain("### frontend-web");
        content.Should().Contain("- **Decomposition enabled:** False");
        content.Should().Contain("- **Labels:** typescript, react");
        content.Should().Contain("## Routing Instructions");
    }

    [Fact]
    public async Task ExecuteAsync_RepositoryWithEmptyLabels_NoLabelsLineInOutput()
    {
        // Arrange
        var projectContext = new DecompositionProjectContext
        {
            ProjectName = "NoLabelsProject",
            Repositories =
            [
                new RepositoryTarget
                {
                    TemplateName = "service-a",
                    Description = "Service A",
                    DecompositionEnabled = true,
                    Available = true,
                    Labels = [] // empty labels
                }
            ]
        };

        var run = CreateRun();
        var context = BuildContext(run, projectContext);
        var step = new WriteProjectContextStep();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);

        var filePath = Path.Combine(_workspacePath, ".agent", "project-context.md");
        var content = await File.ReadAllTextAsync(filePath);

        // The "Labels:" line should NOT appear for repos with empty labels
        content.Should().NotContain("- **Labels:**");
    }

    [Fact]
    public async Task ExecuteAsync_UnavailableRepository_ShowsUnavailableStatus()
    {
        // Arrange
        var projectContext = new DecompositionProjectContext
        {
            ProjectName = "MixedAvailability",
            Repositories =
            [
                new RepositoryTarget
                {
                    TemplateName = "healthy-service",
                    Description = "Healthy service",
                    DecompositionEnabled = true,
                    Available = true,
                    Labels = []
                },
                new RepositoryTarget
                {
                    TemplateName = "broken-service",
                    Description = "Broken service",
                    DecompositionEnabled = true,
                    Available = false,
                    Labels = []
                }
            ]
        };

        var run = CreateRun();
        var context = BuildContext(run, projectContext);
        var step = new WriteProjectContextStep();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        result.Should().Be(StepResult.Continue);

        var filePath = Path.Combine(_workspacePath, ".agent", "project-context.md");
        var content = await File.ReadAllTextAsync(filePath);

        // healthy-service should show ✓
        content.Should().Contain("### healthy-service");
        content.Should().Contain("- **Status:** ✓");

        // broken-service should show ⚠️ unavailable
        content.Should().Contain("### broken-service");
        content.Should().Contain("- **Status:** ⚠️ unavailable");
    }

    #region Helpers

    private PipelineStepContext BuildContext(PipelineRun run, DecompositionProjectContext? projectContext)
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
            ProviderConfigStore = Mock.Of<IConfigurationStore>(),
            QualityGateConfigStore = Mock.Of<IConfigurationStore>(),
            ReviewerConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = _callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger,
            ProjectContext = projectContext
        };
    }

    private PipelineRun CreateRun() => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "42",
        IssueTitle = "Test Epic",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow,
        RunType = PipelineRunType.Decomposition,
        WorkspacePath = _workspacePath
    };

    #endregion
}
