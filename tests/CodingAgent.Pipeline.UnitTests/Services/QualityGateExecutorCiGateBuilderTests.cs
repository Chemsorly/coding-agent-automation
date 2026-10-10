using AwesomeAssertions;
using Moq;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Characterization tests for the GateResult.Details strings produced by
/// <see cref="QualityGateExecutor.AppendExternalCiIfNeededAsync"/> and the post-PR CI path in
/// <see cref="CiPollingCoordinator"/>.<c>WaitForPostPrCiAsync</c> (exercised via
/// <see cref="QualityGateExecutor.ProceedToQualityGatesAsync"/>).
///
/// These tests lock in the exact Details text for pass, fail, timeout, and error arms before
/// the extract-method refactor (issue #2625), so any accidental change to the strings is caught.
/// The static gate-builder methods moved to <see cref="CiPollingCoordinator"/> in issue #2889.
/// </summary>
public class QualityGateExecutorCiGateBuilderTests
{
    // ── Shared fixtures ──────────────────────────────────────────────────────

    private static readonly QualityGateReport PassingLocalReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
    };

    // ── AppendExternalCiIfNeededAsync — pass arm ──────────────────────────────

    [Fact]
    public async Task AppendExternalCiIfNeeded_CiPasses_GateResultDetailsContainsJobCount()
    {
        const int jobCount = 3;
        var ciStatus = new PipelineRunStatus
        {
            State = PipelineRunState.Passed,
            Jobs = Enumerable.Range(0, jobCount)
                .Select(i => new PipelineJobResult { Name = $"job-{i}", State = PipelineRunState.Passed })
                .ToList()
        };

        var (executor, context, mockPipelineProvider, _) = BuildPollingFixture();
        mockPipelineProvider
            .Setup(p => p.WaitForCompletionAsync(
                It.IsAny<BranchName>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ciStatus);

        var result = await executor.AppendExternalCiIfNeededAsync(
            context, PassingLocalReport, allowEmptyCommit: false, CancellationToken.None);

        result.ExternalCi.Should().NotBeNull();
        result.ExternalCi!.Passed.Should().BeTrue();
        result.ExternalCi.GateName.Should().Be("External CI");
        result.ExternalCi.Details.Should().Be($"CI passed. {jobCount} job(s) completed.");
    }

    // ── AppendExternalCiIfNeededAsync — fail arm ──────────────────────────────

    [Fact]
    public async Task AppendExternalCiIfNeeded_CiFails_GateResultDetailsContainsFailureDescription()
    {
        var ciStatus = new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = new List<PipelineJobResult>
            {
                new() { Name = "build", State = PipelineRunState.Failed, FailureReason = "test error" }
            }
        };

        var (executor, context, mockPipelineProvider, _) = BuildPollingFixture();
        mockPipelineProvider
            .Setup(p => p.WaitForCompletionAsync(
                It.IsAny<BranchName>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ciStatus);

        var result = await executor.AppendExternalCiIfNeededAsync(
            context, PassingLocalReport, allowEmptyCommit: false, CancellationToken.None);

        var expectedDetails = QualityGateValidator.BuildCiFailureDetails(ciStatus);

        result.ExternalCi.Should().NotBeNull();
        result.ExternalCi!.Passed.Should().BeFalse();
        result.ExternalCi.GateName.Should().Be("External CI");
        // TODO: The Details assertion below is tautological — it calls the same BuildCiFailureDetails
        // method the production code delegates to, so it would still pass even if BuildCiFailureDetails
        // were replaced with a stub returning an empty string. Replace expectedDetails with a hardcoded
        // string (the actual formatted output for this single-job failure scenario) to make the
        // assertion independently verify the failure message content.
        result.ExternalCi.Details.Should().Be(expectedDetails);
        // TODO: This test calls BuildCiFailureDetails(ciStatus) without ciLogPaths, matching
        // the production call only when ciLogPaths is null. Add a variant that supplies a
        // non-null ciLogPaths dictionary and asserts the Details reflect the log paths, to
        // verify BuildCiGateResult correctly forwards ciLogPaths to BuildCiFailureDetails.
    }

    // ── AppendExternalCiIfNeededAsync — timeout arm ───────────────────────────

    [Fact]
    public async Task AppendExternalCiIfNeeded_CiTimesOut_GateResultDetailsContainsTimeout()
    {
        var timeout = TimeSpan.FromMinutes(5);
        using var innerCts = new CancellationTokenSource();

        var (executor, context, mockPipelineProvider, _) = BuildPollingFixture(externalCiTimeout: timeout);
        // Simulate the inner timeout token firing (not the outer cancellation token)
        mockPipelineProvider
            .Setup(p => p.WaitForCompletionAsync(
                It.IsAny<BranchName>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(innerCts.Token));

        var result = await executor.AppendExternalCiIfNeededAsync(
            context, PassingLocalReport, allowEmptyCommit: false, CancellationToken.None);

        result.ExternalCi.Should().NotBeNull();
        result.ExternalCi!.Passed.Should().BeFalse();
        result.ExternalCi.GateName.Should().Be("External CI");
        result.ExternalCi.Details.Should().Be($"External CI timed out after {timeout}");
    }

    // ── AppendExternalCiIfNeededAsync — error arm ─────────────────────────────

    [Fact]
    public async Task AppendExternalCiIfNeeded_CiThrowsException_GateResultDetailsContainsError()
    {
        const string errorMessage = "CI provider returned 503";

        var (executor, context, mockPipelineProvider, _) = BuildPollingFixture();
        mockPipelineProvider
            .Setup(p => p.WaitForCompletionAsync(
                It.IsAny<BranchName>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException(errorMessage));

        var result = await executor.AppendExternalCiIfNeededAsync(
            context, PassingLocalReport, allowEmptyCommit: false, CancellationToken.None);

        result.ExternalCi.Should().NotBeNull();
        result.ExternalCi!.Passed.Should().BeFalse();
        result.ExternalCi.GateName.Should().Be("External CI");
        result.ExternalCi.Details.Should().Be($"External CI error: {errorMessage}");
    }

    // ── WaitForPostPrCiAsync — pass arm ────────────────────────────────────────

    /// <summary>
    /// When post-PR CI passes, WaitForPostPrCiAsync returns with a passing ExternalCi gate.
    /// Because no second FinalizePullRequest call happens on the success path (only the pre-post-PR
    /// FinalizePullRequest(isDraft=false) fires before CI waits), we verify the Details string via
    /// the EmitOutputLine output emitted by BuildCiGateResult for the pass arm.
    /// </summary>
    [Fact]
    public async Task WaitForPostPrCi_CiPasses_EmitsPassDetailsLine()
    {
        const int jobCount = 2;
        var postPrCiStatus = new PipelineRunStatus
        {
            State = PipelineRunState.Passed,
            Jobs = Enumerable.Range(0, jobCount)
                .Select(i => new PipelineJobResult { Name = $"job-{i}", State = PipelineRunState.Passed })
                .ToList()
        };

        var (executor, context, mockPipelineProvider, mockCallbacks) = BuildPostPrFixture();

        var emittedLines = new List<string>();
        mockCallbacks
            .Setup(c => c.EmitOutputLine(It.IsAny<string>()))
            .Callback<string>(emittedLines.Add);

        // Sequence: pre-PR CI passes (call #1), cleanup CI skipped (no-changes), post-PR CI → our status
        mockPipelineProvider
            .SetupSequence(p => p.WaitForCompletionAsync(
                It.IsAny<BranchName>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Passed, Jobs = [] })  // pre-PR CI
            .ReturnsAsync(postPrCiStatus);                                                         // post-PR CI

        await executor.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // BuildCiGateResult emits "✅ {prefix} passed ({n} jobs)" for the pass arm
        emittedLines.Should().Contain(
            $"✅ Post-PR CI passed ({jobCount} jobs)",
            "BuildCiGateResult must emit the pass status line for the post-PR CI path");
        // TODO: Also assert ExternalCi.Details == "Post-PR CI passed. {jobCount} job(s) completed."
        // for the post-PR CI pass arm. The EmitOutputLine check above only covers uiPrefix; a
        // wrong detailsPrefix ("CI" instead of "Post-PR CI") would still pass this test. The
        // Details assertion requires capturing the QualityGateReport from the FinalizePullRequest
        // call (isDraft=false) or exposing the gate via a returned value on the success path.
    }

    /// <summary>
    /// Supplementary: when post-PR CI passes, the run is finalized as non-draft (isDraft=false),
    /// confirming the flow reaches FinalizePullRequest before the post-PR CI wait starts.
    /// The report passed to that call does not yet have ExternalCi (expected — post-PR CI hasn't run).
    /// </summary>
    [Fact]
    public async Task WaitForPostPrCi_CiPasses_GateResultPassedAndGateName()
    {
        const int jobCount = 2;
        var postPrCiStatus = new PipelineRunStatus
        {
            State = PipelineRunState.Passed,
            Jobs = Enumerable.Range(0, jobCount)
                .Select(i => new PipelineJobResult { Name = $"job-{i}", State = PipelineRunState.Passed })
                .ToList()
        };

        var (executor, context, mockPipelineProvider, mockCallbacks) = BuildPostPrFixture();

        // Sequence: pre-PR CI passes (call #1), cleanup CI skipped (no-changes), post-PR CI → passes
        mockPipelineProvider
            .SetupSequence(p => p.WaitForCompletionAsync(
                It.IsAny<BranchName>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Passed, Jobs = [] })  // pre-PR CI
            .ReturnsAsync(postPrCiStatus);                                                         // post-PR CI

        await executor.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Passing post-PR CI → non-draft finalization (isDraft=false), run completes successfully
        mockCallbacks.Verify(
            c => c.FinalizePullRequest(
                It.IsAny<PipelineRun>(), false, It.IsAny<CancellationToken>()),
            Times.Once,
            "post-PR CI pass must finalize as non-draft");
        mockCallbacks.Verify(
            c => c.FinalizePullRequest(
                It.IsAny<PipelineRun>(), true, It.IsAny<CancellationToken>()),
            Times.Never,
            "post-PR CI pass must NOT finalize as draft");
    }

    // ── WaitForPostPrCiAsync — timeout arm ─────────────────────────────────────

    [Fact]
    public async Task WaitForPostPrCi_CiTimesOut_GateResultDetailsContainsTimeout()
    {
        var timeout = TimeSpan.FromMinutes(5);
        using var innerCts = new CancellationTokenSource();

        var (executor, context, mockPipelineProvider, _) = BuildPostPrFixture(externalCiTimeout: timeout);

        // Sequence: pre-PR CI passes (call #1), cleanup skipped, post-PR CI → inner timeout
        mockPipelineProvider
            .SetupSequence(p => p.WaitForCompletionAsync(
                It.IsAny<BranchName>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Passed, Jobs = [] })  // pre-PR CI
            .ThrowsAsync(new OperationCanceledException(innerCts.Token));                          // post-PR CI times out

        await executor.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // BuildQualityGateErrorSummary includes "External CI: {details}" when ExternalCi failed.
        // FinalizeDraftPrAsync enqueues that summary into run.RetryErrors, so we can verify
        // the Details string without needing a FinalizePullRequest callback.
        var expectedDetails = $"Post-PR CI timed out after {timeout}";
        context.Run.RetryErrors.Should().ContainSingle(
            e => e.Contains($"External CI: {expectedDetails}"),
            "the error summary must contain the timeout details for the post-PR CI gate");
    }

    // ── WaitForPostPrCiAsync — error arm ───────────────────────────────────────

    [Fact]
    public async Task WaitForPostPrCi_CiThrowsException_GateResultDetailsContainsError()
    {
        const string errorMessage = "network failure during post-PR CI poll";

        var (executor, context, mockPipelineProvider, _) = BuildPostPrFixture();

        // Sequence: pre-PR CI passes (call #1), post-PR CI → generic exception
        mockPipelineProvider
            .SetupSequence(p => p.WaitForCompletionAsync(
                It.IsAny<BranchName>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Passed, Jobs = [] })  // pre-PR CI
            .ThrowsAsync(new HttpRequestException(errorMessage));                                  // post-PR CI error

        await executor.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // BuildQualityGateErrorSummary includes "External CI: {details}" when ExternalCi failed.
        // FinalizeDraftPrAsync enqueues that summary into run.RetryErrors, so we can verify
        // the Details string without needing a FinalizePullRequest callback.
        var expectedDetails = $"Post-PR CI error: {errorMessage}";
        context.Run.RetryErrors.Should().ContainSingle(
            e => e.Contains($"External CI: {expectedDetails}"),
            "the error summary must contain the error details for the post-PR CI gate");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds fixtures suitable for calling AppendExternalCiIfNeededAsync directly.
    /// Mirrors the pattern in QualityGateExecutorCiPollingTests.
    /// </summary>
    private static (
        QualityGateExecutor executor,
        QualityGateContext context,
        Mock<IPipelineProvider> mockPipelineProvider,
        Mock<IPipelineCallbacks> mockCallbacks)
        BuildPollingFixture(TimeSpan? externalCiTimeout = null)
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        var mockCallbacks = new Mock<IPipelineCallbacks>();
        var mockIssueOps = new Mock<IAgentIssueOperations>();
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        var mockPipelineProvider = new Mock<IPipelineProvider>();

        mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        mockRepoProvider.Setup(r => r.PushBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-test-abc");

        // GetRunStatusAsync returns Running so WaitForCiRunsToAppearAsync passes through immediately
        mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<BranchName>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Running,
                Jobs = [new() { Name = "build", State = PipelineRunState.Running }]
            });

        mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(
                It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var executor = new QualityGateExecutor(
            new Mock<IQualityGateValidator>().Object,
            new PullRequestOrchestrator(mockLogger.Object),
            new CiLogWriter(mockLogger.Object),
            new FeedbackService(mockLogger.Object),
            mockLogger.Object);

        var run = new PipelineRun
        {
            RunId = "gate-builder-polling",
            IssueIdentifier = "2625",
            IssueTitle = "CI gate builder polling test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-builder-{Guid.NewGuid():N}"),
            BranchName = "feature/auto-2625-polling-test"
        };

        var context = new QualityGateContext
        {
            Run = run,
            Config = new PipelineConfiguration
            {
                AgentTimeout = TimeSpan.FromMinutes(10),
                MaxRetries = 0,
                MaxInfrastructureRetries = 0,
                ExternalCiTimeout = externalCiTimeout ?? TimeSpan.FromMinutes(5),
                CiNotStartedTimeout = TimeSpan.FromMilliseconds(50),
                ExternalCiPollInterval = TimeSpan.FromMilliseconds(50),
                StallPollInterval = TimeSpan.FromMilliseconds(50),
                StallWarningInterval = TimeSpan.FromHours(1)
            },
            AgentProvider = new Mock<IAgentProvider>().Object,
            IssueOps = mockIssueOps.Object,
            Callbacks = mockCallbacks.Object,
            RepoProvider = mockRepoProvider.Object,
            PipelineProvider = mockPipelineProvider.Object,
            QualityGateConfigs = new List<QualityGateConfiguration>()
        };

        return (executor, context, mockPipelineProvider, mockCallbacks);
    }

    /// <summary>
    /// Builds fixtures suitable for exercising the post-PR CI path via
    /// ProceedToQualityGatesAsync. Mirrors the pattern in QualityGateExecutorPostPrCiTests:
    /// validator always passes, first CommitAllAsync succeeds (pre-PR CI commit), second throws
    /// "No changes to commit" (cleanup path → skipCiIfNoChanges skips pre-cleanup CI).
    /// </summary>
    private static (
        QualityGateExecutor executor,
        QualityGateContext context,
        Mock<IPipelineProvider> mockPipelineProvider,
        Mock<IPipelineCallbacks> mockCallbacks)
        BuildPostPrFixture(TimeSpan? externalCiTimeout = null)
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        var mockValidator = new Mock<IQualityGateValidator>();
        var mockCallbacks = new Mock<IPipelineCallbacks>();
        var mockIssueOps = new Mock<IAgentIssueOperations>();
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        var mockPipelineProvider = new Mock<IPipelineProvider>();
        var mockHistoryService = new Mock<IPipelineRunHistoryService>();
        var mockAgent = new Mock<IAgentProvider>();

        // Validator always passes local gates
        mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "ok" },
                Tests = new GateResult { GateName = "Tests", Passed = true, Details = "ok" }
            });

        // First CommitAllAsync succeeds (pre-PR CI commit), second throws "No changes" (cleanup path)
        mockRepoProvider.SetupSequence(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>)
            .ThrowsAsync(new InvalidOperationException("No changes to commit"));
        mockRepoProvider.Setup(r => r.PushBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-postpr-abc");

        // GetRunStatusAsync returns Running so WaitForCiRunsToAppearAsync passes through
        mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<BranchName>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Running,
                Jobs = [new() { Name = "build", State = PipelineRunState.Running }]
            });

        mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(
                It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockCallbacks.Setup(c => c.SwapAgentLabel(
                It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockCallbacks.Setup(c => c.RemoveAllAgentLabels(
                It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockCallbacks.Setup(c => c.AddRunToHistoryAsync(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);
        mockIssueOps.Setup(o => o.SwapLabelAsync(
                It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());
        mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = false });
        mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = ["done"],
                Usage = new TokenUsage { InputTokens = 10, OutputTokens = 5 }
            });

        var executor = new QualityGateExecutor(
            mockValidator.Object,
            new PullRequestOrchestrator(mockLogger.Object),
            new CiLogWriter(mockLogger.Object),
            new FeedbackService(mockLogger.Object),
            mockLogger.Object,
            mockHistoryService.Object);

        var run = new PipelineRun
        {
            RunId = "gate-builder-postpr",
            IssueIdentifier = "2625",
            IssueTitle = "Post-PR CI gate builder test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-postpr-builder-{Guid.NewGuid():N}"),
            BranchName = "feature/auto-2625-postpr-test"
        };

        var context = new QualityGateContext
        {
            Run = run,
            Config = new PipelineConfiguration
            {
                AgentTimeout = TimeSpan.FromMinutes(10),
                MaxRetries = 0,
                MaxInfrastructureRetries = 0,
                ExternalCiTimeout = externalCiTimeout ?? TimeSpan.FromMinutes(5),
                CiNotStartedTimeout = TimeSpan.FromMilliseconds(50),
                ExternalCiPollInterval = TimeSpan.FromMilliseconds(50),
                StallPollInterval = TimeSpan.FromMilliseconds(50),
                StallWarningInterval = TimeSpan.FromHours(1)
            },
            AgentProvider = mockAgent.Object,
            IssueOps = mockIssueOps.Object,
            Callbacks = mockCallbacks.Object,
            RepoProvider = mockRepoProvider.Object,
            PipelineProvider = mockPipelineProvider.Object,
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
                Identifier = "2625",
                Title = "CI gate builder test",
                Description = "Test description",
                Labels = new[] { "refactor" }
            }
        };

        return (executor, context, mockPipelineProvider, mockCallbacks);
    }
}

