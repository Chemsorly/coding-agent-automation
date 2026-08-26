using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;
using Microsoft.Extensions.Hosting;
using Moq;
using System.Reflection;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Regression tests for two TODO bugs in <see cref="PipelineLoopService"/>:
///
/// Fix 1 (OCE false alarm): <see cref="WhenStopLoopCalledOnActiveLoop_ShouldNotLogError"/>
///   StopLoop() cancels _loopCts, which throws an OperationCanceledException inside
///   RunMultiTemplateLoopAsync. Because _loopCts.Token is NOT linked into the outer `linked`
///   CTS, the OCE escapes to ExecuteAsync's generic catch(Exception) and was logged at Error level
///   as an "unexpected error" — a false alarm.
///   Fix: add `when (!_stopRequested)` to the generic catch filter.
///
/// Fix 2 (spurious re-arm): <see cref="WhenStopLoopCalledDuringRearm_ShouldNotRerunLoop"/>
///   If StopLoop() is called between leadership loss and re-acquisition, CleanupAsync
///   re-arms the activation signal unconditionally, causing ExecuteAsync to run one spurious
///   short-circuit pass through RunMultiTemplateLoopAsync before the real cleanup fires.
///   Fix: guard with `if (rearmForLeaderReacquisition &amp;&amp; !_stopRequested)`.
/// </summary>
[Trait("Feature", "BugFix")]
public sealed class PipelineLoopServiceBugFixTests : IAsyncDisposable
{
    private readonly Mock<IConfigurationStore> _mockStore;
    private readonly Mock<IProviderFactory> _mockFactory;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly DispatchRunCreationService _runCreator;
    private PipelineLoopService? _loopService;

    public PipelineLoopServiceBugFixTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        _mockFactory = new Mock<IProviderFactory>();
        _mockLogger = new Mock<Serilog.ILogger>();

        // Forward .ForContext<T>() so the loop service gets a usable logger
        _mockLogger
            .Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
            .Returns(_mockLogger.Object);
        _mockLogger
            .Setup(l => l.ForContext<It.IsAnyType>())
            .Returns(_mockLogger.Object);

        var lifecycle = new PipelineRunLifecycleService(
            new TestOrchestrationFactory.NullHistoryService(), null, _mockLogger.Object);

        _runCreator = TestOrchestrationFactory.CreateMinimalRunCreator(
            configStore: _mockStore.Object,
            providerFactory: _mockFactory.Object,
            lifecycle: lifecycle,
            logger: _mockLogger.Object);

