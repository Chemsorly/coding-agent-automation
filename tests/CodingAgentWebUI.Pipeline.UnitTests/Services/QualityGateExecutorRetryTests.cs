using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for QualityGateExecutor retry loop mechanics:
/// retry exhaustion, cancellation between iterations, mixed gate results, and first-attempt success.
/// </summary>
public class QualityGateExecutorRetryTests
{
    private readonly Mock<IQualityGateValidator> _mockValidator;
    private readonly Mock<IAgentProvider> _mockAgent;
    private readonly Mock<IPipelineCallbacks> _mockCallbacks;
    private readonly Mock<IAgentIssueOperations> _mockIssueOps;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly PipelineRun _run;
    private readonly QualityGateExecutor _executor;

    private static readonly QualityGateReport FailingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = false, Details = "Build error CS1234" },
        Tests = new GateResult { GateName = "Tests", Passed = false, Details = "2 tests failed" }
    };

    private static readonly QualityGateReport PassingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "Build succeeded" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "All tests passed" }
    };

    private static readonly QualityGateReport CompilationFailsTestsPass = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = false, Details = "Build error" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "All tests passed" }
    };

    private static readonly QualityGateReport CompilationPassesTestsFail = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "Build succeeded" },
        Tests = new GateResult { GateName = "Tests", Passed = false, Details = "3 tests failed" }
    };

    public QualityGateExecutorRetryTests()
    {
        _mockValidator = new Mock<IQualityGateValidator>();
        _mockAgent = new Mock<IAgentProvider>();
        _mockCallbacks = new Mock<IPipelineCallbacks>();
        _mockIssueOps = new Mock<IAgentIssueOperations>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockHistoryService = new Mock<IPipelineRunHistoryService>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _run = new PipelineRun
        {
            RunId = "test-run-retry",
            IssueIdentifier = "99",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-retry-test-{Guid.NewGuid():N}")
        };

        _executor = new QualityGateExecutor(
            _mockValidator.Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object,
            _mockHistoryService.Object);

        // Default callback setups (follow QualityGateExecutorFeedbackTests pattern)
        _mockCallbacks.Setup(c => c.SwapAgentLabel(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.RemoveAllAgentLabels(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.UpdateFileChangeStats(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.CreatePullRequest(It.IsAny<PipelineRun>(), It.IsAny<QualityGateReport>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.FinalizePullRequest(It.IsAny<PipelineRun>(), It.IsAny<QualityGateReport>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Default issue ops
        _mockIssueOps.Setup(o => o.SwapLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Default history service
        _mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());

        // Default agent health status (IsProcessAlive defaults to null — safe for stall monitor)
        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = false });

        // Default agent execution — returns non-empty output to avoid empty-response detection
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = new[] { "Fixed the issue" },
                Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 }
            });
    }

    // ── Retry Exhaustion ─────────────────────────────────────────────────────

    [Fact]
    public async Task RetryExhaustion_FinalizesPullRequestAsDraft()
    {
        var config = CreateConfig(maxRetries: 2);
        SetupValidatorAlwaysFails();

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        _mockCallbacks.Verify(c => c.FinalizePullRequest(
            _run, It.IsAny<QualityGateReport>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RetryExhaustion_IncrementsRetryCountCorrectly()
    {
        var config = CreateConfig(maxRetries: 2);
        SetupValidatorAlwaysFails();

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        _run.RetryCount.Should().Be(2);
    }

    [Fact]
    public async Task RetryExhaustion_PopulatesRetryErrorsOnEachFailure()
    {
        var config = CreateConfig(maxRetries: 2);
        SetupValidatorAlwaysFails();

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        // Initial failure + 2 retry failures = 3 error entries
        // (initial failure error is added in the retry loop on first iteration,
        //  plus the final exhaustion error added after the loop)
        _run.RetryErrors.Should().NotBeEmpty();
        _run.RetryErrors.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    // ── Cancellation Between Retry Iterations ────────────────────────────────

    [Fact]
    public async Task Cancellation_BetweenRetries_TransitionsToCancelled()
    {
        var config = CreateConfig(maxRetries: 3);
        var callCount = 0;

        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .Returns((string _, IReadOnlyList<QualityGateConfiguration> _, CancellationToken _, string? _) =>
            {
                callCount++;
                if (callCount >= 2)
                    throw new OperationCanceledException();
                return Task.FromResult(FailingReport);
            });

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.Cancelled), Times.Once);
    }

    [Fact]
    public async Task Cancellation_BetweenRetries_SwapsToAgentCancelledLabel()
    {
        var config = CreateConfig(maxRetries: 3);
        var callCount = 0;

        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .Returns((string _, IReadOnlyList<QualityGateConfiguration> _, CancellationToken _, string? _) =>
            {
                callCount++;
                if (callCount >= 2)
                    throw new OperationCanceledException();
                return Task.FromResult(FailingReport);
            });

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        _mockCallbacks.Verify(
            c => c.SwapAgentLabel(_run.IssueIdentifier, AgentLabels.Cancelled, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Mixed Gate Results — Retry Prompt Content ────────────────────────────

    [Fact]
    public async Task MixedResults_CompilationPassesTestsFail_PromptContainsBothStatuses()
    {
        var config = CreateConfig(maxRetries: 1);
        string? capturedPrompt = null;

        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync(CompilationPassesTestsFail);

        _mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                if (!req.Prompt.Contains("Pipeline Failure Feedback"))
                    capturedPrompt = req.Prompt;
            })
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = new[] { "Fixed" },
                Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 }
            });

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        capturedPrompt.Should().NotBeNull();
        capturedPrompt.Should().Contain("Compilation: PASSED");
        capturedPrompt.Should().Contain("Tests: FAILED");
    }

    [Fact]
    public async Task MixedResults_TestsPassCompilationFails_PromptContainsBothStatuses()
    {
        var config = CreateConfig(maxRetries: 1);
        string? capturedPrompt = null;

        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync(CompilationFailsTestsPass);

        _mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                if (!req.Prompt.Contains("Pipeline Failure Feedback"))
                    capturedPrompt = req.Prompt;
            })
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = new[] { "Fixed" },
                Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 }
            });

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        capturedPrompt.Should().NotBeNull();
        capturedPrompt.Should().Contain("Compilation: FAILED");
        capturedPrompt.Should().Contain("Tests: PASSED");
    }

    // ── First-Attempt Success ────────────────────────────────────────────────

    [Fact]
    public async Task FirstAttemptSuccess_NoRetry_FinalizesAsNonDraft()
    {
        var config = CreateConfig(maxRetries: 3);
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync(PassingReport);

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        _run.RetryCount.Should().Be(0);
        _mockCallbacks.Verify(c => c.FinalizePullRequest(
            _run, It.IsAny<QualityGateReport>(), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FirstAttemptSuccess_NoAgentFixCallMade()
    {
        var config = CreateConfig(maxRetries: 3);
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync(PassingReport);

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        // Agent is called for cleanup but NOT for retry fixes or feedback
        // Verify no call with "Quality gates failed" prompt (retry prompt signature)
        _mockAgent.Verify(a => a.ExecuteAsync(
            It.Is<AgentRequest>(r => r.Prompt.Contains("Quality gates failed")),
            It.IsAny<CancellationToken>(),
            It.IsAny<Action<string>?>()), Times.Never);
    }

    // ── Provider Error Category — Retry Budget ───────────────────────────────

    /// <summary>
    /// When the agent returns ProviderRateLimit (HTTP 429), RetryCount must not be incremented.
    /// The retry loop decrements RetryCount back after the increment at the top, then delays.
    /// We cancel via the CancellationToken to exit the delay immediately without waiting 30 s.
    /// </summary>
    [Fact]
    public async Task RateLimitResult_DoesNotIncrementRetryCount()
    {
        var config = CreateConfig(maxRetries: 2);
        SetupValidatorAlwaysFails();

        using var cts = new CancellationTokenSource();

        _mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                // Cancel after the retry agent call (not the feedback agent)
                // TODO: [WARNING] This callback fires on every non-feedback call. If the rate-limit path
                // incorrectly made a second implementation attempt before cancellation propagated, the
                // callback would fire again (double-cancel is safe but masks the scenario). Consider
                // tracking a call counter and cancelling only on the first non-feedback invocation.
                if (!req.Prompt.Contains("Pipeline Failure Feedback"))
                    cts.Cancel();
            })
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 1, // GeneralFailure
                OutputLines = new[] { "HTTP 429: rate limited" },
                ErrorCategory = AgentErrorCategory.ProviderRateLimit
            });

        // The Task.Delay(30s, ct) throws OperationCanceledException when ct is cancelled,
        // unwinding the loop. ProceedToQualityGatesAsync catches it and transitions to Cancelled.
        await _executor.ProceedToQualityGatesAsync(BuildContext(config), cts.Token);

        // RetryCount must be 0: the loop incremented it, then decremented it on the rate-limit path
        // TODO: [WARNING] This assertion is only meaningful if the decrement executed before cancellation
        // fired. A defect removing RetryCount-- would still produce RetryCount==0 if cancellation fired
        // before the second loop increment. A stronger test would use maxRetries:2, issue two rate-limit
        // responses, and assert RetryCount==0 after both — ensuring both decrements were actually executed.
        // TODO: [WARNING] This test does not assert that the fix/implementation prompt was NOT sent during
        // the rate-limit iteration. Add: _mockAgent.Verify(a => a.ExecuteAsync(It.Is<AgentRequest>(r =>
        // r.Prompt.Contains("Quality gates failed")), ...), Times.Never) to catch regressions where the
        // fix prompt is dispatched despite the rate-limit path being taken.
        _run.RetryCount.Should().Be(0,
            "ProviderRateLimit must not consume retry budget — RetryCount must be decremented back");
    }

    /// <summary>
    /// When the agent returns ProviderOverload (HTTP 503), RetryCount must not be incremented.
    /// Same mechanics as the 429 test above.
    /// </summary>
    [Fact]
    public async Task ProviderOverloadResult_DoesNotIncrementRetryCount()
    {
        var config = CreateConfig(maxRetries: 2);
        SetupValidatorAlwaysFails();

        using var cts = new CancellationTokenSource();

        _mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                if (!req.Prompt.Contains("Pipeline Failure Feedback"))
                    cts.Cancel();
            })
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 1, // GeneralFailure
                OutputLines = new[] { "HTTP 503: service unavailable" },
                ErrorCategory = AgentErrorCategory.ProviderOverload
            });

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), cts.Token);

        // TODO: [WARNING] Same fragility as RateLimitResult_DoesNotIncrementRetryCount: the assertion
        // RetryCount==0 does not prove the decrement fired — cancellation before the second increment
        // would also produce 0. Use maxRetries:2 with two overload responses for a stronger proof.
        // TODO: [WARNING] Does not assert that the fix/implementation prompt was NOT sent during the
        // overload iteration. Add a Times.Never verify on "Quality gates failed" prompt to guard against
        // regressions where the fix prompt is dispatched on the overload path.
        _run.RetryCount.Should().Be(0,
            "ProviderOverload must not consume retry budget — RetryCount must be decremented back");
    }

    /// <summary>
    /// When the agent returns PermanentAuthFailure (HTTP 401/403), the loop must break immediately
    /// without exhausting all configured retries. RetryCount is 1 (one attempt was made).
    /// </summary>
    [Fact]
    public async Task PermanentAuthFailure_AbortsLoopImmediately()
    {
        var config = CreateConfig(maxRetries: 3);
        SetupValidatorAlwaysFails();

        _mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 1, // GeneralFailure
                OutputLines = new[] { "HTTP 401: unauthorized" },
                ErrorCategory = AgentErrorCategory.PermanentAuthFailure
            });

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        // Loop broke after 1 attempt — did not exhaust all 3 retries
        _run.RetryCount.Should().Be(1,
            "PermanentAuthFailure must abort immediately after the first attempt");

        // Verify the retry agent was called exactly once (ignoring the feedback/cleanup agents)
        // TODO: [WARNING] This test does not assert that no fix/implementation prompt was sent *after*
        // the auth failure break. If the break were accidentally placed after the fix prompt dispatch,
        // RetryCount and call-count assertions would still pass. Add a verify that confirms the
        // "Quality gates failed" prompt was sent at most once (the one that triggered the auth failure),
        // not twice (i.e. no second attempt was made after the break).
        _mockAgent.Verify(a => a.ExecuteAsync(
            It.Is<AgentRequest>(r => r.Prompt.Contains("Quality gates failed")),
            It.IsAny<CancellationToken>(),
            It.IsAny<Action<string>?>()), Times.Once);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PipelineConfiguration CreateConfig(int maxRetries) => new()
    {
        AgentTimeout = TimeSpan.FromMinutes(10),
        MaxRetries = maxRetries,
        StallPollInterval = TimeSpan.FromMilliseconds(50),
        StallWarningInterval = TimeSpan.FromHours(1)
    };

    private void SetupValidatorAlwaysFails()
    {
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync(FailingReport);
    }

    private QualityGateContext BuildContext(PipelineConfiguration config) => new()
    {
        Run = _run,
        Config = config,
        AgentProvider = _mockAgent.Object,
        IssueOps = _mockIssueOps.Object,
        Callbacks = _mockCallbacks.Object,
        RepoProvider = _mockRepoProvider.Object,
        QualityGateConfigs = new[]
        {
            new QualityGateConfiguration
            {
                DisplayName = "Test QGC",
                CompilationCommand = "dotnet",
                CompilationArguments = new[] { "build" },
                TestCommand = "dotnet",
                TestArguments = new[] { "test" }
            }
        },
        Issue = new IssueDetail
        {
            Identifier = "99",
            Title = "Test Issue",
            Description = "Test issue description",
            Labels = new[] { "bug" }
        }
    };
}
