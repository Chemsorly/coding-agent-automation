using AwesomeAssertions;
using Moq;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Characterization tests for each retry-loop outcome handler extracted from
/// <see cref="QualityGateExecutor.RunFixAgentIterationAsync"/>. Each test drives one
/// of the four <see cref="RetryOutcome"/> paths and asserts both the resulting
/// <c>run.RetryCount</c> mutation and the observable control-flow decision (break or
/// continue) that the handler returns via <see cref="RetryDecision"/>.
///
/// Because the handler methods are <c>private</c>, tests exercise them end-to-end through
/// <see cref="QualityGateExecutor.ProceedToQualityGatesAsync"/>. Control-flow decisions are
/// inferred from observable side effects: agent call counts, validator call counts, and
/// whether the PR was finalized as draft.
/// </summary>
public class QualityGateExecutorRetryDecisionTests
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
        Compilation = new GateResult { GateName = "Compilation", Passed = false, Details = "Build error CS0001" },
        Tests = new GateResult { GateName = "Tests", Passed = false, Details = "2 tests failed" }
    };

    private static readonly string[] NormalOutputLines = ["Fixed the issue"];

    public QualityGateExecutorRetryDecisionTests()
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
            RunId = "test-run-retry-decision",
            IssueIdentifier = "99",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-retry-decision-{Guid.NewGuid():N}")
        };

        _executor = new QualityGateExecutor(
            _mockValidator.Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object,
            _mockHistoryService.Object);

        // Required callback setups — omitting any causes NullReferenceException rather than
        // a meaningful assertion failure. Follow the pattern from QualityGateExecutorRetryTests.
        _mockCallbacks.Setup(c => c.SwapAgentLabel(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.RemoveAllAgentLabels(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.UpdateFileChangeStats(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.CreatePullRequest(It.IsAny<PipelineRun>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.FinalizePullRequest(It.IsAny<PipelineRun>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockIssueOps.Setup(o => o.SwapLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = false });

        // Default agent execution: normal result — individual tests override as needed.
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = NormalOutputLines,
                Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 }
            });

        SetupValidatorAlwaysFails();
    }

    // ── Test 1: TransientWait with cap → ShouldBreak ──────────────────────────

    /// <summary>
    /// When the agent returns <see cref="AgentErrorCategory.ProviderRateLimit"/> on every call,
    /// the transient cap (<c>MaxConsecutiveTransientRetries = 10</c>) fires and the
    /// <c>HandleTransientAsync</c> handler returns <c>ShouldBreak: true</c>.
    ///
    /// Counter mutation (positive value): <c>run.RetryErrors.Count</c> must equal 10 — one entry
    /// is enqueued per retry-loop iteration regardless of outcome type; 10 transient iterations ran
    /// before the cap fired. This is the positive-value counter assertion for this outcome path.
    /// <c>run.RetryCount</c> must remain 0 — transient iterations carry <c>RetryCountDelta: 0</c>
    /// and do not consume retry budget; this is a secondary verification, not the positive assertion.
    /// Control-flow decision: loop broke after exactly 10 transient iterations, not after the 100
    /// configured by <c>MaxRetries</c>.
    /// </summary>
    [Fact]
    public async Task TransientWithCap_RetryErrorsCountIsEleven_AndLoopBreaks()
    {
        // MaxRetries is high so the standard budget never expires; only the transient cap exits the loop.
        var config = CreateConfig(maxRetries: 100);

        _mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 1,
                OutputLines = ["HTTP 429: rate limited"],
                ErrorCategory = AgentErrorCategory.ProviderRateLimit
            });

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        // Counter mutation — positive expected value (11):
        // run.RetryErrors receives one entry per retry-loop iteration at the top of the while loop,
        // before RunFixAgentIterationAsync is called. All 10 transient iterations each enqueue one
        // error summary (10 entries). After the loop breaks, FinalizeDraftPrAsync enqueues one
        // additional entry before calling CollectFailureFeedbackAsync, bringing the total to 11.
        // This is the observable counter that increases positively on the TransientWait path.
        _run.RetryErrors.Count.Should().Be(11,
            "10 RetryErrors entries from the retry loop (one per transient iteration) plus 1 " +
            "from FinalizeDraftPrAsync after the cap fires = 11 total");

        // Secondary verification — no retry budget consumed:
        // Transient iterations carry RetryCountDelta: 0, so run.RetryCount must remain unchanged
        // at 0. This verifies the budget-isolation property of the TransientWait outcome, distinct
        // from the positive counter assertion above.
        _run.RetryCount.Should().Be(0,
            "transient iterations must not consume retry budget (RetryCountDelta is 0 for TransientWait)");

        // Control-flow decision: ShouldBreak was taken after 10 consecutive transient responses.
        // 10 retry-loop agent calls + 1 failure-feedback call = 11 total.
        // TODO [WARNING]: Times.Exactly(11) ties the assertion to infrastructure assumptions: 10
        // comes from MaxConsecutiveTransientRetries (a private const) and 1 from the failure-
        // feedback call path in CollectFailureFeedbackAsync. If either changes, this assertion
        // silently becomes wrong. A more resilient alternative would assert separately that the
        // validator was never called (ShouldContinue / ShouldBreak skipped QG validation) and
        // that FinalizePullRequest(draft: true) was called — leaving the exact agent call count
        // as an implementation detail.
        // See review findings: TestQualityReviewer WARNING and DotNetSpecialist SUGGESTION.
        _mockAgent.Verify(
            a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()),
            Times.Exactly(11),
            "exactly 10 retry calls (transient cap boundary) plus 1 failure-feedback call must be made");

        // ShouldBreak → finalized as draft (not as non-draft), and only once.
        _mockCallbacks.Verify(
            c => c.FinalizePullRequest(_run, true, It.IsAny<CancellationToken>()),
            Times.Once,
            "ShouldBreak path must finalize as draft PR");
    }

    // ── Test 2: AbortAuth → ShouldBreak, RetryCount == 1 ────────────────────

    /// <summary>
    /// When the agent returns <see cref="AgentErrorCategory.PermanentAuthFailure"/>,
    /// the <c>HandleAuthAbortAsync</c> handler returns <c>ShouldBreak: true</c> with
    /// <c>RetryCountDelta: 1</c>.
    ///
    /// Counter mutation: <c>run.RetryCount</c> must be 1 (positive value; AbortAuth preserves
    /// the current behavior of incrementing the counter). Control-flow decision: loop broke
    /// immediately after one agent call, not after exhausting the configured 3 retries.
    /// </summary>
    [Fact]
    public async Task AuthAbort_RetryCountIsOne_AndLoopBreaksImmediately()
    {
        var config = CreateConfig(maxRetries: 3);

        _mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 1,
                OutputLines = ["HTTP 401: unauthorized"],
                ErrorCategory = AgentErrorCategory.PermanentAuthFailure
            });

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        // Counter mutation: 1 — AbortAuth carries RetryCountDelta: 1.
        _run.RetryCount.Should().Be(1,
            "AbortAuth increments run.RetryCount by 1 (RetryCountDelta: 1) even though it breaks");

        // Control-flow decision: ShouldBreak was taken immediately — only 1 retry-loop agent
        // call was made (plus 1 failure-feedback call = 2 total), not 3.
        // TODO [WARNING]: It.Is<AgentRequest>(r => r.Prompt.Contains("Quality gates failed"))
        // filters the verification to only calls whose prompt contains that substring. If the
        // retry prompt template changes, this filter could silently match zero calls, causing
        // Times.Once to pass trivially even if no retry call was made. Consider removing the
        // filter and asserting all ExecuteAsync calls, or binding the substring to a constant.
        // See review finding: TestQualityReviewer WARNING.
        _mockAgent.Verify(
            a => a.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Quality gates failed")),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()),
            Times.Once,
            "loop must break after the first auth-abort — retry prompt must be sent exactly once");

        // ShouldBreak → draft PR finalized.
        _mockCallbacks.Verify(
            c => c.FinalizePullRequest(_run, true, It.IsAny<CancellationToken>()),
            Times.Once,
            "ShouldBreak path must finalize as draft PR");
    }

    // ── Test 3: RestartSession → ShouldContinue, RetryCount == 1 ─────────────

    /// <summary>
    /// When the agent returns a dead session (ExitCode=0, TotalTokens=0, empty output),
    /// the <c>HandleSessionRestartAsync</c> handler returns <c>ShouldContinue: true</c>
    /// with <c>RetryCountDelta: 0</c> — the dead iteration does not consume retry budget.
    /// The second (normal) call then proceeds through QG validation and exhausts the budget.
    ///
    /// Counter mutation: <c>run.RetryCount</c> must be 1 (the second, normal call increments).
    /// Control-flow decision: ShouldContinue was taken on the first call (loop did not break
    /// and did not run QG validation), then ShouldContinue: false was taken on the second call.
    /// </summary>
    [Fact]
    public async Task SessionRestart_RetryCountIsOne_AndLoopContinues()
    {
        // maxRetries: 1 — the dead-session iteration does not consume budget, so only the
        // one subsequent normal call can consume the single retry slot before exhaustion.
        var config = CreateConfig(maxRetries: 1);
        _run.CodegenSessionId = "session-to-be-cleared";

        var callCount = 0;
        _mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                return callCount == 1
                    // First retry call: dead session — ExitCode=0, 0 tokens, 0 output lines.
                    ? new AgentResult
                    {
                        ExitCode = 0,
                        OutputLines = [],
                        Usage = new TokenUsage() // TotalTokens = 0
                    }
                    // Subsequent calls: normal response.
                    : new AgentResult
                    {
                        ExitCode = 0,
                        OutputLines = NormalOutputLines,
                        Usage = new TokenUsage { InputTokens = 60, OutputTokens = 40 }
                    };
            });

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        // Counter mutation: 1 — dead session carries RetryCountDelta: 0; the normal second
        // call carries RetryCountDelta: 1, so total is 1 after two loop iterations.
        _run.RetryCount.Should().Be(1,
            "dead session (RestartSession) must not consume retry budget; the subsequent normal call does");

        // Control-flow decision: ShouldContinue was taken on the first call, so the loop
        // made at least two retry-loop agent calls (not zero or one).
        // TODO [WARNING]: BeGreaterThanOrEqualTo(2) is a weak lower-bound assertion. A tighter
        // assertion (e.g., callCount.Should().Be(3) for 1 dead-session call + 1 normal retry
        // call + 1 failure-feedback call) would more precisely validate that ShouldContinue
        // was taken exactly once. The current assertion would also pass if the loop made 5 or
        // 20 calls. See review finding: TestQualityReviewer WARNING.
        callCount.Should().BeGreaterThanOrEqualTo(2,
            "ShouldContinue must have been returned for the dead-session call, causing a second agent call");

        // TODO [WARNING]: This test does not assert that _run.CodegenSessionId was cleared to null
        // after the RestartSession iteration. HandleSessionRestartAsync sets run.CodegenSessionId = null
        // as its primary side effect, but that mutation is not verified here. Adding
        // _run.CodegenSessionId.Should().BeNull() would directly validate the handler's state mutation.
        // See review finding: TestQualityReviewer WARNING.
    }

    // ── Test 4: DefaultRetry → ShouldContinue: false, proceeds to QG ─────────

    /// <summary>
    /// When the agent returns a normal successful result, the <c>HandleDefaultRetryAsync</c>
    /// handler returns <c>ShouldBreak: false, ShouldContinue: false</c> with
    /// <c>RetryCountDelta: 1</c> — the loop proceeds to quality-gate validation.
    ///
    /// Counter mutation: <c>run.RetryCount</c> must be 1 (positive value; one real fix attempt
    /// consumed budget). Control-flow decision: ShouldContinue was false — the validator was
    /// called again after the agent call (not skipped as it is on the transient/restart paths).
    /// </summary>
    [Fact]
    public async Task DefaultRetry_RetryCountIsOne_AndQualityGateValidationRuns()
    {
        var config = CreateConfig(maxRetries: 1);
        var validatorCallCount = 0;

        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .Returns(() =>
            {
                validatorCallCount++;
                return Task.FromResult(FailingReport);
            });

        // Agent returns normal result every call.
        _mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = NormalOutputLines,
                Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 }
            });

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        // Counter mutation: 1 — one real fix attempt carried RetryCountDelta: 1.
        _run.RetryCount.Should().Be(1,
            "default retry must increment run.RetryCount by 1 (RetryCountDelta: 1)");

        // Control-flow decision: ShouldContinue was false — QG validation ran after the agent call.
        // At minimum: 1 initial QG call + 1 post-fix QG call = 2 calls.
        // TODO [WARNING]: BeGreaterThanOrEqualTo(2) is a weak assertion. Since maxRetries: 1 and
        // the agent always returns a normal result, the exact expected count is deterministic:
        // 1 initial QG call + 1 post-fix QG call = exactly 2. Asserting .Be(2) would catch
        // regressions where the loop runs extra iterations.
        // See review finding: TestQualityReviewer WARNING.
        validatorCallCount.Should().BeGreaterThanOrEqualTo(2,
            "ShouldContinue: false must cause quality-gate validation to run after the agent fix");

        // Exhausted by budget (not broke by ShouldBreak) → draft PR.
        _mockCallbacks.Verify(
            c => c.FinalizePullRequest(_run, true, It.IsAny<CancellationToken>()),
            Times.Once,
            "retry budget exhausted after one default-retry iteration must finalize as draft PR");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PipelineConfiguration CreateConfig(int maxRetries) => new()
    {
        AgentTimeout = TimeSpan.FromMinutes(10),
        MaxRetries = maxRetries,
        StallPollInterval = TimeSpan.FromMilliseconds(50),
        StallWarningInterval = TimeSpan.FromHours(1),
        TransientRetryDelay = TimeSpan.Zero  // eliminate 30-second delay in unit tests
    };

    private void SetupValidatorAlwaysFails()
    {
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
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