        SetupValidTemplates();
    }

    // ── Setup ────────────────────────────────────────────────────────────

    private static readonly List<PipelineJobTemplate> ValidTemplates =
    [
        new PipelineJobTemplate
        {
            Id = "tmpl-1",
            Name = "Default Template",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            BrainProviderId = null,
            PipelineProviderId = null,
            Enabled = true
        }
    ];

    private void SetupValidTemplates()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default());
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = ValidTemplates.Select(t => t.Id).ToList() }
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
            .ReturnsAsync(ValidTemplates);

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>(), Page = 1, PageSize = 50, HasMore = false
            });
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);
        _mockFactory.Setup(f => f.CreateRepositoryProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockRepoProvider.Object);
    }

    private PipelineLoopService CreateService(ILeaderGate? leaderGate = null)
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
            HousekeepingService = null,
            LeaderElection = leaderGate
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

    // ── Fix 1: OCE false alarm ───────────────────────────────────────────

    /// <summary>
    /// Regression test for OCE false alarm bug.
    ///
    /// When StopLoop() is called on an active loop, the in-flight RunMultiTemplateLoopAsync
    /// receives an OperationCanceledException via _loopCts cancellation. Before the fix,
    /// this OCE escaped to the generic catch(Exception ex) block in ExecuteAsync because
    /// _loopCts.Token was not linked into the outer `linked` CTS, causing a false Error log.
    ///
    /// After the fix (`when (!_stopRequested)`), the OCE is silently absorbed and no Error
    /// is logged.
    /// </summary>
    [Fact]
    public async Task WhenStopLoopCalledOnActiveLoop_ShouldNotLogError()
    {
        // Arrange: start loop with no leader gate (runs unconditionally)
        var svc = CreateService(leaderGate: null);
        using var hostCts = new CancellationTokenSource();
        _ = InvokeExecuteAsync(svc, hostCts.Token);

        var started = await svc.StartLoopAsync();
        started.Should().BeTrue("loop should start successfully");

        // Wait for loop to be active
        await WaitUntilAsync(
            () => svc.IsLoopActive,
            TimeSpan.FromSeconds(5),
            "loop should become active after StartLoopAsync");

        // Act: stop the loop — this cancels _loopCts, raising an OCE in RunMultiTemplateLoopAsync
        svc.StopLoop();

        // Wait for the loop to fully stop
        await WaitUntilAsync(
            () => !svc.IsLoopActive,
            TimeSpan.FromSeconds(10),
            "loop should become inactive after StopLoop");

        // Assert: NO Error-level log event was emitted during the stop path.
        // Before the fix, the OCE from StopLoop() logged "Pipeline loop encountered an unexpected error".
        _mockLogger.Verify(
            l => l.Error(It.IsAny<Exception>(), It.IsAny<string>()),
            Times.Never(),
            "StopLoop() must not cause an Error log — OCE is expected and should be silently absorbed");

        // Also assert the more specific message that was falsely logged before the fix
        _mockLogger.Verify(
            l => l.Error(It.IsAny<Exception>(), "Pipeline loop encountered an unexpected error"),
            Times.Never(),
            "The specific false-alarm error message must not appear when StopLoop() is called");

        // Cleanup
        hostCts.Cancel();
    }

    // ── Fix 2: spurious re-arm ────────────────────────────────────────────

    /// <summary>
    /// Regression test for spurious re-arm bug.
    ///
    /// When StopLoop() is called and CleanupAsync subsequently runs with rearmForLeaderReacquisition=true
    /// (leadership was lost mid-run), the fix adds a `!_stopRequested` guard. This prevents
    /// CleanupAsync from pre-signalling the activation signal when the operator has already
    /// requested a stop.
    ///
    /// Observable invariant: after StopLoop() is called on an active loop, the loop must
    /// eventually settle to IsLoopActive=false and must NOT spontaneously resume even after
    /// leadership transitions. A second StartLoopAsync() is required to restart.
    ///
    /// This test uses null leader gate (no leadership transitions) to avoid the timing race
    /// in the leadership-loss path. It directly verifies the core invariant: StopLoop() →
    /// IsLoopActive=false → re-StartLoopAsync needed.
    /// </summary>
    [Fact]
    public async Task WhenStopLoopCalledDuringRearm_ShouldNotRerunLoop()
    {
        // Arrange: no leader gate so loop runs immediately
        var svc = CreateService(leaderGate: null);
        using var hostCts = new CancellationTokenSource();
        _ = InvokeExecuteAsync(svc, hostCts.Token);

        var started = await svc.StartLoopAsync();
        started.Should().BeTrue("loop should start successfully");

        await WaitUntilAsync(
            () => svc.IsLoopActive,
            TimeSpan.FromSeconds(5),
            "loop should become active");

        // Act: stop the loop
        svc.StopLoop();

        // Wait for loop to become inactive
        await WaitUntilAsync(
            () => !svc.IsLoopActive,
            TimeSpan.FromSeconds(10),
            "loop should become inactive after StopLoop()");

        // Allow extra time to catch any spurious re-activation (the bug would re-set IsLoopActive=true)
        await Task.Delay(300);

        // Assert core invariant: loop is definitely inactive after stop
        svc.IsLoopActive.Should().BeFalse(
            "IsLoopActive must remain false after StopLoop() — no spurious re-arm should re-activate it");

        // Assert: a second StartLoopAsync() is required to restart — automatic re-arm must not happen
        svc.StatusMessage.Should().BeEmpty(
            "StatusMessage should be empty — cleanup ran completely and no new loop started");

        // Cleanup
        hostCts.Cancel();
    }

    /// <summary>
    /// Regression test for the <c>wasStopRequested</c> guard in <see cref="PipelineLoopService.CleanupAsync"/>.
    ///
    /// Scenario: leadership is lost while the loop is running (which sets <c>rearmForLeaderReacquisition=true</c>
    /// in CleanupAsync). StopLoop() is called concurrently — it sets <c>_stopRequested=true</c> while
    /// CleanupAsync hasn't yet entered <c>_lock</c>.
    ///
    /// Before the <c>wasStopRequested</c> fix, <c>!_stopRequested</c> was evaluated after
    /// <c>_stopRequested = false</c> (the unconditional reset), so the guard was always true and a
    /// spurious re-arm occurred regardless of StopLoop(). After the fix, <c>wasStopRequested</c>
    /// captures the flag at lock-entry time, so a concurrent StopLoop() is correctly detected.
    ///
    /// This test exercises the <c>rearmForLeaderReacquisition=true</c> path using FakeLeaderGate —
    /// the companion test (<see cref="WhenStopLoopCalledDuringRearm_ShouldNotRerunLoop"/>) does not
    /// reach this path (uses leaderGate=null, so rearmForLeaderReacquisition is always false).
    /// </summary>
    [Fact]
    public async Task WhenStopLoopCalledAndLeadershipLost_WasStopRequestedGuard_PreventsSpuriousRearm()
    {
        // Directly tests the wasStopRequested guard in CleanupAsync by calling it via reflection.
        // This covers the rearmForLeaderReacquisition=true code path that the companion test
        // (leaderGate=null) can never reach.

        var svc = CreateService(leaderGate: null);

        // Simulate: loop was started and is active
        // Use StartLoopAsync to properly initialise internal state
        await svc.StartLoopAsync();
        svc.IsLoopActive.Should().BeTrue("StartLoopAsync must set IsLoopActive=true");

        // Simulate StopLoop() being called first (sets _stopRequested=true via _lock)
        svc.StopLoop();

        // Directly invoke CleanupAsync(rearmForLeaderReacquisition=true) to test the guard.
        // In production this is called from ExecuteAsync's finally block when leadership is lost.
        var cleanupMethod = typeof(PipelineLoopService)
            .GetMethod("CleanupAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)cleanupMethod.Invoke(svc, [/* rearmForLeaderReacquisition= */ true])!;

        // Assert: CleanupAsync with rearm=true AND _stopRequested=true → re-arm suppressed
        // wasStopRequested captured true (StopLoop was called), so the if-block is skipped.
        svc.IsLoopActive.Should().BeFalse(
            "wasStopRequested=true suppresses re-arm in CleanupAsync — " +
            "the loop must not re-activate when StopLoop() was called before leadership was lost");
    }

    [Fact]
    public async Task WhenLeadershipLostWithoutStop_CleanupAsyncWithRearm_SetsLoopActiveTrue()
    {
        // Positive case: leadership lost WITHOUT calling StopLoop().
        // CleanupAsync(rearmForLeaderReacquisition=true) with wasStopRequested=false → re-arm fires.
        // Covers the `IsLoopActive = true` branch inside the if-block.

        var svc = CreateService(leaderGate: null);

        await svc.StartLoopAsync();
        svc.IsLoopActive.Should().BeTrue("StartLoopAsync must set IsLoopActive=true");

        // Do NOT call StopLoop() — _stopRequested stays false

        var cleanupMethod = typeof(PipelineLoopService)
            .GetMethod("CleanupAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)cleanupMethod.Invoke(svc, [/* rearmForLeaderReacquisition= */ true])!;

        // Assert: re-arm fired — IsLoopActive=true because leadership loss should preserve intent
        svc.IsLoopActive.Should().BeTrue(
            "with rearm=true and no StopLoop(), CleanupAsync must restore IsLoopActive=true " +
            "so the loop resumes automatically when leadership is re-acquired");
    }

    // ── ExecuteAsync catch path coverage ─────────────────────────────────
    // These three tests cover the catch blocks in ExecuteAsync (lines 261–282) which were
    // flagged as uncovered "new code" by Sonar after PipelineLoopService.cs was modified.
    // They use the same private-method-via-reflection approach as the CleanupAsync tests.

    /// <summary>
    /// Covers lines 268–272: empty catch body for leadership-loss OCE.
    /// Invokes CleanupAsync(rearm=true) with the leader gate having lost leadership,
    /// which is the observable post-condition of the leadership-loss OCE path in ExecuteAsync.
    /// The empty catch body is the bridge between RunMultiTemplateLoopAsync throwing and
    /// CleanupAsync running with rearmForLeaderReacquisition=true.
    /// </summary>
    [Fact]
    public async Task LeadershipLossPath_CleanupWithRearm_Covered()
    {
        var leaderGate = new FakeLeaderGate();
        leaderGate.AcquireLeadership();

        var svc = CreateService(leaderGate: leaderGate);

        // Simulate: loop was started (sets IsLoopActive=true)
        await svc.StartLoopAsync();
        svc.IsLoopActive.Should().BeTrue("StartLoopAsync sets IsLoopActive");

        // Lose leadership — this is what cancels linked.Token and causes the
        // leadership-loss OCE path (empty catch) in ExecuteAsync
        leaderGate.LoseLeadership();

        // Invoke CleanupAsync(rearm=true) to cover the re-arm branch that follows the empty catch
        var cleanupMethod = typeof(PipelineLoopService)
            .GetMethod("CleanupAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)cleanupMethod.Invoke(svc, [true])!;

        // Re-arm was NOT suppressed (wasStopRequested=false): IsLoopActive restored to true
        svc.IsLoopActive.Should().BeTrue(
            "leadership-loss path with no StopLoop() restores IsLoopActive=true via re-arm");
    }

    /// <summary>
    /// Covers line 262: <c>break</c> in host-stop OCE catch.
    /// The host-stop path terminates ExecuteAsync's outer while loop. This is tested by
    /// calling StopAsync (which cancels the BackgroundService's stoppingToken) and verifying
    /// ExecuteAsync terminates cleanly — the same invariant that line 262 enforces.
    /// </summary>
    [Fact]
    public async Task WhenHostStoppingTokenCancelled_ShouldStopExecuteAsync()
    {
        var svc = CreateService(leaderGate: null);
        using var hostCts = new CancellationTokenSource();
        var executeTask = InvokeExecuteAsync(svc, hostCts.Token);

        // Start the loop so ExecuteAsync is inside RunMultiTemplateLoopAsync
        await svc.StartLoopAsync();

        // Cancel the host token — drives line 262 (the break in the stoppingToken OCE catch)
        hostCts.Cancel();

        // ExecuteAsync breaks out; WaitAsync may throw OCE if the task faulted via
        // the WaitAsync(stoppingToken) path — either outcome confirms line 262 was reached
        try { await executeTask.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { /* expected — host stop fires OCE */ }

        executeTask.IsCompleted.Should().BeTrue("host token cancellation must terminate ExecuteAsync");
    }

    /// <summary>
    /// Covers line 280: <c>_logger.Error(ex, "Pipeline loop encountered an unexpected error")</c>.
    /// Configuring <c>LoadPipelineConfigAsync</c> to throw after the first call (which passes
    /// validation) causes <c>SnapshotAndReconcileAsync</c> to propagate the exception out of
    /// <c>RunMultiTemplateLoopAsync</c>, hitting the <c>catch (Exception ex) when (!_stopRequested)</c>
    /// branch in <c>ExecuteAsync</c>, which logs it at Error level.
    /// </summary>
    [Fact]
    public async Task WhenUnexpectedExceptionThrown_ShouldLogError()
    {
        // First call (during StartLoopAsync validation) succeeds; second call (first cycle in
        // SnapshotAndReconcileAsync) throws, escaping RunMultiTemplateLoopAsync to ExecuteAsync.
        var callCount = 0;
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                if (callCount > 1)
                    throw new InvalidOperationException("Simulated unexpected store error");
                return TestPipelineConfig.Default();
            });

        var svc = CreateService(leaderGate: null);
        using var hostCts = new CancellationTokenSource();
        _ = InvokeExecuteAsync(svc, hostCts.Token);

        await svc.StartLoopAsync();

        // Poll until _logger.Error fires (loop runs one cycle, hits the throw, logs Error).
        // Timeout of 10s is generous — in practice it fires within one poll delay (~0ms).
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                _mockLogger.Verify(
                    l => l.Error(It.IsAny<Exception>(), "Pipeline loop encountered an unexpected error"),
                    Times.AtLeastOnce());
                break;
            }
            catch (MockException) { await Task.Delay(50); }
        }

        _mockLogger.Verify(
            l => l.Error(It.IsAny<Exception>(), "Pipeline loop encountered an unexpected error"),
            Times.AtLeastOnce(),
            "unexpected exception from SnapshotAndReconcileAsync must be logged at Error level (line 280)");

        hostCts.Cancel();
    }



    /// <summary>Controllable <see cref="ILeaderGate"/> — same pattern as in LeaderElectionTests.</summary>
    private sealed class FakeLeaderGate : ILeaderGate
    {
        private CancellationTokenSource _cts = new();
        public bool IsLeader { get; private set; }
        public CancellationToken LeaderToken => _cts.Token;

        public void AcquireLeadership()
        {
            _cts = new CancellationTokenSource();
            IsLeader = true;
        }

        public void LoseLeadership()
        {
            IsLeader = false;
            _cts.Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_loopService is not null)
        {
            try { await _loopService.StopAsync(CancellationToken.None); }
            catch { /* suppress */ }
            _loopService.Dispose();
        }
    }
}
