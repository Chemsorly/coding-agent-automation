using AwesomeAssertions;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Moq;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Web.TestUtilities;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Characterization tests verifying that the <c>quality_gate.retries</c> counter emitted by
/// <see cref="QualityGateExecutor"/> includes the <c>outcome</c> dimension in addition to the
/// standard run-type and project context tags.
///
/// These tests exercise the production call site via a real <see cref="QualityGateExecutor"/>
/// with an injected <see cref="TestMeterFactory"/> so that <c>quality_gate.retries</c> is
/// isolated to this test class and does not bleed into the global static
/// <see cref="PipelineTelemetry.QualityGateRetries"/> instrument.
///
/// Each test is a TDD "red" test against the original broken code (which emits only
/// <c>RunTypeTag</c>) and becomes "green" once the fix wires <c>BuildRetryTags</c> into
/// each outcome branch of <c>RunFixAgentIterationAsync</c>.
/// </summary>
public class QualityGateExecutorRetryCounterDimensionTests : IDisposable
{
    private readonly TestMeterFactory _meterFactory = new();
    private readonly MetricCollector<long> _retriesCollector;

    private readonly Mock<IQualityGateValidator> _mockValidator = new();
    private readonly Mock<IAgentProvider> _mockAgent = new();
    private readonly Mock<IPipelineCallbacks> _mockCallbacks = new();
    private readonly Mock<IAgentIssueOperations> _mockIssueOps = new();
    private readonly Mock<IRepositoryProvider> _mockRepoProvider = new();
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly PipelineRun _run;
    private readonly QualityGateExecutor _executor;

