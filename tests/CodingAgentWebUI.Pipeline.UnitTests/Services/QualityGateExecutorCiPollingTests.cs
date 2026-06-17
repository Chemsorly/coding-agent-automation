using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Verifies that external CI polling always filters by commit SHA,
/// including when a PR exists (regression test for #542).
/// </summary>
public class QualityGateExecutorCiPollingTests
{
    private readonly Mock<IPipelineCallbacks> _mockCallbacks;
    private readonly Mock<IAgentIssueOperations> _mockIssueOps;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IPipelineProvider> _mockPipelineProvider;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly QualityGateExecutor _executor;

    private static readonly QualityGateReport PassingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
    };

    public QualityGateExecutorCiPollingTests()
    {
        _mockCallbacks = new Mock<IPipelineCallbacks>();
        _mockIssueOps = new Mock<IAgentIssueOperations>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockPipelineProvider = new Mock<IPipelineProvider>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _executor = new QualityGateExecutor(
            new Mock<IQualityGateValidator>().Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object);

        SetupDefaultMocks();
    }

    [Fact]
    public async Task AppendExternalCi_WithPullRequestNumber_PassesShaToPoller()
    {
        var run = CreateRun();
        run.PullRequestNumber = "99";

        var context = BuildContext(run);

        await _executor.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        _mockPipelineProvider.Verify(p => p.WaitForCompletionAsync(
            run.BranchName!, "sha-head-abc", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AppendExternalCi_WithPullRequestNumber_InfraRetry_PassesShaToPoller()
    {
        var run = CreateRun();
        run.PullRequestNumber = "99";

        // First call: infrastructure failure; second call: passes
        var infraFailure = new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = new List<PipelineJobResult>
            {
                new()
                {
                    Name = "build", State = PipelineRunState.Failed,
                    LogContent = "lost communication with the server"
                }
            }
        };
        var passed = new PipelineRunStatus { State = PipelineRunState.Passed, Jobs = new List<PipelineJobResult>() };

        _mockPipelineProvider.SetupSequence(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(infraFailure)
            .ReturnsAsync(passed);

        // Infra retry creates an empty commit + push, then reads new SHA
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), true, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);

        var context = BuildContext(run);

        await _executor.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        // Both initial poll and retry poll should pass the SHA (not null)
        _mockPipelineProvider.Verify(p => p.WaitForCompletionAsync(
            run.BranchName!, "sha-head-abc", It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task AppendExternalCi_WhenShaReadFails_PassesNullToPoller()
    {
        var run = CreateRun();
        run.PullRequestNumber = "99";

        _mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("git not available"));

        var context = BuildContext(run);

        await _executor.AppendExternalCiIfNeededAsync(context, PassingReport, false, CancellationToken.None);

        // Graceful degradation: null SHA means branch-only filtering
        _mockPipelineProvider.Verify(p => p.WaitForCompletionAsync(
            run.BranchName!, null, It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetupDefaultMocks()
    {
        _mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _mockRepoProvider.Setup(r => r.PushBranchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-head-abc");
        _mockPipelineProvider.Setup(p => p.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Passed, Jobs = new List<PipelineJobResult>() });
    }

    private static PipelineRun CreateRun() => new()
    {
        RunId = "test-run-ci-poll",
        IssueIdentifier = "542",
        IssueTitle = "CI polling fix",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-cipoll-test-{Guid.NewGuid():N}"),
        BranchName = "feature/auto-542-ci-poll"
    };

    private QualityGateContext BuildContext(PipelineRun run) => new()
    {
        Run = run,
        Config = new PipelineConfiguration
        {
            AgentTimeout = TimeSpan.FromMinutes(10),
            MaxRetries = 0,
            MaxInfrastructureRetries = 2,
            ExternalCiTimeout = TimeSpan.FromMinutes(5),
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromHours(1)
        },
        AgentProvider = new Mock<IAgentProvider>().Object,
        IssueOps = _mockIssueOps.Object,
        Callbacks = _mockCallbacks.Object,
        RepoProvider = _mockRepoProvider.Object,
        PipelineProvider = _mockPipelineProvider.Object,
        QualityGateConfigs = new List<QualityGateConfiguration>()
    };
}
