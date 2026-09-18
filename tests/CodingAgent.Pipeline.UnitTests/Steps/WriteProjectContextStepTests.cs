using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests for <see cref="WriteProjectContextStep"/>.
/// Verifies .agent/project-context.md generation for cross-repo decomposition.
/// Feature: 029-pipeline-projects
/// </summary>
public class WriteProjectContextStepTests : IDisposable
{
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<Serilog.ILogger> _logger = new();
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

    [Fact]
    public async Task ExecuteAsync_FileWriteThrows_ReturnsStepResultContinueAndLogsWarning()
    {
        // Arrange — create a file where the .agent directory should be,
        // so Directory.CreateDirectory throws IOException (file exists at path).
        var blockerFile = Path.Combine(_workspacePath, ".agent");
        File.WriteAllText(blockerFile, "I am a file, not a directory");
        // Now workspace points to a path whose .agent entry is a file — CreateDirectory will throw.
        // TODO: This relies on Directory.CreateDirectory throwing when a file exists at the target path,
        // which is POSIX/Win32 behaviour but is not a documented .NET BCL contract. Consider injecting a
        // file-system abstraction that can be faulted deterministically for a more robust test.

        var run = CreateRun(_workspacePath);
        var projectContext = new DecompositionProjectContext
        {
            ProjectName = "FailProject",
            Repositories =
            [
                new RepositoryTarget
                {
                    TemplateName = "some-repo",
                    Description = "Some repo",
                    DecompositionEnabled = true,
                    Available = true,
                    Labels = []
                }
            ]
        };
        var context = BuildContext(run, projectContext);
        var step = new WriteProjectContextStep();

        // Act
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Assert — exception is swallowed; run continues
        result.Should().Be(StepResult.Continue);

        // Assert — warning was logged with the run ID
        // TODO: Tighten this Verify to check the actual RunId value is forwarded, e.g.:
        //   It.Is<string>(id => id == run.RunId) as the third argument instead of It.IsAny<string>().
        // The current matcher accepts any string (or even an empty/null value), so a regression that
        // drops or changes the RunId in the log call would not be caught. Also note that Moq's handling
        // of Serilog's params object[] overload may differ between versions — consider using
        // It.IsAny<object>() or It.IsAny<object[]>() if the verify behaves unexpectedly.
        _logger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.IsAny<string>(),
                It.IsAny<string>()),
            Times.Once);
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
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = _callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger.Object),
            Logger = _logger.Object,
            ProjectContext = projectContext
        };
    }

    private PipelineRun CreateRun(string? workspacePath = null) => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "42",
        IssueTitle = "Test Epic",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow,
        RunType = PipelineRunType.Decomposition,
        WorkspacePath = workspacePath ?? _workspacePath
    };

    #endregion
}