/// <summary>
/// Characterization tests for the three terminal Build* report methods in
/// <see cref="QualityGateExecutor"/>: <c>BuildConflictRestartReport</c>,
/// <c>BuildPrMergedReport</c>, and <c>BuildPrClosedReport</c>.
///
/// These tests lock in the exact <c>ExternalCi.Passed</c> flag and <c>ExternalCi.Details</c>
/// string for each terminal path, and verify that <c>Compilation</c> and <c>Tests</c> from
/// the input report are passed through unchanged. Added as a prerequisite for the
/// extract-method refactor in issue #3521 so any accidental change to the Passed flag or
/// Details strings is caught immediately.
///
/// Each test triggers a terminal branch via <c>AppendExternalCiIfNeededAsync</c> (the
/// Build* methods are private static and cannot be called directly).
/// </summary>
public class QualityGateExecutorTerminalReportTests
{
    // ── Shared input report with distinct Compilation/Tests values ───────────────────────────

    /// <summary>
    /// Input report with distinctive non-default values so passthrough assertions are meaningful.
    /// </summary>
    private static readonly QualityGateReport InputReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "build-ok-sentinel" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "test-ok-sentinel" }
    };

    // ── ConflictRestart: ExternalCi shape ────────────────────────────────────────────────────

    /// <summary>
    /// BuildConflictRestartReport must set ExternalCi.Passed = false and the exact Details string.
    /// </summary>
    [Fact]
    public async Task AppendExternalCiIfNeeded_WhenConflictRestart_ExternalCiIsFalseWithCorrectDetails()
    {
        var (executor, context) = BuildConflictRestartFixture("42");

        var result = await executor.AppendExternalCiIfNeededAsync(
            context, InputReport, allowEmptyCommit: false, CancellationToken.None);

        result.ExternalCi.Should().NotBeNull();
        result.ExternalCi!.Passed.Should().BeFalse("conflict restart must mark the gate as failed");
        result.ExternalCi.GateName.Should().Be("External CI");
        result.ExternalCi.Details.Should().Be(
            "Conflict restart — PR conflicted with main; re-dispatched as agent:next",
            "exact Details string must be preserved by any refactor");
    }

    /// <summary>
    /// BuildConflictRestartReport must pass Compilation and Tests through from the input report unchanged.
    /// </summary>
    [Fact]
    public async Task AppendExternalCiIfNeeded_WhenConflictRestart_PreservesCompilationAndTests()
    {
        var (executor, context) = BuildConflictRestartFixture("42");

        var result = await executor.AppendExternalCiIfNeededAsync(
            context, InputReport, allowEmptyCommit: false, CancellationToken.None);

        result.Compilation.Should().BeSameAs(InputReport.Compilation,
            "Compilation must be the same object reference passed through from the input report");
        result.Tests.Should().BeSameAs(InputReport.Tests,
            "Tests must be the same object reference passed through from the input report");
    }

    // ── PrMerged: ExternalCi shape ───────────────────────────────────────────────────────────

    /// <summary>
    /// BuildPrMergedReport must set ExternalCi.Passed = true and the exact Details string.
    /// </summary>
    [Fact]
    public async Task AppendExternalCiIfNeeded_WhenPrMerged_ExternalCiIsTrueWithCorrectDetails()
    {
        var (executor, context) = BuildPrMergedFixture("42");

        var result = await executor.AppendExternalCiIfNeededAsync(
            context, InputReport, allowEmptyCommit: false, CancellationToken.None);

        result.ExternalCi.Should().NotBeNull();
        result.ExternalCi!.Passed.Should().BeTrue("PR-merged path must mark the gate as passed");
        result.ExternalCi.GateName.Should().Be("External CI");
        result.ExternalCi.Details.Should().Be(
            "PR was merged — run ended Succeeded",
            "exact Details string must be preserved by any refactor");
    }

    /// <summary>
    /// BuildPrMergedReport must pass Compilation and Tests through from the input report unchanged.
    /// </summary>
    [Fact]
    public async Task AppendExternalCiIfNeeded_WhenPrMerged_PreservesCompilationAndTests()
    {
        var (executor, context) = BuildPrMergedFixture("42");

        var result = await executor.AppendExternalCiIfNeededAsync(
            context, InputReport, allowEmptyCommit: false, CancellationToken.None);

        result.Compilation.Should().BeSameAs(InputReport.Compilation,
            "Compilation must be the same object reference passed through from the input report");
        result.Tests.Should().BeSameAs(InputReport.Tests,
            "Tests must be the same object reference passed through from the input report");
    }

    // ── PrClosed: ExternalCi shape ───────────────────────────────────────────────────────────

    /// <summary>
    /// BuildPrClosedReport must set ExternalCi.Passed = false and the exact Details string.
    /// </summary>
    [Fact]
    public async Task AppendExternalCiIfNeeded_WhenPrClosed_ExternalCiIsFalseWithCorrectDetails()
    {
        var (executor, context) = BuildPrClosedFixture("42");

        var result = await executor.AppendExternalCiIfNeededAsync(
            context, InputReport, allowEmptyCommit: false, CancellationToken.None);

        result.ExternalCi.Should().NotBeNull();
        result.ExternalCi!.Passed.Should().BeFalse("PR-closed path must mark the gate as failed");
        result.ExternalCi.GateName.Should().Be("External CI");
        result.ExternalCi.Details.Should().Be(
            "PR was closed without merging — run ended Cancelled",
            "exact Details string must be preserved by any refactor");
    }

    /// <summary>
    /// BuildPrClosedReport must pass Compilation and Tests through from the input report unchanged.
    /// </summary>
    [Fact]
    public async Task AppendExternalCiIfNeeded_WhenPrClosed_PreservesCompilationAndTests()
    {
        var (executor, context) = BuildPrClosedFixture("42");

        var result = await executor.AppendExternalCiIfNeededAsync(
            context, InputReport, allowEmptyCommit: false, CancellationToken.None);

        result.Compilation.Should().BeSameAs(InputReport.Compilation,
            "Compilation must be the same object reference passed through from the input report");
        result.Tests.Should().BeSameAs(InputReport.Tests,
            "Tests must be the same object reference passed through from the input report");
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a fixture that triggers <c>BuildConflictRestartReport</c>: CI never starts
    /// (GetRunStatusAsync returns Pending) and <c>IsPullRequestBehindBaseAsync</c> returns
    /// <c>Conflicted</c> on the first poll attempt. Pattern mirrors
    /// <c>QualityGateExecutorConflictRestartPollTests</c>.
    /// </summary>
    private static (QualityGateExecutor executor, QualityGateContext context)
        BuildConflictRestartFixture(string prNumber)
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        var mockCallbacks = new Mock<IPipelineCallbacks>();
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        var mockPipelineProvider = new Mock<IPipelineProvider>();

        SetupCommonRepoMocks(mockRepoProvider);
        mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(
                It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockCallbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()));

        // CI never starts
        mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<BranchName>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = [] });

        // PR is conflicted — triggers BuildConflictRestartReport
        var prNum = int.Parse(prNumber);
        mockRepoProvider.Setup(r => r.IsPullRequestBehindBaseAsync(prNum, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrMergeabilityStatus.Conflicted);

        var executor = new QualityGateExecutor(
            new Mock<IQualityGateValidator>().Object,
            new PullRequestOrchestrator(mockLogger.Object),
            new CiLogWriter(mockLogger.Object),
            new FeedbackService(mockLogger.Object),
            mockLogger.Object);

        var context = BuildContext(prNumber, mockCallbacks, mockRepoProvider, mockPipelineProvider);
        return (executor, context);
    }

    /// <summary>
    /// Builds a fixture that triggers <c>BuildPrMergedReport</c>: CI never starts
    /// (GetRunStatusAsync returns Pending) and <c>GetPullRequestStateAsync</c> returns
    /// <c>Merged</c>. Pattern mirrors <c>CiPollingCoordinatorTests.PrMergedMidLoop_*</c>.
    /// </summary>
    private static (QualityGateExecutor executor, QualityGateContext context)
        BuildPrMergedFixture(string prNumber)
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        var mockCallbacks = new Mock<IPipelineCallbacks>();
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        var mockPipelineProvider = new Mock<IPipelineProvider>();

        SetupCommonRepoMocks(mockRepoProvider);
        mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(
                It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // CI never starts
        mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<BranchName>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = [] });

        // PR is merged — triggers BuildPrMergedReport
        var prNum = int.Parse(prNumber);
        mockRepoProvider.Setup(r => r.GetPullRequestStateAsync(prNum, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PullRequestState.Merged);

        var executor = new QualityGateExecutor(
            new Mock<IQualityGateValidator>().Object,
            new PullRequestOrchestrator(mockLogger.Object),
            new CiLogWriter(mockLogger.Object),
            new FeedbackService(mockLogger.Object),
            mockLogger.Object);

        var context = BuildContext(prNumber, mockCallbacks, mockRepoProvider, mockPipelineProvider);
        return (executor, context);
    }

    /// <summary>
    /// Builds a fixture that triggers <c>BuildPrClosedReport</c>: CI never starts
    /// (GetRunStatusAsync returns Pending) and <c>GetPullRequestStateAsync</c> returns
    /// <c>Closed</c>. Pattern mirrors <c>CiPollingCoordinatorTests.PrClosedMidLoop_*</c>.
    /// </summary>
    private static (QualityGateExecutor executor, QualityGateContext context)
        BuildPrClosedFixture(string prNumber)
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        var mockCallbacks = new Mock<IPipelineCallbacks>();
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        var mockPipelineProvider = new Mock<IPipelineProvider>();

        SetupCommonRepoMocks(mockRepoProvider);
        mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(
                It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // CI never starts
        mockPipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<BranchName>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus { State = PipelineRunState.Pending, Jobs = [] });

        // PR is closed — triggers BuildPrClosedReport
        var prNum = int.Parse(prNumber);
        mockRepoProvider.Setup(r => r.GetPullRequestStateAsync(prNum, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PullRequestState.Closed);

        var executor = new QualityGateExecutor(
            new Mock<IQualityGateValidator>().Object,
            new PullRequestOrchestrator(mockLogger.Object),
            new CiLogWriter(mockLogger.Object),
            new FeedbackService(mockLogger.Object),
            mockLogger.Object);

        var context = BuildContext(prNumber, mockCallbacks, mockRepoProvider, mockPipelineProvider);
        return (executor, context);
    }

    /// <summary>
    /// Sets up the mock repository provider with the minimum stubs required for
    /// <c>AppendExternalCiIfNeededAsync</c> to reach the CI polling loop.
    /// </summary>
    private static void SetupCommonRepoMocks(Mock<IRepositoryProvider> mockRepoProvider)
    {
        // TODO: Only the 5-parameter CommitAllAsync overload is stubbed here. The 6-parameter
        // allowEmpty overload (used in PollCiWithNotStartedRetryAsync for empty re-trigger commits)
        // is not stubbed. This is safe today because all fixtures set CiNotStartedMaxRetries = 0,
        // which prevents the retry loop from firing. If that config default ever changes, Moq will
        // return null for the unmatched overload, causing a NullReferenceException instead of a
        // clear test failure. Consider adding a stub for the 6-parameter overload here to make
        // the fixture robust to future config changes.
        mockRepoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        mockRepoProvider.Setup(r => r.PushBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockRepoProvider.Setup(r => r.GetHeadCommitShaAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-terminal-test");
    }

    private static QualityGateContext BuildContext(
        string prNumber,
        Mock<IPipelineCallbacks> mockCallbacks,
        Mock<IRepositoryProvider> mockRepoProvider,
        Mock<IPipelineProvider> mockPipelineProvider)
    {
        var run = new PipelineRun
        {
            RunId = "terminal-report-test",
            IssueIdentifier = "3521",
            IssueTitle = "Terminal report characterization test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-terminal-{Guid.NewGuid():N}"),
            BranchName = "feature/auto-3521-terminal-test",
            PullRequestNumber = prNumber
        };

        return new QualityGateContext
        {
            Run = run,
            Config = new PipelineConfiguration
            {
                AgentTimeout = TimeSpan.FromMinutes(10),
                MaxRetries = 0,
                MaxInfrastructureRetries = 0,
                CiNotStartedTimeout = TimeSpan.FromMilliseconds(1),
                CiNotStartedMaxRetries = 0,
                ExternalCiPollInterval = TimeSpan.FromMilliseconds(5),
                ExternalCiTimeout = TimeSpan.FromMinutes(5),
                StallPollInterval = TimeSpan.FromMilliseconds(50),
                StallWarningInterval = TimeSpan.FromHours(1)
            },
            AgentProvider = new Mock<IAgentProvider>().Object,
            IssueOps = new Mock<IAgentIssueOperations>().Object,
            Callbacks = mockCallbacks.Object,
            RepoProvider = mockRepoProvider.Object,
            PipelineProvider = mockPipelineProvider.Object,
            QualityGateConfigs = new List<QualityGateConfiguration>()
        };
    }
}