    private static readonly QualityGateReport FailingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = false, Details = "Build error CS0001" },
        Tests = new GateResult { GateName = "Tests", Passed = false, Details = "2 tests failed" }
    };

    // TODO [WARNING]: PassingReport is declared but never referenced in this test class (dead field).
    // If no future tests require it, consider removing it to avoid a potential CS0649 compiler
    // warning under warning-as-error configurations.
    private static readonly QualityGateReport PassingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "Build succeeded" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "All tests passed" }
    };

    public QualityGateExecutorRetryCounterDimensionTests()
    {
        _retriesCollector = new MetricCollector<long>(_meterFactory, PipelineTelemetry.SourceName, "quality_gate.retries");

        _run = new PipelineRun
        {
            RunId = "retry-counter-dim-test",
            IssueIdentifier = "2744",
            IssueTitle = "Retry counter dimension test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            ProjectId = "proj-2744",
            ProjectName = "RetryCounterProject",
            WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-retry-dim-{Guid.NewGuid():N}")
        };

        _executor = new QualityGateExecutor(
            _mockValidator.Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object,
            _mockHistoryService.Object,
            _meterFactory);

        SetupDefaultMocks();
    }

    public void Dispose()
    {
        _retriesCollector.Dispose();
        _meterFactory.Dispose();
    }

    // ── Normal retry outcome (default branch) ────────────────────────────────

    /// <summary>
    /// When the fix agent runs successfully (default/Retry outcome), the
    /// <c>quality_gate.retries</c> counter must include
    /// <c>run_type</c>, <c>pipeline.project_id</c>, <c>pipeline.project_name</c>,
    /// AND <c>outcome="retry"</c>.
    /// </summary>
    [Fact]
    public async Task RetryCounter_OnNormalRetryOutcome_EmitsRunType_ProjectId_ProjectName_AndOutcomeRetry()
    {
        // Arrange: validator always fails so we enter the retry loop.
        // Agent returns a normal successful result → default (Retry) branch.
        // After one retry the validator still fails → retries exhausted → draft PR.
        SetupValidatorAlwaysFails();
        SetupAgentSuccessResponse();

        var config = CreateConfig(maxRetries: 1);

        // Act
        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        // Assert: at least one retry emission with the outcome tag
        var measurements = _retriesCollector.GetMeasurementSnapshot();
        measurements.Should().NotBeEmpty("the retry loop must have fired at least once");
        // TODO [WARNING]: The combined predicate below catches the case where outcome is emitted but
        // one of run_type/project_id/project_name is missing — however the failure message won't
        // isolate which tag is absent. The remaining three outcome tests only assert the outcome tag
        // and do not verify the standard tags, so a regression stripping pipeline.project_id or
        // pipeline.project_name from BuildRetryTags would not be caught by those tests. Consider
        // splitting the assertion into separate Should().Contain() calls (one per tag) to produce
        // targeted failure messages, and add parallel tag assertions to the other outcome tests.
        measurements.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Contains(new KeyValuePair<string, object?>("outcome", "retry")) &&
            m.Tags.Contains(new KeyValuePair<string, object?>("run_type", "implementation")) &&
            m.Tags.Contains(new KeyValuePair<string, object?>("pipeline.project_id", "proj-2744")) &&
            m.Tags.Contains(new KeyValuePair<string, object?>("pipeline.project_name", "RetryCounterProject")),
            "normal retry outcome must emit outcome='retry' plus run_type, project_id, and project_name tags");
    }

    // ── Transient outcome ────────────────────────────────────────────────────

    /// <summary>
    /// When the fix agent returns <see cref="AgentErrorCategory.ProviderRateLimit"/> (transient),
    /// the counter must include <c>outcome="transient"</c>.
    /// We cancel after the first transient to avoid the 30-second delay.
    /// </summary>
    [Fact]
    public async Task RetryCounter_OnTransientOutcome_EmitsOutcomeTransient()
    {
        SetupValidatorAlwaysFails();

        using var cts = new CancellationTokenSource();

        _mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                // TODO [WARNING]: Fragile prompt-content guard — cancels based on whether the prompt
                // does NOT contain "Pipeline Failure Feedback". If the feedback prompt template changes
                // to include that string, the CancellationTokenSource fires on the wrong call and the
                // test may produce a false pass or false fail. Consider replacing with a call-count
                // guard (see session-restart test) for robustness, or use maxRetries: 1 to let the
                // loop exhaust naturally. Additionally, there is a race between cancellation propagation
                // and the _qualityGateRetries.Add call: if OperationCanceledException propagates before
                // Add executes in the switch case, measurements could be empty, causing a non-deterministic
                // failure. Switching to maxRetries: 1 (no CancellationTokenSource) would eliminate both risks.
                if (!req.Prompt.Contains("Pipeline Failure Feedback"))
                    cts.Cancel();
            })
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 1,
                OutputLines = ["HTTP 429: rate limited"],
                ErrorCategory = AgentErrorCategory.ProviderRateLimit,
                Usage = new TokenUsage { InputTokens = 5, OutputTokens = 0 }
            });

        var config = CreateConfig(maxRetries: 5);
        await _executor.ProceedToQualityGatesAsync(BuildContext(config), cts.Token);

        var measurements = _retriesCollector.GetMeasurementSnapshot();
        measurements.Should().NotBeEmpty("the retry loop must have fired at least once");
        measurements.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Contains(new KeyValuePair<string, object?>("outcome", "transient")),
            "ProviderRateLimit outcome must emit outcome='transient'");
    }

    // ── Auth abort outcome ───────────────────────────────────────────────────

    /// <summary>
    /// When the fix agent returns <see cref="AgentErrorCategory.PermanentAuthFailure"/>,
    /// the counter must include <c>outcome="auth_abort"</c>.
    /// </summary>
    [Fact]
    public async Task RetryCounter_OnAuthAbortOutcome_EmitsOutcomeAuthAbort()
    {
        SetupValidatorAlwaysFails();

        _mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 1,
                OutputLines = ["HTTP 401: unauthorized"],
                ErrorCategory = AgentErrorCategory.PermanentAuthFailure,
                Usage = new TokenUsage { InputTokens = 5, OutputTokens = 0 }
            });

        var config = CreateConfig(maxRetries: 3);
        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        var measurements = _retriesCollector.GetMeasurementSnapshot();
        measurements.Should().NotBeEmpty("the retry loop must have fired at least once");
        measurements.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Contains(new KeyValuePair<string, object?>("outcome", "auth_abort")),
            "PermanentAuthFailure outcome must emit outcome='auth_abort'");
    }

    // ── Session restart outcome ──────────────────────────────────────────────

    /// <summary>
    /// When the fix agent returns ExitCode=0 with zero tokens and no output (dead session),
    /// the counter must include <c>outcome="session_restart"</c>.
    /// </summary>
    [Fact]
    public async Task RetryCounter_OnSessionRestartOutcome_EmitsOutcomeSessionRestart()
    {
        SetupValidatorAlwaysFails();

        var callCount = 0;
        _mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                    // Dead/exhausted session → RestartSession outcome
                    return new AgentResult
                    {
                        ExitCode = 0,
                        OutputLines = [],
                        Usage = new TokenUsage()  // TotalTokens == 0
                    };
                // Second call: normal response so the loop can exhaust via MaxRetries
                return new AgentResult
                {
                    ExitCode = 0,
                    OutputLines = ["done"],
                    Usage = new TokenUsage { InputTokens = 10, OutputTokens = 5 }
                };
            });

        var config = CreateConfig(maxRetries: 1);
        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        var measurements = _retriesCollector.GetMeasurementSnapshot();
        measurements.Should().NotBeEmpty("the retry loop must have fired at least once");
        measurements.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Contains(new KeyValuePair<string, object?>("outcome", "session_restart")),
            "dead-session outcome must emit outcome='session_restart'");
        // TODO [WARNING]: Because the RestartSession branch returns (ShouldBreak: false, ShouldContinue: true),
        // the loop continues without consuming retry budget, so a second agent call fires and emits
        // a second counter measurement with outcome='retry'. This test does not assert that only one
        // session_restart emission occurred (i.e. no Should().HaveCount(1, ...) or negation of 'retry').
        // A double-count scenario would not be detected. Consider asserting the exact count of
        // session_restart emissions equals 1, or using measurements.Where(m => ...).Should().HaveCount(1).
    }

    // ── Exception (catch) path ───────────────────────────────────────────────

    /// <summary>
    /// When <c>ExecuteAgentAndRecordAsync</c> absorbs a non-cancellation exception from the
    /// underlying agent and returns <see langword="null"/>, <c>ClassifyRetryOutcome(null)</c>
    /// maps to <see cref="RetryOutcome.TransientWait"/>, so the counter must emit
    /// <c>outcome="transient"</c>.
    ///
    /// <para>
    /// Note: <c>AgentPhaseExecutor.ExecuteAgentAndRecordAsync</c> absorbs all non-cancellation
    /// exceptions internally and returns <c>null</c> — it never re-throws to
    /// <c>RunFixAgentIterationAsync</c>. In the test we simulate this by returning a null-like
    /// result equivalent: an <see cref="AgentResult"/> with zero tokens and an exit code of 1,
    /// which <c>ClassifyRetryOutcome</c> maps to <see cref="RetryOutcome.TransientWait"/>.
    /// </para>
    ///
    /// <para>
    /// The catch block in <c>RunFixAgentIterationAsync</c> is a defensive guard for future code
    /// paths; it also emits <c>OutcomeTransient</c> for consistency with the null-result contract.
    /// </para>
    /// </summary>
    // TODO [WARNING]: This test exercises the normal ProviderRateLimit → TransientWait switch case,
    // NOT the defensive catch block in RunFixAgentIterationAsync. The catch block's
    // _qualityGateRetries.Add(1, BuildRetryTags(run, OutcomeTransient)) call (RetryLoop.cs ~line 581)
    // is unreachable via IAgentProvider mocks because ExecuteAgentAndRecordAsync absorbs all
    // non-cancellation exceptions internally before they can propagate to RunFixAgentIterationAsync.
    // A revert of the counter emit inside the catch block would not be detected by any test in
    // this file. To cover that path, a seam (e.g. a virtual/internal method or a delegate) would
    // be needed in AgentPhaseExecutor to allow injecting a throwing dependency.
    [Fact]
    public async Task RetryCounter_WhenAgentReturnsNullLikeResult_EmitsOutcomeTransient()
    {
        SetupValidatorAlwaysFails();

        // Simulate the production path: ExecuteAgentAndRecordAsync absorbs an exception and
        // returns null, which ClassifyRetryOutcome maps to RetryOutcome.TransientWait.
        // We use ExitCode=1 with a zero-token ProviderRateLimit result as the closest
        // mockable approximation of what AgentPhaseExecutor returns after absorbing an exception.
        // (The actual null path is internal to AgentPhaseExecutor and not directly injectable
        // through IAgentProvider.)
        using var cts = new CancellationTokenSource();
        var callCount = 0;
        _mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount == 1)
                    // First call: simulate absorbed exception → transient-equivalent result
                    return new AgentResult
                    {
                        ExitCode = 1,
                        OutputLines = ["provider error (absorbed)"],
                        ErrorCategory = AgentErrorCategory.ProviderRateLimit,
                        Usage = new TokenUsage { InputTokens = 0, OutputTokens = 0 }
                    };
                // Cancel after first retry to avoid spinning on TransientRetryDelay
                cts.Cancel();
                return new AgentResult
                {
                    ExitCode = 0,
                    OutputLines = ["done"],
                    Usage = new TokenUsage { InputTokens = 10, OutputTokens = 5 }
                };
            });

        var config = CreateConfig(maxRetries: 5);
        await _executor.ProceedToQualityGatesAsync(BuildContext(config), cts.Token);

        var measurements = _retriesCollector.GetMeasurementSnapshot();
        measurements.Should().NotBeEmpty("the retry loop must have fired at least once");
        measurements.Should().Contain(m =>
            m.Value == 1 &&
            m.Tags.Contains(new KeyValuePair<string, object?>("outcome", "transient")),
            "when ExecuteAgentAndRecordAsync absorbs an exception and returns null, " +
            "ClassifyRetryOutcome(null) → TransientWait must emit outcome='transient'");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PipelineConfiguration CreateConfig(int maxRetries) => new()
    {
        AgentTimeout = TimeSpan.FromMinutes(10),
        MaxRetries = maxRetries,
        StallPollInterval = TimeSpan.FromMilliseconds(50),
        StallWarningInterval = TimeSpan.FromHours(1),
        TransientRetryDelay = TimeSpan.Zero
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

    private void SetupAgentSuccessResponse()
    {
        _mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["Fixed"],
                Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 }
            });
    }

    private void SetupDefaultMocks()
    {
        _mockCallbacks.Setup(c => c.SwapAgentLabel(
                It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.RemoveAllAgentLabels(
                It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.UpdateFileChangeStats(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.CreatePullRequest(
                It.IsAny<PipelineRun>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.FinalizePullRequest(
                It.IsAny<PipelineRun>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(
                It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);

        _mockIssueOps.Setup(o => o.SwapLabelAsync(
                It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = false });
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
            Identifier = "2744",
            Title = "Retry counter dimension test",
            Description = "Test description",
            Labels = new[] { "bug" }
        }
    };
}
