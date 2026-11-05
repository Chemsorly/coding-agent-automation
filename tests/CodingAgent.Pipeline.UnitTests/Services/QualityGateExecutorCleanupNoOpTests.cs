using System.Collections.Concurrent;
using AwesomeAssertions;
using Moq;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Pins issue #4070: after the pre-PR cleanup, the final quality gate pass runs only when the cleanup
/// changed the code. "Changed" means a different <c>HEAD</c> or a different working-tree fingerprint,
/// and an unknown state (a null or failing fingerprint) counts as changed.
///
/// The mocked fingerprint and <c>HEAD</c> depend on whether the cleanup agent has run yet: they return the
/// "before" value until <c>ExecuteAsync</c> is called and the "after" value from then on. A capture taken
/// in the wrong place, or a cleanup that never runs, therefore changes what the executor sees.
/// </summary>
public class QualityGateExecutorCleanupNoOpTests
{
    private readonly Mock<IQualityGateValidator> _mockValidator = new();
    private readonly Mock<IAgentProvider> _mockAgent = new();
    private readonly Mock<IPipelineCallbacks> _mockCallbacks = new();
    private readonly Mock<IAgentIssueOperations> _mockIssueOps = new();
    private readonly Mock<IRepositoryProvider> _mockRepoProvider = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly ConcurrentQueue<string> _output = new();
    private bool _cleanupHasRun;
    private readonly PipelineRun _run;
    private readonly QualityGateExecutor _executor;

