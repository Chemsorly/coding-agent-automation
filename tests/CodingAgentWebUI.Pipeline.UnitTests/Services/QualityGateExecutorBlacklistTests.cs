using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for blacklisted file detection in QualityGateExecutor.
/// Validates: RecordBlacklistedFiles behavior via AppendExternalCiIfNeededAsync.
/// </summary>
public class QualityGateExecutorBlacklistTests
{
    private readonly Mock<IQualityGateValidator> _mockValidator;
    private readonly Mock<IPipelineCallbacks> _mockCallbacks;
    private readonly Mock<IAgentIssueOperations> _mockIssueOps;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IPipelineProvider> _mockPipelineProvider;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly QualityGateExecutor _orchestrator;

    private static readonly QualityGateReport PassingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
    };

    public QualityGateExecutorBlacklistTests()
    {
        _mockValidator = new Mock<IQualityGateValidator>();
        _mockCallbacks = new Mock<IPipelineCallbacks>();
        _mockIssueOps = new Mock<IAgentIssueOperations>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockPipelineProvider = new Mock<IPipelineProvider>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _orchestrator = new QualityGateExecutor(
            _mockValidator.Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object);

        _mockCallbacks.Setup(c => c.SwapAgentLabel(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.RemoveAllAgentLabels(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.UpdateFileChangeStats(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.CreatePullRequest(It.IsAny<PipelineRun>(), It.IsAny<QualityGateReport>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockIssueOps.Setup(o => o.SwapLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Mock GetRunStatusAsync so CI polling exits immediately (CI "appeared")
        _mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Running,
                Jobs = new List<PipelineJobResult> { new() { Name = "build", State = PipelineRunState.Running } }
            });
    }

    [Fact]
    public async Task AppendExternalCi_BlacklistedFilesInWarnMode_RecordsAndContinues()
    {
        // Arrange
        var blacklistedFiles = new List<string> { ".agent/settings.json", ".github/workflows/ci.yml" };
        var run = CreateRun();
        var config = CreateConfig();

        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(blacklistedFiles.AsReadOnly());
        _mockRepoProvider.Setup(r => r.HasCommitsAheadAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockRepoProvider.Setup(r => r.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("abc123");
        _mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Passed, Jobs = new List<PipelineJobResult>() });

        var context = BuildContext(run, config);

        // Act
        var result = await _orchestrator.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        // Assert: pipeline continues — blacklisted files recorded but no failure
        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.Failed), Times.Never);
        _mockIssueOps.Verify(o => o.SwapLabelAsync(It.IsAny<IssueIdentifier>(), AgentLabels.Error, It.IsAny<CancellationToken>()), Times.Never);
        run.BlacklistedFilesDetected.Should().Contain(".agent/settings.json");
        run.BlacklistedFilesDetected.Should().Contain(".github/workflows/ci.yml");
        run.FailureReason.Should().BeNull();
    }

    [Fact]
    public async Task AppendExternalCi_BlacklistedFilesInWarnMode_ContinuesPipeline()
    {
        // Arrange
        var blacklistedFiles = new List<string> { ".agent/config.json" };
        var run = CreateRun();
        var config = CreateConfig();

        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(blacklistedFiles.AsReadOnly());
        _mockRepoProvider.Setup(r => r.HasCommitsAheadAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockRepoProvider.Setup(r => r.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("abc123");
        _mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Passed, Jobs = new List<PipelineJobResult>() });

        var context = BuildContext(run, config);

        // Act
        var result = await _orchestrator.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        // Assert: pipeline continues — NotifyChange called, no failure transition
        _mockCallbacks.Verify(c => c.NotifyChange(), Times.AtLeastOnce);
        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.Failed), Times.Never);
        _mockIssueOps.Verify(o => o.SwapLabelAsync(It.IsAny<IssueIdentifier>(), AgentLabels.Error, It.IsAny<CancellationToken>()), Times.Never);
        run.BlacklistedFilesDetected.Should().Contain(".agent/config.json");
        run.FailureReason.Should().BeNull();
    }

    [Fact]
    public async Task AppendExternalCi_NoBlacklistedFiles_NoOp()
    {
        // Arrange
        var run = CreateRun();
        var config = CreateConfig();

        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(r => r.HasCommitsAheadAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockRepoProvider.Setup(r => r.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("abc123");
        _mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Passed, Jobs = new List<PipelineJobResult>() });

        var context = BuildContext(run, config);

        // Act
        var result = await _orchestrator.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        // Assert: no blacklist actions taken
        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.Failed), Times.Never);
        _mockIssueOps.Verify(o => o.SwapLabelAsync(It.IsAny<IssueIdentifier>(), AgentLabels.Error, It.IsAny<CancellationToken>()), Times.Never);
        _mockCallbacks.Verify(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>()), Times.Never);
        run.BlacklistedFilesDetected.Should().BeEmpty();
        run.FailureReason.Should().BeNull();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PipelineRun CreateRun() => new()
    {
        RunId = "test-run-blacklist",
        IssueIdentifier = "42",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-blacklist-test-{Guid.NewGuid():N}"),
        BranchName = "feature/auto-42-test"
    };

    private static PipelineConfiguration CreateConfig() => new()
    {
        AgentTimeout = TimeSpan.FromMinutes(10),
        MaxRetries = 0,
        BlacklistedPaths = new[] { ".agent", ".github" },
        ExternalCiTimeout = TimeSpan.FromMinutes(5),
        StallPollInterval = TimeSpan.FromMilliseconds(50),
        StallWarningInterval = TimeSpan.FromHours(1)
    };

    private QualityGateContext BuildContext(PipelineRun run, PipelineConfiguration config) => new()
    {
        Run = run,
        Config = config,
        AgentProvider = new Mock<IAgentProvider>().Object,
        IssueOps = _mockIssueOps.Object,
        Callbacks = _mockCallbacks.Object,
        RepoProvider = _mockRepoProvider.Object,
        PipelineProvider = _mockPipelineProvider.Object,
        QualityGateConfigs = new List<QualityGateConfiguration>()
    };
}
