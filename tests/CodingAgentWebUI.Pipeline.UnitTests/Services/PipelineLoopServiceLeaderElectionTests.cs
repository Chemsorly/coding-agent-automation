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
/// Tests for the leader-election gating behaviour added to <see cref="PipelineLoopService"/>
/// by issue #1987.
///
/// Contract:
/// - When <c>ILeaderGate</c> is null (Legacy / no-gate mode), the loop runs unconditionally.
/// - When <c>ILeaderGate.IsLeader</c> is false, <c>ExecuteAsync</c> stays in the 2s leader-wait
///   loop and never enters <c>RunMultiTemplateLoopAsync</c>.
/// - When <c>IsLeader</c> becomes true, <c>ExecuteAsync</c> exits the wait loop and activates.
/// - On leadership loss (<c>LeaderToken</c> cancelled), <c>RunMultiTemplateLoopAsync</c> exits
///   cleanly, <c>CleanupAsync</c> re-arms the activation signal, and the loop auto-resumes on
///   re-acquisition — without requiring a second <c>StartLoopAsync()</c> call.
/// </summary>
[Trait("Feature", "LeaderGate")]
public class PipelineLoopServiceLeaderElectionTests : IAsyncDisposable
{
    private readonly Mock<IConfigurationStore> _mockStore;
    private readonly Mock<IProviderFactory> _mockFactory;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly DispatchRunCreationService _runCreator;
    private PipelineLoopService? _loopService;

    public PipelineLoopServiceLeaderElectionTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        _mockFactory = new Mock<IProviderFactory>();
        _mockLogger = new Mock<Serilog.ILogger>();

        var lifecycle = new PipelineRunLifecycleService(
            new TestOrchestrationFactory.NullHistoryService(), null, _mockLogger.Object);

        _runCreator = TestOrchestrationFactory.CreateMinimalRunCreator(
            configStore: _mockStore.Object,
            providerFactory: _mockFactory.Object,
            lifecycle: lifecycle,
            logger: _mockLogger.Object);