    private static readonly QualityGateReport PassingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "Build succeeded" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "All tests passed" }
    };

    private static readonly QualityGateReport FailingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = false, Details = "Build error CS1234" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "All tests passed" }
    };

    public QualityGateExecutorCleanupNoOpTests()
    {
        _executor = new QualityGateExecutor(
            _mockValidator.Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object);

        _run = new PipelineRun
        {
            RunId = "cleanup-noop-test",
            IssueIdentifier = "4070",
            IssueTitle = "Skip the final quality gates when the cleanup changed nothing",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = Path.GetTempPath(),
            BranchName = "agent/42"
        };

        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PassingReport);

        _mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-head");

        _mockCallbacks.Setup(c => c.EmitOutputLine(It.IsAny<string>()))
            .Callback<string>(line => _output.Enqueue(line));
        _mockCallbacks.Setup(c => c.FinalizePullRequest(It.IsAny<PipelineRun>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);

        _mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .Callback(() => _cleanupHasRun = true)
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["Cleaned up"],
                Usage = new TokenUsage { InputTokens = 1, OutputTokens = 1 }
            });
        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = false });
    }

    [Fact]
    public async Task WhenCleanupLeavesTheTreeUnchanged_ShouldSkipTheFinalGatesAndFinalizeThePr()
    {
        SetupFingerprints(Files(), Files());

        await _executor.ProceedToQualityGatesAsync(BuildContext(), CancellationToken.None);

        VerifyCleanupRuns(Times.Once());
        VerifyValidatorCalls(Times.Once());
        _run.QualityGateHistory.Should().HaveCount(1, "no gates ran after the cleanup, so nothing more is recorded");
        _output.Should().Contain(l => l.Contains("Final quality gates skipped"));
        _output.Should().NotContain(l => l.Contains("Running final quality gates"));
        VerifyFinalized(isDraft: false, Times.Once());
    }

    [Fact]
    public async Task WhenCleanupLeavesTheSameUncommittedChanges_ShouldSkipTheFinalGates()
    {
        SetupFingerprints(Files(("src/A.cs", "hash-a")), Files(("src/A.cs", "hash-a")));

        await _executor.ProceedToQualityGatesAsync(BuildContext(), CancellationToken.None);

        VerifyValidatorCalls(Times.Once());
        _output.Should().Contain(l => l.Contains("Final quality gates skipped"));
    }

    [Fact]
    public async Task WhenCleanupEditsAFile_ShouldRunTheFinalGates()
    {
        SetupFingerprints(Files(("src/A.cs", "hash-a")), Files(("src/A.cs", "hash-b")));

        await _executor.ProceedToQualityGatesAsync(BuildContext(), CancellationToken.None);

        VerifyValidatorCalls(Times.Exactly(2));
        _output.Should().Contain(l => l.Contains("Running final quality gates"));
        _output.Should().NotContain(l => l.Contains("Final quality gates skipped"));
    }

    [Fact]
    public async Task WhenCleanupAddsAFile_ShouldRunTheFinalGates()
    {
        SetupFingerprints(Files(), Files(("src/B.cs", "hash-b")));

        await _executor.ProceedToQualityGatesAsync(BuildContext(), CancellationToken.None);

        VerifyValidatorCalls(Times.Exactly(2));
    }

    [Fact]
    public async Task WhenCleanupCommitsItsChange_ShouldRunTheFinalGates()
    {
        SetupFingerprints(Files(), Files());
        _mockRepoProvider
            .Setup(r => r.GetHeadCommitShaAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(_cleanupHasRun ? "sha-after" : "sha-before"));

        await _executor.ProceedToQualityGatesAsync(BuildContext(), CancellationToken.None);

        VerifyValidatorCalls(Times.Exactly(2));
    }

    [Fact]
    public async Task WhenHeadIsUnavailable_ShouldRunTheFinalGates()
    {
        SetupFingerprints(Files(), Files());
        _mockRepoProvider
            .Setup(r => r.GetHeadCommitShaAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("detached HEAD"));

        await _executor.ProceedToQualityGatesAsync(BuildContext(), CancellationToken.None);

        VerifyValidatorCalls(Times.Exactly(2));
    }

    [Fact]
    public async Task WhenTheFingerprintIsUnavailable_ShouldRunTheFinalGates()
    {
        SetupFingerprints(null, null);

        await _executor.ProceedToQualityGatesAsync(BuildContext(), CancellationToken.None);

        VerifyValidatorCalls(Times.Exactly(2));
    }

    [Fact]
    public async Task WhenTheFingerprintThrowsAfterCleanup_ShouldRunTheFinalGates()
    {
        _mockRepoProvider
            .Setup(r => r.GetWorkingTreeFingerprintAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns(() => _cleanupHasRun
                ? Task.FromException<IReadOnlyDictionary<string, string>?>(new InvalidOperationException("corrupt index"))
                : Task.FromResult<IReadOnlyDictionary<string, string>?>(Files()));

        await _executor.ProceedToQualityGatesAsync(BuildContext(), CancellationToken.None);

        VerifyValidatorCalls(Times.Exactly(2));
    }

    [Fact]
    public async Task WhenCleanupChangedCodeAndTheFinalGatesFail_ShouldStillNotFinalizeAsReady()
    {
        SetupFingerprints(Files(), Files(("src/B.cs", "hash-b")));
        _mockValidator
            .SetupSequence(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PassingReport)
            .ReturnsAsync(FailingReport);

        await _executor.ProceedToQualityGatesAsync(BuildContext(maxRetries: 0), CancellationToken.None);

        VerifyFinalized(isDraft: false, Times.Never());
        VerifyFinalized(isDraft: true, Times.Once());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>A fresh fingerprint instance per call, so equality can never come from reference identity.</summary>
    private static IReadOnlyDictionary<string, string> Files(params (string Path, string Hash)[] files)
        => files.ToDictionary(f => f.Path, f => f.Hash);

    private void SetupFingerprints(
        IReadOnlyDictionary<string, string>? beforeCleanup, IReadOnlyDictionary<string, string>? afterCleanup)
        => _mockRepoProvider
            .Setup(r => r.GetWorkingTreeFingerprintAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(_cleanupHasRun ? afterCleanup : beforeCleanup));

    private void VerifyCleanupRuns(Times times)
        => _mockAgent.Verify(a => a.ExecuteAsync(
            It.IsAny<AgentRequest>(),
            It.IsAny<CancellationToken>(),
            It.IsAny<Action<string>?>()), times);

    private void VerifyValidatorCalls(Times times)
        => _mockValidator.Verify(v => v.ValidateAsync(
            It.IsAny<WorkspacePath>(),
            It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
            It.IsAny<CancellationToken>()), times);

    private void VerifyFinalized(bool isDraft, Times times)
        => _mockCallbacks.Verify(c => c.FinalizePullRequest(_run, isDraft, It.IsAny<CancellationToken>()), times);

    private QualityGateContext BuildContext(int maxRetries = 0) => new()
    {
        Run = _run,
        Config = new PipelineConfiguration
        {
            MaxRetries = maxRetries,
            AgentTimeout = TimeSpan.FromMinutes(1),
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromHours(1)
        },
        AgentProvider = _mockAgent.Object,
        IssueOps = _mockIssueOps.Object,
        Callbacks = _mockCallbacks.Object,
        RepoProvider = _mockRepoProvider.Object,
        PipelineProvider = null,
        QualityGateConfigs =
        [
            new QualityGateConfiguration { Id = "qgc", DisplayName = "QGC", MatchLabels = [], CompilationCommand = "dotnet" }
        ],
        QgcsConfiguredAtDispatch = true
    };
}
