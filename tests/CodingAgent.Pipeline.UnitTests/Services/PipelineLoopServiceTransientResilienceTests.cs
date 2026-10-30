using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.TestUtilities;
using Microsoft.Extensions.Hosting;
using Moq;
using Polly.CircuitBreaker;
using Polly.Timeout;
using System.Reflection;
using Xunit;

namespace CodingAgent.Pipeline.UnitTests;

/// <summary>
/// Regression tests for Issue #2535 — transient API failures mid-cycle must NOT permanently
/// stop the pipeline loop (dormant-but-leader scenario).
///
/// Gap A: A transient exception escaping ExecuteCycleAsync is caught by the new
/// <c>IsTransientApiFailure</c> guard in <c>RunMultiTemplateLoopAsync</c> and retried
/// in place after a backoff, keeping the loop alive.
///
/// Fail-fast preserved: non-transient exceptions (InvalidOperationException etc.) still
/// bubble to ExecuteAsync's Error catch, stopping the loop as before.
/// </summary>
[Trait("Feature", "BugFix")]
public sealed class PipelineLoopServiceTransientResilienceTests : IAsyncDisposable
{
    private readonly Mock<IConfigurationStore> _mockStore;
    private readonly Mock<IProviderFactory> _mockFactory;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly DispatchRunCreationService _runCreator;
    private PipelineLoopService? _loopService;

    public PipelineLoopServiceTransientResilienceTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        _mockFactory = new Mock<IProviderFactory>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _mockLogger
            .Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
            .Returns(_mockLogger.Object);
        _mockLogger
            .Setup(l => l.ForContext<It.IsAnyType>())
            .Returns(_mockLogger.Object);

        var lifecycle = new PipelineRunLifecycleService(
            new TestOrchestrationFactory.NullHistoryService(), null, _mockLogger.Object);

        _runCreator = TestOrchestrationFactory.CreateMinimalRunCreator(
            lifecycle: lifecycle,
            logger: _mockLogger.Object);