        SetupValidTemplates();
    }

    // ── Setup ───────────────────────────────────────────────────────────

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
    }

    private PipelineLoopService CreateService(ILeaderGate? leaderGate)
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

    // ── Test double ─────────────────────────────────────────────────────

    /// <summary>
    /// Controllable <see cref="ILeaderGate"/> for tests. Avoids reflection-based field
    /// manipulation that the existing <c>LeaderElectedPollingServiceTests</c> use.
    /// </summary>
    private sealed class FakeLeaderGate : ILeaderGate
    {
        private CancellationTokenSource _cts = new();

        public bool IsLeader { get; private set; }
        public CancellationToken LeaderToken => _cts.Token;

        /// <summary>Simulates acquiring leadership.</summary>
        public void AcquireLeadership()
        {
            // TODO [WARNING]: The previous _cts is replaced without being disposed, leaking one
            // CancellationTokenSource (and its OS wait handle) per acquire/lose cycle. Dispose the
            // old instance before replacing it. Additionally, the assignment to _cts and subsequent
            // write to IsLeader are not separated by a memory barrier (no `volatile`, `Interlocked`,
            // or `lock`), so under the .NET memory model a reader on another thread could observe
            // IsLeader == true before the new _cts assignment is visible, causing the linked CTS in
            // ExecuteAsync to be built from the old (already-cancelled) token.
            // Fix: dispose old CTS, then use Volatile.Write or a lock for both assignments.

            // Replace CTS first so LeaderToken is valid before IsLeader is observed as true.
            _cts = new CancellationTokenSource();
            IsLeader = true;
        }

        /// <summary>Simulates losing leadership (cancels LeaderToken).</summary>
        public void LoseLeadership()
        {
            IsLeader = false;
            _cts.Cancel();
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static Task InvokeExecuteAsync(PipelineLoopService service, CancellationToken stoppingToken)
    {
        var method = typeof(BackgroundService).GetMethod("ExecuteAsync",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        // Return the raw task so exceptions propagate through `await` in xUnit's normal
        // exception-handling path. A ContinueWith + GetAwaiter().GetResult() rethrow would
        // surface any TaskCanceledException as an unobserved exception on TaskScheduler.Default,
        // which trips the SetupCommandRunnerTests.RunAsync_Timeout_DoesNotProduceUnobservedTaskException test.
        return (Task)method.Invoke(service, [stoppingToken])!;
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout, string failMessage)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        condition().Should().BeTrue(failMessage);
    }

    // ── Tests ────────────────────────────────────────────────────────────

    /// <summary>
    /// Acceptance criterion: Legacy and SignalR mode behavior is unchanged.
    /// With null LeaderGate, the loop activates immediately without waiting for leadership.
    /// </summary>
    [Fact]
    public async Task NullLeaderGate_LoopActivatesWithoutLeaderWait()
    {
        // Arrange: no leader gate (Legacy mode)
        var svc = CreateService(leaderGate: null);
        using var hostCts = new CancellationTokenSource();
        var executeTask = InvokeExecuteAsync(svc, hostCts.Token);

        // Act: start loop
        var started = await svc.StartLoopAsync();
        started.Should().BeTrue();

        // Assert: loop becomes active immediately — no leader-wait delay
        await WaitUntil(() => svc.IsLoopActive, TimeSpan.FromSeconds(5),
            "loop should activate immediately when LeaderGate is null (Legacy mode)");

        // Cleanup
        hostCts.Cancel();
        await Task.WhenAny(executeTask, Task.Delay(5000));
    }

    /// <summary>
    /// Acceptance criterion: PipelineLoopService does not run its poll loop on non-leader replicas.
    /// When IsLeader is false, StartLoopAsync signals the activation TCS (and sets IsLoopActive=true)
    /// but ExecuteAsync stays in the leader-wait loop and never enters RunMultiTemplateLoopAsync.
    /// The StatusMessage stays at the initial "Loop starting…" value.
    /// </summary>
    [Fact]
    public async Task NonLeader_StartLoopAsync_SetsActiveButDoesNotRunLoop()
    {
        // Arrange: not the leader
        var gate = new FakeLeaderGate(); // IsLeader = false
        var svc = CreateService(gate);
        using var hostCts = new CancellationTokenSource();
        _ = InvokeExecuteAsync(svc, hostCts.Token);

        // Act: request loop activation
        var started = await svc.StartLoopAsync();

        // Assert: StartLoopAsync succeeds (sets IsLoopActive, returns true)…
        started.Should().BeTrue("StartLoopAsync should succeed even on a non-leader");
        svc.IsLoopActive.Should().BeTrue("IsLoopActive reflects operator intent, not current execution");

        // …but no polling happens: StatusMessage never advances beyond "starting"
        // and in particular never says "Cycle complete" or contains a template poll message.
        // TODO [WARNING]: The fixed 300 ms Task.Delay is a timing-based negative assertion — the
        // test passes whenever the loop hasn't happened to cycle within the window, not because it
        // structurally cannot. A more reliable approach is to assert zero invocations on the provider
        // mock's polling entry point (e.g. _mockFactory or _mockStore method calls), giving a
        // deterministic signal independent of wall-clock timing. The StatusMessage string checks
        // below are also fragile: if the exact message text changes, the assertion silently becomes
        // vacuously true without catching a regression in the leader-gate logic.
        await Task.Delay(300); // short fixed wait — loop definitely not cycling in 300ms
        svc.StatusMessage.Should().NotContain("Cycle complete",
            "loop should not complete any cycle while not the leader");
        svc.StatusMessage.Should().NotContain("polling",
            "loop should not begin polling while not the leader");

        // Cleanup
        hostCts.Cancel();
        await Task.WhenAny(InvokeExecuteAsync(svc, CancellationToken.None), Task.Delay(3000));
    }

    /// <summary>
    /// Acceptance criterion: Leadership acquisition (re)activates the loop.
    /// After StartLoopAsync is called on a non-leader, acquiring leadership causes
    /// ExecuteAsync to exit the wait loop and begin polling.
    /// </summary>
    [Fact]
    public async Task AcquiringLeadership_AfterStartLoop_ActivatesLoop()
    {
        // Arrange: not the leader initially
        var gate = new FakeLeaderGate(); // IsLeader = false
        var svc = CreateService(gate);
        using var hostCts = new CancellationTokenSource();
        _ = InvokeExecuteAsync(svc, hostCts.Token);

        var started = await svc.StartLoopAsync();
        started.Should().BeTrue();

        // Verify no activity yet
        await Task.Delay(100);
        svc.StatusMessage.Should().NotContain("Cycle complete");

        // Act: acquire leadership
        gate.AcquireLeadership();

        // Assert: loop enters polling within the leader-wait period (≤2s per tick) + CI overhead.
        // We wait up to 10s to be safe under parallel suite load.
        // Brain note: LeaderElectedPollingService pattern polls IsLeader every 2s.
        await WaitUntil(
            () => svc.StatusMessage.Contains("Cycle complete", StringComparison.OrdinalIgnoreCase)
               || svc.StatusMessage.Contains("polling", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(10),
            "loop should begin polling after leadership is acquired");

        // Cleanup
        hostCts.Cancel();
        await Task.WhenAny(InvokeExecuteAsync(svc, CancellationToken.None), Task.Delay(3000));
    }

    /// <summary>
    /// Acceptance criterion: Leadership loss during an active loop stops the loop cleanly.
    /// No in-progress cycle is aborted mid-step — the cancellation propagates via the linked
    /// LeaderToken, RunMultiTemplateLoopAsync exits, and CleanupAsync fires.
    /// The host stoppingToken is NOT cancelled; ExecuteAsync re-enters the outer while loop.
    /// </summary>
    [Fact]
    public async Task LeadershipLoss_StopsLoopCleanly_ServiceDoesNotTerminate()
    {
        // Arrange: start as leader
        var gate = new FakeLeaderGate();
        gate.AcquireLeadership();
        var svc = CreateService(gate);
        using var hostCts = new CancellationTokenSource();
        var executeTask = InvokeExecuteAsync(svc, hostCts.Token);

        await svc.StartLoopAsync();

        // Wait for at least one poll cycle to confirm the loop is running
        await WaitUntil(
            () => svc.StatusMessage.Contains("Cycle complete", StringComparison.OrdinalIgnoreCase)
               || svc.StatusMessage.Contains("polling", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(10),
            "loop should start polling after leadership is acquired");

        // Act: lose leadership
        gate.LoseLeadership();

        // Assert: after leadership loss, the StatusMessage reverts (cleanup ran and re-armed)
        // and IsLoopActive stays true (operator intent preserved).
        // The service does NOT terminate (executeTask is not completed).
        // TODO [WARNING]: The fixed Task.Delay(500) does not guarantee CleanupAsync has completed.
        // IsLoopActive is briefly false between the start of CleanupAsync (which sets it to false)
        // and the end of the re-arm block (which restores it to true). If the assertion races with
        // that window, it will produce a false pass. Replace with:
        //   await WaitUntil(() => svc.IsLoopActive, TimeSpan.FromSeconds(5), "...");
        // which positively confirms the re-arm is done before asserting.
        await Task.Delay(500); // give cleanup time to run
        executeTask.IsCompleted.Should().BeFalse(
            "ExecuteAsync should not exit when only leadership is lost (host is still running)");
        svc.IsLoopActive.Should().BeTrue(
            "IsLoopActive is re-armed by CleanupAsync to reflect operator intent");

        // Cleanup
        hostCts.Cancel();
        await Task.WhenAny(executeTask, Task.Delay(5000));
    }

    /// <summary>
    /// Acceptance criterion: Leadership acquisition (re)activates the loop consistent with
    /// LoopStatePersistenceService persisted state.
    ///
    /// This is the key test for the CleanupAsync re-arm logic. After leadership is lost
    /// and CleanupAsync runs, a second StartLoopAsync() call is NOT required — the loop
    /// auto-resumes when leadership is re-acquired.
    /// </summary>
    [Fact]
    public async Task LeadershipLossThenReacquisition_AutoResumesWithoutStartLoopAsync()
    {
        // Arrange: start as leader, loop active
        var gate = new FakeLeaderGate();
        gate.AcquireLeadership();
        var svc = CreateService(gate);
        using var hostCts = new CancellationTokenSource();
        _ = InvokeExecuteAsync(svc, hostCts.Token);

        await svc.StartLoopAsync();

        // Wait for the loop to start polling
        await WaitUntil(
            () => svc.StatusMessage.Contains("Cycle complete", StringComparison.OrdinalIgnoreCase)
               || svc.StatusMessage.Contains("polling", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(10),
            "loop should start polling after leadership is acquired");

        // Lose leadership
        gate.LoseLeadership();

        // Wait for CleanupAsync to run (IsLoopActive is re-armed, StatusMessage clears)
        // TODO [WARNING]: StatusMessage is cleared early in CleanupAsync (inside the lock, before
        // the re-arm block). WaitUntil may therefore return as soon as StatusMessage is empty but
        // *before* IsLoopActive is restored to true by the re-arm block. The subsequent
        // svc.IsLoopActive.Should().BeTrue() assertion below can then catch the service in the brief
        // window where cleanup has set IsLoopActive = false but hasn't yet re-armed it, producing
        // a flaky false failure. Replace the WaitUntil condition with one that directly polls
        // `svc.IsLoopActive` to confirm the re-arm is complete before asserting on it.
        await WaitUntil(
            () => !svc.StatusMessage.Contains("Cycle complete", StringComparison.OrdinalIgnoreCase)
               && !svc.StatusMessage.Contains("polling", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(5),
            "StatusMessage should clear after cleanup");

        // Confirm we are back in the leader-wait state (IsLoopActive=true but not cycling)
        svc.IsLoopActive.Should().BeTrue("CleanupAsync should re-arm IsLoopActive");
        // Do NOT call StartLoopAsync() again — this is what we're testing

        // Act: re-acquire leadership
        gate.AcquireLeadership();

        // Assert: loop auto-resumes polling — no second StartLoopAsync() needed
        await WaitUntil(
            () => svc.StatusMessage.Contains("Cycle complete", StringComparison.OrdinalIgnoreCase)
               || svc.StatusMessage.Contains("polling", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(10),
            "loop should auto-resume after leadership re-acquisition without a second StartLoopAsync() call");

        // Cleanup
        hostCts.Cancel();
        await Task.WhenAny(InvokeExecuteAsync(svc, CancellationToken.None), Task.Delay(3000));
    }

    public async ValueTask DisposeAsync()
    {
        if (_loopService is not null)
        {
            try { await _loopService.StopAsync(CancellationToken.None); }
            catch { /* suppress — test cleanup */ }
            _loopService.Dispose();
        }
    }
}