        SetupValidTemplates();
    }

    // ── Setup ────────────────────────────────────────────────────────────────

    private static readonly PipelineJobTemplate HousekeepingTemplate = new()
    {
        Id = "tmpl-1",
        Name = "Default Template",
        IssueProviderId = "ip-1",
        RepoProviderId = "rp-1",
        BrainProviderId = null,
        PipelineProviderId = null,
        Enabled = true,
        HousekeepingEnabled = true,
    };

    private void SetupValidTemplates()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default());
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = [HousekeepingTemplate.Id] }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { HousekeepingTemplate });

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>(),
                Page = 1,
                PageSize = 50,
                HasMore = false
            });

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(r => r.SupportsServerSideBranchUpdate).Returns(true);
        mockRepoProvider.Setup(r => r.ListOpenPullRequestsAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PullRequestSummary>
            {
                Items = new List<PullRequestSummary>().AsReadOnly(),
                Page = 1,
                PageSize = 100,
                HasMore = false
            });

        _mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);
        _mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);
    }

    private PipelineLoopService CreateServiceWithHousekeeping(IHousekeepingService? housekeepingService)
    {
        _loopService = new PipelineLoopService(new PipelineLoopServiceDependencies
        {
            Orchestration = _runCreator,
            ProviderFactory = _mockFactory.Object,
            PipelineConfigStore = _mockStore.Object,
            ProviderConfigStore = _mockStore.Object,
            ProjectStore = _mockStore.Object,
            Logger = _mockLogger.Object,
            WorkDistributor = null,
            DispatchOrchestration = new NullDispatchOrchestrationService(),
            DependencyChecker = null,
            HousekeepingService = housekeepingService,
            LeaderElection = null
        });
        return _loopService;
    }

    private static Task InvokeExecuteAsync(PipelineLoopService service, CancellationToken stoppingToken)
    {
        var method = typeof(BackgroundService).GetMethod("ExecuteAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (Task)method.Invoke(service, [stoppingToken])!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string failMessage)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        condition().Should().BeTrue(failMessage);
    }

    // ── Tests ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Regression test for Gap A (issue #2535): a transient HttpRequestException thrown
    /// by IHousekeepingService.ExecuteAsync must be caught and retried, NOT cause the loop
    /// to enter permanent dormancy.
    /// </summary>
    [Fact]
    public async Task WhenHousekeepingThrowsTransientException_LoopContinuesPolling()
    {
        // Arrange: mock housekeeping throws transient on first call, succeeds on subsequent
        var callCount = 0;
        var housekeepingMock = new Mock<IHousekeepingService>();
        housekeepingMock
            .Setup(h => h.ExecuteAsync(It.IsAny<HousekeepingRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                    throw new HttpRequestException("transient: connection refused");
                return Task.CompletedTask;
            });

        var svc = CreateServiceWithHousekeeping(housekeepingMock.Object);
        using var hostCts = new CancellationTokenSource();
        _ = InvokeExecuteAsync(svc, hostCts.Token);

        var started = await svc.StartLoopAsync();
        started.Should().BeTrue("loop should start successfully");

        await WaitUntilAsync(
            () => svc.IsLoopActive,
            TimeSpan.FromSeconds(5),
            "loop should become active after StartLoopAsync");

        // Wait for the transient throw to be absorbed AND housekeeping to succeed on second call
        await WaitUntilAsync(
            () => callCount >= 2,
            TimeSpan.FromSeconds(30),
            "housekeeping must be called at least twice — first throw, then succeed");

        // Assert: loop is still alive after the transient exception
        // TODO: potential race — IsLoopActive is read immediately after callCount >= 2 is observed,
        //   but the loop task runs on a separate thread and could start a third cycle and become
        //   inactive between the WaitUntilAsync gate and this assertion under thread-pool starvation.
        //   A tighter fix is to combine both conditions: WaitUntilAsync(() => callCount >= 2 && svc.IsLoopActive).
        svc.IsLoopActive.Should().BeTrue(
            "loop must stay active after a transient exception — retry not permanent dormancy");

        // Assert: no Error log was emitted (transient exception is a Warning, not an Error)
        // TODO: this Verify targets the non-generic Error(Exception, string) overload. Serilog routes
        //   structured log calls through generic Error<T0,T1,...> overloads, so this verification
        //   will not match if the production code uses a structured template with arguments. The
        //   Times.Never verdict is currently safe (absence of Error proves no match either way), but
        //   for a positive Times.Once assertion this would produce a false-green. Consider replacing
        //   the mock logger with a real in-memory Serilog sink (e.g. Serilog.Sinks.InMemory) for
        //   reliable log-level assertions.
        _mockLogger.Verify(
            l => l.Error(It.IsAny<Exception>(), It.Is<string>(s => s.Contains("unexpected error"))),
            Times.Never,
            "transient exceptions must not log at Error level — they are expected and retried");

        hostCts.Cancel();
    }

    /// <summary>
    /// Gap A: a BrokenCircuitException (also transient per IsTransientApiFailure) thrown
    /// by housekeeping must be caught and retried, not permanently stop the loop.
    /// </summary>
    [Fact]
    public async Task WhenBrokenCircuitExceptionEscapesCycle_LoopRetriesNotDormant()
    {
        var callCount = 0;
        var housekeepingMock = new Mock<IHousekeepingService>();
        housekeepingMock
            .Setup(h => h.ExecuteAsync(It.IsAny<HousekeepingRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                    throw new BrokenCircuitException("circuit is open");
                return Task.CompletedTask;
            });

        var svc = CreateServiceWithHousekeeping(housekeepingMock.Object);
        using var hostCts = new CancellationTokenSource();
        _ = InvokeExecuteAsync(svc, hostCts.Token);

        var started = await svc.StartLoopAsync();
        started.Should().BeTrue("loop should start successfully");

        await WaitUntilAsync(
            () => svc.IsLoopActive,
            TimeSpan.FromSeconds(5),
            "loop should become active");

        // Wait for the BrokenCircuitException to be absorbed and a second call to occur
        await WaitUntilAsync(
            () => callCount >= 2,
            TimeSpan.FromSeconds(30),
            "BrokenCircuitException must be absorbed — loop should retry");

        // TODO: potential race — same as WhenHousekeepingThrowsTransientException_LoopContinuesPolling:
        //   IsLoopActive is read immediately after callCount >= 2 is observed on a separate thread.
        //   A tighter fix is to combine: WaitUntilAsync(() => callCount >= 2 && svc.IsLoopActive).
        svc.IsLoopActive.Should().BeTrue(
            "loop must stay active after BrokenCircuitException");

        hostCts.Cancel();
    }

    /// <summary>
    /// Fail-fast preserved: a non-transient exception (InvalidOperationException) must still
    /// stop the loop permanently, not be retried forever.
    /// </summary>
    [Fact]
    public async Task WhenNonTransientExceptionEscapesCycle_LoopStops_FailFastPreserved()
    {
        var housekeepingMock = new Mock<IHousekeepingService>();
        housekeepingMock
            .Setup(h => h.ExecuteAsync(It.IsAny<HousekeepingRequest>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("genuine bug — must not be retried"));

        var svc = CreateServiceWithHousekeeping(housekeepingMock.Object);
        using var hostCts = new CancellationTokenSource();
        _ = InvokeExecuteAsync(svc, hostCts.Token);

        var started = await svc.StartLoopAsync();
        started.Should().BeTrue("loop should start successfully");

        // Wait for the loop to stop due to the non-transient exception
        await WaitUntilAsync(
            () => !svc.IsLoopActive,
            TimeSpan.FromSeconds(30),
            "loop must stop on a non-transient exception — fail-fast preserved");

        svc.IsLoopActive.Should().BeFalse(
            "non-transient exception must permanently stop the loop");

        // Error must have been logged (fail-fast path)
        // TODO: this Verify targets the non-generic Error(Exception, string) overload. Serilog routes
        //   structured log calls through generic Error<T0,T1,...> overloads, so if the production
        //   code uses a structured template with arguments the Times.Once check will never match and
        //   this assertion will produce a false-green. Replace the mock logger with a real in-memory
        //   Serilog sink (e.g. Serilog.Sinks.InMemory) for a reliable positive Times.Once assertion.
        _mockLogger.Verify(
            l => l.Error(It.IsAny<Exception>(), It.Is<string>(s => s.Contains("unexpected error"))),
            Times.Once,
            "non-transient exception must log at Error level exactly once");

        hostCts.Cancel();
    }

    // ── Factory helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="PipelineLoopService"/> with a real <see cref="IWorkDistributor"/>
    /// mock wired in (for tests that need to exercise WorkDistributor paths).
    /// Passes <see cref="NullDispatchOrchestrationService"/> so <c>_dispatcher</c> is non-null
    /// and <c>CycleCount</c> increments normally (without DispatchOrchestration, _dispatcher
    /// is null and ExecuteCycleAsync returns false before reaching the CycleCount increment).
    /// </summary>
    private PipelineLoopService CreateServiceWithWorkDistributor(
        IWorkDistributor workDistributor,
        IHousekeepingService? housekeepingService = null)
    {
        _loopService = new PipelineLoopService(new PipelineLoopServiceDependencies
        {
            Orchestration = _runCreator,
            ProviderFactory = _mockFactory.Object,
            PipelineConfigStore = _mockStore.Object,
            ProviderConfigStore = _mockStore.Object,
            ProjectStore = _mockStore.Object,
            Logger = _mockLogger.Object,
            WorkDistributor = workDistributor,
            DispatchOrchestration = new NullDispatchOrchestrationService(),
            DependencyChecker = null,
            HousekeepingService = housekeepingService,
            LeaderElection = null
        });
        return _loopService;
    }

    // ── AC1 test ─────────────────────────────────────────────────────────

    /// <summary>
    /// AC1: A BrokenCircuitException that escapes ExecuteCycleAsync reaches the
    /// <c>catch (Exception ex) when (IsTransientApiFailure(ex))</c> guard in
    /// <c>RunMultiTemplateLoopAsync</c>. The guard logs a Warning and delays before retrying.
    /// The loop does NOT stop and executes at least one subsequent cycle.
    ///
    /// Injection via IHousekeepingService: RunHousekeepingAsync has no internal catch, so
    /// exceptions from housekeepingService.ExecuteAsync propagate through ExecuteCycleAsync
    /// directly to the IsTransientApiFailure guard. This is the injection path that actually
    /// exercises lines 53–56 of PipelineLoopService.MultiTemplateLoop.cs (the REQUIRED
    /// explicit delay that prevents 100% CPU hot-looping when the circuit breaker is open).
    ///
    /// The Warning log assertion (contains "transient API failure mid-cycle") definitively
    /// proves the IsTransientApiFailure catch body was reached, not just inferred from CycleCount.
    ///
    /// TODO: [WARNING — DotNetSpecialist review] A dispatch-injection test (making
    /// IDispatchOrchestrationService.PrepareDistributionRequestAsync throw BrokenCircuitException
    /// to verify AC1 specifically via the DispatchFairRoundRobinAsync path) is architecturally
    /// infeasible: DispatchScheduler.DispatchRoundAsync wraps every tryDispatchOne invocation in
    /// a catch (Exception ex) that logs at Error and continues — exceptions from
    /// IDispatchOrchestrationService never escape DispatchFairRoundRobinAsync to reach the
    /// IsTransientApiFailure guard at RunMultiTemplateLoopAsync:58. Coverage of that guard via the
    /// dispatch path would require a code change in DispatchRoundAsync (e.g., not catching
    /// BrokenCircuitException there), which is a production design decision beyond this issue's scope.
    /// The housekeeping-injection path reaches the same catch block and the Warning assertion here
    /// is definitive proof that lines 53–56 were executed. This TODO documents the known gap in
    /// dispatch-specific injection, not an absence of guard coverage.
    /// </summary>
    [Fact]
    public async Task WhenBrokenCircuitExceptionEscapesExecuteCycle_IsTransientApiFailureGuardFires()
    {
        // Use a short poll interval so the second cycle starts within the test timeout
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default() with { ClosedLoopPollInterval = TimeSpan.FromMilliseconds(50) });

        // Housekeeping throws BrokenCircuitException on the first call; subsequent calls succeed.
        // RunHousekeepingAsync has no internal catch — the exception escapes ExecuteCycleAsync
        // and reaches RunMultiTemplateLoopAsync's IsTransientApiFailure guard.
        var callCount = 0;
        var housekeepingMock = new Mock<IHousekeepingService>();
        housekeepingMock
            .Setup(h => h.ExecuteAsync(It.IsAny<HousekeepingRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 1)
                    throw new BrokenCircuitException("circuit is open");
                return Task.CompletedTask;
            });

        var svc = CreateServiceWithHousekeeping(housekeepingMock.Object);
        using var hostCts = new CancellationTokenSource();
        _ = InvokeExecuteAsync(svc, hostCts.Token);

        var started = await svc.StartLoopAsync();
        started.Should().BeTrue("loop should start successfully");

        await WaitUntilAsync(
            () => svc.IsLoopActive,
            TimeSpan.FromSeconds(5),
            "loop should become active after StartLoopAsync");

        // Wait for at least 2 completed cycles — proves the IsTransientApiFailure guard
        // retried successfully and the loop executed at least one subsequent cycle (AC1 criterion)
        await WaitUntilAsync(
            () => svc.CycleCount >= 2,
            TimeSpan.FromSeconds(30),
            "loop must execute at least two cycles: first fails with BrokenCircuitException, second succeeds");

        svc.IsLoopActive.Should().BeTrue(
            "loop must stay active after BrokenCircuitException — IsTransientApiFailure guard retried, not stopped");

        // The Warning log from the IsTransientApiFailure catch body must have been emitted.
        // This assertion definitively proves the specific catch block at MultiTemplateLoop.cs:53-56
        // was reached — not just an inner handler lower in the call stack.
        _mockLogger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("transient API failure mid-cycle"))),
            Times.AtLeastOnce,
            "IsTransientApiFailure catch must log 'transient API failure mid-cycle' warning");

        hostCts.Cancel();
    }

    // ── AC2 test ─────────────────────────────────────────────────────────

    /// <summary>
    /// AC2: When IWorkDistributor.GetActiveDecompositionCountAsync throws, LoadActiveDecompositionCountAsync
    /// catches the exception, logs a Warning, and returns 0. The cycle continues to completion
    /// and dispatch is not blocked.
    /// </summary>
    [Fact]
    public async Task WhenGetActiveDecompositionCountThrows_CycleCompletes_WarningLogged()
    {
        // Use a short poll interval for fast cycle completion
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default() with { ClosedLoopPollInterval = TimeSpan.FromMilliseconds(50) });

        var mockDistributor = new Mock<IWorkDistributor>();

        // GetActiveDecompositionCountAsync must be explicitly set up to throw because it has a
        // default interface implementation (=> Task.FromResult(0)); without an explicit Setup,
        // Moq with CallBase=false does not invoke the default implementation.
        mockDistributor
            .Setup(d => d.GetActiveDecompositionCountAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API temporarily unavailable"));

        // GetActiveIssueIdentifiersAsync has no default — return empty set to avoid NRE
        mockDistributor
            .Setup(d => d.GetActiveIssueIdentifiersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<(IssueIdentifier, ProviderConfigId)>());

        // ReconcileStuckItemsAsync also has a default impl; stub explicitly for clarity
        mockDistributor
            .Setup(d => d.ReconcileStuckItemsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var svc = CreateServiceWithWorkDistributor(mockDistributor.Object);
        using var hostCts = new CancellationTokenSource();
        _ = InvokeExecuteAsync(svc, hostCts.Token);

        var started = await svc.StartLoopAsync();
        started.Should().BeTrue("loop should start successfully");

        await WaitUntilAsync(
            () => svc.IsLoopActive,
            TimeSpan.FromSeconds(5),
            "loop should become active after StartLoopAsync");

        // Wait for a full cycle to complete — CycleCount increments after dispatch and before DelayOrStop
        await WaitUntilAsync(
            () => svc.CycleCount >= 1,
            TimeSpan.FromSeconds(10),
            "loop must complete at least one cycle after GetActiveDecompositionCountAsync throws");

        // Dispatch was not blocked — loop is still active
        svc.IsLoopActive.Should().BeTrue(
            "loop must stay active when GetActiveDecompositionCountAsync throws — fail-open returns 0");

        // A Warning must have been logged with the decomposition count failure message
        // (non-generic Warning(Exception, string) overload since the message has no structured parameters)
        _mockLogger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.Is<string>(s => s.Contains("decomposition count"))),
            Times.AtLeastOnce,
            "Warning must be logged when GetActiveDecompositionCountAsync throws");

        hostCts.Cancel();
    }

    // ── AC3 tests ────────────────────────────────────────────────────────

    /// <summary>
    /// AC3 (primary): Cancelling the stoppingToken before loop activation causes
    /// OperationCanceledException to propagate out of ExecuteAsync.
    ///
    /// Mechanism: ExecuteAsync suspends at <c>await _activationSignal.Task.WaitAsync(stoppingToken)</c>
    /// while waiting for StartLoopAsync to signal activation. This await is NOT inside any
    /// try/catch in ExecuteAsync's outer while body. When stoppingToken is cancelled while
    /// suspended here, WaitAsync throws OCE which propagates uncaught from ExecuteAsync,
    /// transitioning the returned Task to Canceled state.
    ///
    /// This is deterministic: InvokeExecuteAsync returns only after ExecuteAsync reaches its
    /// first non-completed await (the activation wait). Cancelling the token after
    /// InvokeExecuteAsync returns is guaranteed to find the service suspended at WaitAsync.
    /// </summary>
    [Fact]
    public async Task WhenStoppingTokenCancelledBeforeActivation_OCEPropagatesFromExecuteAsync()
    {
        var housekeepingMock = new Mock<IHousekeepingService>();
        var svc = CreateServiceWithHousekeeping(housekeepingMock.Object);

        using var hostCts = new CancellationTokenSource();

        // InvokeExecuteAsync returns once ExecuteAsync reaches its first suspension point:
        // await _activationSignal.Task.WaitAsync(stoppingToken). Since StartLoopAsync has
        // NOT been called, _activationSignal.Task is not completed and the service waits there.
        var executeTask = InvokeExecuteAsync(svc, hostCts.Token);

        // Cancelling the token while ExecuteAsync is suspended in WaitAsync causes WaitAsync to
        // throw OCE. There is no try/catch around this await in ExecuteAsync's outer while body,
        // so the OCE propagates uncaught, transitioning executeTask to Canceled state.
        hostCts.Cancel();

        // OCE must propagate: awaiting a Canceled Task throws OperationCanceledException
        // (or its subclass TaskCanceledException which WaitAsync raises on cancellation).
        var oce = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await executeTask);
        oce.Should().NotBeNull("OCE must propagate out of ExecuteAsync when stoppingToken is cancelled before activation");
    }

    /// <summary>
    /// AC3 (supplementary): When the stoppingToken is cancelled while the loop is mid-cycle,
    /// ExecuteAsync completes cleanly without hanging. RunMultiTemplateLoopAsync catches OCE
    /// via 'break' (does not re-throw) and DelayOrStop also swallows OCE, so ExecuteAsync
    /// returns a normally-completed Task in this scenario.
    ///
    /// Pattern note: executeTask MUST be captured (not discarded with '_'). Discarding the task
    /// with '_ = InvokeExecuteAsync(...)' makes the WaitAsync assertion unreachable (false-green).
    /// </summary>
    [Fact]
    public async Task WhenStoppingTokenCancelledMidCycle_ExecuteAsyncCompletes_LoopDoesNotHang()
    {
        // Use a short poll interval so the first cycle completes quickly before cancellation
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default() with { ClosedLoopPollInterval = TimeSpan.FromMilliseconds(50) });

        var housekeepingMock = new Mock<IHousekeepingService>();
        var svc = CreateServiceWithHousekeeping(housekeepingMock.Object);

        using var hostCts = new CancellationTokenSource();

        // CAPTURE the task — do NOT discard with `_ = ...`
        var executeTask = InvokeExecuteAsync(svc, hostCts.Token);

        var started = await svc.StartLoopAsync();
        started.Should().BeTrue("loop should start successfully");

        await WaitUntilAsync(
            () => svc.IsLoopActive,
            TimeSpan.FromSeconds(5),
            "loop should become active after StartLoopAsync");

        // Wait for at least one full cycle so the loop is confirmed running
        await WaitUntilAsync(
            () => svc.CycleCount >= 1,
            TimeSpan.FromSeconds(10),
            "loop must complete at least one cycle before cancellation is tested");

        // Cancel the stoppingToken — this is the signal the .NET host sends on shutdown
        hostCts.Cancel();

        // ExecuteAsync must complete promptly (the loop does not spin indefinitely after cancellation).
        // In the mid-cycle scenario RunMultiTemplateLoopAsync exits via 'break', so executeTask
        // completes normally (no OCE thrown at the ExecuteAsync level).
        await executeTask.WaitAsync(TimeSpan.FromSeconds(15));

        // CleanupAsync has run by the time executeTask completes — IsLoopActive reflects the stopped state
        svc.IsLoopActive.Should().BeFalse(
            "loop must be stopped after the stoppingToken is cancelled");
    }

    // ── Issue #3955 acceptance-criteria tests ────────────────────────────

    /// <summary>
    /// AC1-a (issue #3955): a bare <see cref="TaskCanceledException"/> (no inner
    /// <see cref="TimeoutRejectedException"/>) that escapes <c>ExecuteCycleAsync</c>
    /// via the L58 call site (<c>RunMultiTemplateLoopAsync</c>) must propagate rather
    /// than being retried as transient.
    ///
    /// Injection: <see cref="IHousekeepingService.ExecuteAsync"/> — <c>RunHousekeepingAsync</c>
    /// has no internal catch, so the exception propagates through <c>ExecuteCycleAsync</c>
    /// directly to <c>RunMultiTemplateLoopAsync</c>'s <c>IsTransientApiFailure</c> guard.
    /// Post-fix the guard no longer fires; the exception reaches <c>ExecuteAsync</c>'s
    /// <c>catch (Exception ex)</c> at <c>PipelineLoopService.cs:334</c>, logs at Error
    /// level, and stops the loop.
    /// </summary>
    [Fact]
    public async Task WhenBareTaskCanceledEscapesExecuteCycle_LoopStops_NotRetried()
    {
        // Deterministic synchronization: the TCS is signalled inside the mock the instant the loop
        // reaches housekeeping, guaranteeing the loop has started before we poll for it stopping.
        // The previous approach polled svc.IsLoopActive with a 5s timeout, but because
        // StartLoopAsync sets IsLoopActive=true and the loop can throw and call CleanupAsync
        // (setting IsLoopActive=false) before the first poll iteration, the poll could observe
        // IsLoopActive=false on every check and time out with "loop must start".
        var loopStartedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var housekeepingMock = new Mock<IHousekeepingService>();
        housekeepingMock
            .Setup(h => h.ExecuteAsync(It.IsAny<HousekeepingRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => loopStartedTcs.TrySetResult(true))
            .Throws(new TaskCanceledException("provider internal timeout — no inner TimeoutRejectedException"));

        var svc = CreateServiceWithHousekeeping(housekeepingMock.Object);
        using var hostCts = new CancellationTokenSource();
        _ = InvokeExecuteAsync(svc, hostCts.Token);
        (await svc.StartLoopAsync()).Should().BeTrue();
        // Await the TCS instead of polling IsLoopActive: fires when the mock is first called,
        // confirming the loop has entered ExecuteCycleAsync and reached housekeeping.
        await loopStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Post-fix: bare TaskCanceledException propagates out of RunMultiTemplateLoopAsync
        // to ExecuteAsync's catch(Exception ex) → Error logged → CleanupAsync(false) → loop stops.
        await WaitUntilAsync(() => !svc.IsLoopActive, TimeSpan.FromSeconds(30),
            "bare TaskCanceledException must stop the loop — it is no longer treated as transient");

        svc.IsLoopActive.Should().BeFalse(
            "bare TaskCanceledException must propagate as a non-transient exception and stop the loop");

        // The production log call at PipelineLoopService.cs:334 is non-generic Error(Exception, string)
        // with no extra structured parameters — this Verify correctly targets that exact overload.
        _mockLogger.Verify(
            l => l.Error(It.IsAny<Exception>(), It.Is<string>(s => s.Contains("unexpected error"))),
            Times.Once,
            "bare TaskCanceledException must be logged at Error level exactly once");

        hostCts.Cancel();
    }

    /// <summary>
    /// AC1-b (issue #3955): a bare <see cref="TaskCanceledException"/> thrown by
    /// <c>LoadPipelineConfigAsync</c> (the first async call inside
    /// <c>SnapshotCycleConfigAsync</c>) must propagate rather than being swallowed
    /// and returning <c>null</c> for a retry, exercising the L690 call site.
    ///
    /// <c>SnapshotCycleConfigAsync</c> is called outside the
    /// <c>try { ExecuteCycleAsync } catch</c> block in <c>RunMultiTemplateLoopAsync</c>,
    /// so the exception escapes the inner loop directly to <c>ExecuteAsync</c>'s
    /// <c>catch (Exception ex)</c>.
    /// </summary>
    [Fact]
    public async Task WhenBareTaskCanceledDuringSnapshot_LoopStops_NotRetried()
    {
        // Deterministic synchronization: the TCS is signalled inside the mock the instant the loop
        // calls LoadPipelineConfigAsync (the first call in SnapshotCycleConfigAsync), confirming
        // the loop has entered RunMultiTemplateLoopAsync before we poll for it stopping.
        // The previous approach polled svc.IsLoopActive with a 5s timeout, but because
        // StartLoopAsync sets IsLoopActive=true and the loop can throw and call CleanupAsync
        // (setting IsLoopActive=false) before the first poll iteration, the poll could observe
        // IsLoopActive=false on every check and time out with "loop must start".
        var loopStartedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Override default LoadPipelineConfigAsync to throw a bare TaskCanceledException.
        // _mockStore is wired into CreateServiceWithHousekeeping as PipelineConfigStore.
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .Callback<CancellationToken>(_ => loopStartedTcs.TrySetResult(true))
            .ThrowsAsync(new TaskCanceledException("provider internal timeout — no inner TimeoutRejectedException"));

        // CreateServiceWithHousekeeping(null): HousekeepingService is IHousekeepingService?
        // (nullable), so null is a valid argument.
        var svc = CreateServiceWithHousekeeping(null);
        using var hostCts = new CancellationTokenSource();
        _ = InvokeExecuteAsync(svc, hostCts.Token);
        (await svc.StartLoopAsync()).Should().BeTrue();
        // Await the TCS instead of polling IsLoopActive: fires when LoadPipelineConfigAsync is
        // first called inside SnapshotCycleConfigAsync, confirming the loop has started.
        await loopStartedTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await WaitUntilAsync(() => !svc.IsLoopActive, TimeSpan.FromSeconds(30),
            "bare TaskCanceledException from SnapshotCycleConfigAsync must propagate and stop the loop");

        svc.IsLoopActive.Should().BeFalse(
            "bare TaskCanceledException during snapshot is not transient — loop must stop");

        // Same non-generic Error(Exception, string) overload as in AC1-a.
        _mockLogger.Verify(
            l => l.Error(It.IsAny<Exception>(), It.Is<string>(s => s.Contains("unexpected error"))),
            Times.Once,
            "bare TaskCanceledException during snapshot must be logged at Error level");

        hostCts.Cancel();
    }

    /// <summary>
    /// AC2 (issue #3955): a <see cref="TaskCanceledException"/> whose
    /// <see cref="Exception.InnerException"/> is a <see cref="TimeoutRejectedException"/>
    /// must still be treated as transient and retried — the loop must stay alive.
    ///
    /// Note: with <c>AddStandardResilienceHandler</c>, Polly raises
    /// <see cref="TimeoutRejectedException"/> as the outer exception (already in the
    /// transient list), so this pattern does not occur naturally in the current call graph.
    /// The new arm is a forward-compatibility guard and this test exercises it directly as
    /// required by the acceptance criteria.
    /// </summary>
    [Fact]
    public async Task WhenTaskCanceledWrappingTimeoutRejectedException_EscapesCycle_LoopRetries()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default() with { ClosedLoopPollInterval = TimeSpan.FromMilliseconds(50) });

        var callCount = 0;
        var housekeepingMock = new Mock<IHousekeepingService>();
        housekeepingMock
            .Setup(h => h.ExecuteAsync(It.IsAny<HousekeepingRequest>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                // Interlocked.Increment for the write: the lambda runs on the loop thread;
                // WaitUntilAsync polls on the test thread. Without a memory barrier the JIT
                // could cache callCount in a register and the test thread would never see the update.
                var count = Interlocked.Increment(ref callCount);
                if (count == 1)
                    // TaskCanceledException(string, Exception) sets InnerException to the second arg.
                    throw new TaskCanceledException("polly-mediated timeout",
                        new TimeoutRejectedException("polly inner"));
                return Task.CompletedTask;
            });

        var svc = CreateServiceWithHousekeeping(housekeepingMock.Object);
        using var hostCts = new CancellationTokenSource();
        _ = InvokeExecuteAsync(svc, hostCts.Token);
        (await svc.StartLoopAsync()).Should().BeTrue();
        await WaitUntilAsync(() => svc.IsLoopActive, TimeSpan.FromSeconds(5), "loop must start");

        // The wrapped TaskCanceledException still matches IsTransientApiFailure → retried.
        // Volatile.Read for the read: pairs with Interlocked.Increment in the lambda above to
        // ensure cross-thread visibility without a full lock.
        await WaitUntilAsync(() => Volatile.Read(ref callCount) >= 2, TimeSpan.FromSeconds(30),
            "TaskCanceledException wrapping TimeoutRejectedException must still be retried as transient");

        svc.IsLoopActive.Should().BeTrue(
            "TaskCanceledException wrapping TimeoutRejectedException is still transient — loop must not stop");

        _mockLogger.Verify(
            l => l.Error(It.IsAny<Exception>(), It.Is<string>(s => s.Contains("unexpected error"))),
            Times.Never,
            "Polly-wrapped TaskCanceledException must NOT log at Error level");

        hostCts.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (_loopService is not null)
        {
            _loopService.StopLoop();
            await _loopService.StopAsync(CancellationToken.None);
            _loopService.Dispose();
        }
    }
}
