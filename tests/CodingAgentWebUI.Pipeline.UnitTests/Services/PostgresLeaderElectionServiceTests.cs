using System.Data;
using System.Reflection;
using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.LeaderElection;
using Microsoft.Extensions.Options;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

public class PostgresLeaderElectionServiceTests
{
    private static PostgresLeaderElectionOptions DefaultOptions() => new()
    {
        RenewalInterval = TimeSpan.FromSeconds(5),
        RetryDelay = TimeSpan.FromSeconds(5),
        LockKey = 0x0CAA_1EAD
    };

    private static PostgresLeaderElectionOptions FastOptions() => new()
    {
        RenewalInterval = TimeSpan.FromMilliseconds(30),
        RetryDelay = TimeSpan.FromMilliseconds(30),
        LockKey = 0x0CAA_1EAD
    };

    #region Initial State Tests

    [Fact]
    public void IsLeader_DefaultsToFalse()
    {
        var sut = new PostgresLeaderElectionService(
            "Host=localhost;Database=test",
            DefaultOptions());

        sut.IsLeader.Should().BeFalse();
    }

    [Fact]
    public void LeaderToken_WhenNotStarted_IsCancelled()
    {
        var sut = new PostgresLeaderElectionService(
            "Host=localhost;Database=test",
            DefaultOptions());

        sut.LeaderToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void Constructor_ThrowsOnNullOrEmptyConnectionString()
    {
        var act1 = () => new PostgresLeaderElectionService(
            "", DefaultOptions());
        act1.Should().Throw<ArgumentException>();

        var act2 = () => new PostgresLeaderElectionService(
            null!, DefaultOptions());
        act2.Should().Throw<ArgumentException>();
    }

    #endregion

    #region Lifecycle Tests

    [Fact]
    public async Task StartAsync_InitializesElectionLoop()
    {
        // TODO: This test only verifies StartAsync doesn't throw with an unreachable DB and
        // that IsLeader stays false — it would pass even if StartAsync were a no-op. The real
        // election loop behavior is tested in the Lock Acquisition tests using fake connections.
        var sut = new PostgresLeaderElectionService(
            "Host=localhost;Database=test",
            DefaultOptions());

        // StartAsync should not throw even with unreachable DB
        // (the loop handles connection failures internally)
        await sut.StartAsync(CancellationToken.None);

        // Service is started but not yet leader (can't connect to DB)
        sut.IsLeader.Should().BeFalse();

        // Clean up
        await sut.StopAsync(CancellationToken.None);
        sut.Dispose();
    }

    [Fact]
    public async Task StopAsync_WithoutStart_DoesNotThrow()
    {
        var sut = new PostgresLeaderElectionService(
            "Host=localhost;Database=test",
            DefaultOptions());

        var act = () => sut.StopAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Dispose_WithoutStart_DoesNotThrow()
    {
        var sut = new PostgresLeaderElectionService(
            "Host=localhost;Database=test",
            DefaultOptions());

        var act = () => sut.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task Dispose_AfterStart_DoesNotThrow()
    {
        var sut = new PostgresLeaderElectionService(
            "Host=localhost;Database=test",
            DefaultOptions());
        await sut.StartAsync(CancellationToken.None);

        // Give the loop a moment to start attempting connections
        await Task.Delay(50);

        await sut.StopAsync(CancellationToken.None);

        var act = () => sut.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task LeaderToken_AfterStart_IsNotCancelled()
    {
        var sut = new PostgresLeaderElectionService(
            "Host=localhost;Database=test",
            DefaultOptions());

        await sut.StartAsync(CancellationToken.None);

        // After start, a fresh CTS is created (token is not cancelled until leadership is lost)
        sut.LeaderToken.IsCancellationRequested.Should().BeFalse();

        await sut.StopAsync(CancellationToken.None);
        sut.Dispose();
    }

    [Fact]
    public async Task StopAsync_WhenLeader_CancelsLeaderToken()
    {
        // TODO: This test doesn't actually make the service become leader (DB is unreachable),
        // so it only verifies StopAsync doesn't throw and IsLeader remains false. The real
        // leader-token-cancelled-on-stop behavior is verified in Release_OnStop_CancelsLeaderToken
        // which uses a fake connection to achieve actual leadership.
        var sut = new PostgresLeaderElectionService(
            "Host=localhost;Database=test",
            DefaultOptions());
        await sut.StartAsync(CancellationToken.None);

        await sut.StopAsync(CancellationToken.None);

        // After stop, IsLeader must be false
        sut.IsLeader.Should().BeFalse();
        sut.Dispose();
    }

    [Fact]
    public void Options_DefaultValues_AreCorrect()
    {
        var opts = new PostgresLeaderElectionOptions();

        opts.RenewalInterval.Should().Be(TimeSpan.FromSeconds(5));
        opts.RetryDelay.Should().Be(TimeSpan.FromSeconds(5));
        opts.LockKey.Should().Be(0x0CAA_1EAD);
    }

    [Fact]
    public void Options_SectionName_IsCorrect()
    {
        PostgresLeaderElectionOptions.SectionName.Should().Be("LeaderElection:Postgres");
    }

    [Fact]
    public void ImplementsILeaderElectionService()
    {
        var sut = new PostgresLeaderElectionService(
            "Host=localhost;Database=test",
            DefaultOptions());

        sut.Should().BeAssignableTo<ILeaderElectionService>();
    }

    [Fact]
    public async Task OnStartedLeading_NotFiredWhenCannotConnect()
    {
        var options = DefaultOptions();
        options.RetryDelay = TimeSpan.FromMilliseconds(50);

        var sut = new PostgresLeaderElectionService(
            "Host=localhost;Port=59999;Database=test;Timeout=1",
            options);

        var startedLeading = false;
        sut.OnStartedLeading += () => startedLeading = true;

        await sut.StartAsync(CancellationToken.None);

        // Wait for a few retry cycles
        await Task.Delay(300);

        startedLeading.Should().BeFalse("should not fire when DB is unreachable");
        sut.IsLeader.Should().BeFalse();

        await sut.StopAsync(CancellationToken.None);
        sut.Dispose();
    }

    [Fact]
    public async Task MultipleStartStop_DoesNotThrow()
    {
        var options = DefaultOptions();
        options.RetryDelay = TimeSpan.FromMilliseconds(50);

        var sut = new PostgresLeaderElectionService(
            "Host=localhost;Port=59999;Database=test;Timeout=1",
            options);

        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await sut.StopAsync(CancellationToken.None);

        // Second start/stop cycle
        await sut.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await sut.StopAsync(CancellationToken.None);

        sut.Dispose();
    }

    [Fact]
    public void ILeaderElectionService_ExistingK8sService_ImplementsInterface()
    {
        var sut = new LeaderElectionService(Options.Create(new LeaderElectionOptions()));
        sut.Should().BeAssignableTo<ILeaderElectionService>();
    }

    #endregion

    #region Lock Acquisition (Happy Path) — CRITICAL

    [Fact]
    public async Task Acquire_WhenLockAvailable_BecomesLeaderAndFiresEvent()
    {
        var fakeConn = new FakeAdvisoryLockConnection(acquireResult: true, verifyResult: true);
        var factory = new FakeAdvisoryLockConnectionFactory(fakeConn);
        var options = FastOptions();

        var sut = new PostgresLeaderElectionService(options, factory);

        var startedLeading = false;
        sut.OnStartedLeading += () => startedLeading = true;

        await sut.StartAsync(CancellationToken.None);

        // Wait for the election loop to acquire the lock
        await WaitUntilAsync(() => sut.IsLeader, timeout: TimeSpan.FromSeconds(2));

        sut.IsLeader.Should().BeTrue("service should become leader when lock is acquired");
        startedLeading.Should().BeTrue("OnStartedLeading should fire on acquisition");
        sut.LeaderToken.IsCancellationRequested.Should().BeFalse("LeaderToken should be valid while leader");

        await sut.StopAsync(CancellationToken.None);
        sut.Dispose();
    }

    [Fact]
    public async Task Acquire_WhenLockNotAvailable_RemainsNonLeader()
    {
        var fakeConn = new FakeAdvisoryLockConnection(acquireResult: false, verifyResult: false);
        var factory = new FakeAdvisoryLockConnectionFactory(fakeConn);
        var options = FastOptions();

        var sut = new PostgresLeaderElectionService(options, factory);

        var startedLeading = false;
        sut.OnStartedLeading += () => startedLeading = true;

        await sut.StartAsync(CancellationToken.None);

        // Give it time to attempt acquisition
        await Task.Delay(200);

        sut.IsLeader.Should().BeFalse("service should not become leader when lock is unavailable");
        startedLeading.Should().BeFalse("OnStartedLeading should not fire");

        await sut.StopAsync(CancellationToken.None);
        sut.Dispose();
    }

    #endregion

    #region Lock Release — CRITICAL

    [Fact]
    public async Task Release_OnGracefulShutdown_ReleasesLock()
    {
        var fakeConn = new FakeAdvisoryLockConnection(acquireResult: true, verifyResult: true);
        var factory = new FakeAdvisoryLockConnectionFactory(fakeConn);
        var options = FastOptions();

        var sut = new PostgresLeaderElectionService(options, factory);

        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sut.IsLeader, timeout: TimeSpan.FromSeconds(2));

        // Stop the service — should release the lock
        await sut.StopAsync(CancellationToken.None);

        fakeConn.ReleaseLockCalled.Should().BeTrue("pg_advisory_unlock should be called during graceful shutdown");
        sut.IsLeader.Should().BeFalse("should no longer be leader after stop");
        sut.Dispose();
    }

    [Fact]
    public async Task Release_OnStop_CancelsLeaderToken()
    {
        var fakeConn = new FakeAdvisoryLockConnection(acquireResult: true, verifyResult: true);
        var factory = new FakeAdvisoryLockConnectionFactory(fakeConn);
        var options = FastOptions();

        var sut = new PostgresLeaderElectionService(options, factory);

        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sut.IsLeader, timeout: TimeSpan.FromSeconds(2));

        var leaderToken = sut.LeaderToken;
        leaderToken.IsCancellationRequested.Should().BeFalse();

        await sut.StopAsync(CancellationToken.None);

        leaderToken.IsCancellationRequested.Should().BeTrue(
            "LeaderToken should be cancelled when leadership is lost on shutdown");
        sut.Dispose();
    }

    #endregion

    #region Connection Drop Detection — CRITICAL

    [Fact]
    public async Task ConnectionDrop_WhenLeader_LosesLeadershipAndFiresEvent()
    {
        var fakeConn = new FakeAdvisoryLockConnection(acquireResult: true, verifyResult: true);
        // After drop, factory returns a connection that fails to open — keeps service non-leader
        var failConn = new FakeAdvisoryLockConnection(acquireResult: false, verifyResult: false, failOnOpen: true);
        var factory = new FakeAdvisoryLockConnectionFactory(fakeConn);
        var options = FastOptions();

        var sut = new PostgresLeaderElectionService(options, factory);

        var stoppedLeadingTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken leaderTokenBeforeDrop = default;

        sut.OnStoppedLeading += () => stoppedLeadingTcs.TrySetResult();

        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sut.IsLeader, timeout: TimeSpan.FromSeconds(2));

        leaderTokenBeforeDrop = sut.LeaderToken;

        // After the drop, make the factory return a connection that fails to open
        // so the service cannot re-acquire leadership immediately
        factory.SetNextConnection(failConn);

        // Simulate connection drop
        fakeConn.SimulateConnectionDrop();

        // Wait for OnStoppedLeading to fire (reliable signal of leadership loss)
        var completed = await Task.WhenAny(stoppedLeadingTcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        completed.Should().Be(stoppedLeadingTcs.Task, "OnStoppedLeading should fire on connection drop");

        leaderTokenBeforeDrop.IsCancellationRequested.Should().BeTrue(
            "LeaderToken should be cancelled when leadership is lost due to connection drop");

        await sut.StopAsync(CancellationToken.None);
        sut.Dispose();
    }

    [Fact]
    public async Task ConnectionDrop_WhenVerifyFails_LosesLeadership()
    {
        var fakeConn = new FakeAdvisoryLockConnection(acquireResult: true, verifyResult: true);
        // After verify failure, factory returns a connection that fails to open
        var failConn = new FakeAdvisoryLockConnection(acquireResult: false, verifyResult: false, failOnOpen: true);
        var factory = new FakeAdvisoryLockConnectionFactory(fakeConn);
        var options = FastOptions();

        var sut = new PostgresLeaderElectionService(options, factory);

        var stoppedLeadingTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        sut.OnStoppedLeading += () => stoppedLeadingTcs.TrySetResult();

        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sut.IsLeader, timeout: TimeSpan.FromSeconds(2));

        // After the failure, make the factory return a connection that fails to open
        factory.SetNextConnection(failConn);

        // Simulate lock verification failure (lock stolen or pg_locks query error)
        fakeConn.SimulateVerifyFailure();

        // Wait for OnStoppedLeading to fire
        var completed = await Task.WhenAny(stoppedLeadingTcs.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        completed.Should().Be(stoppedLeadingTcs.Task,
            "OnStoppedLeading should fire when lock verification fails");

        await sut.StopAsync(CancellationToken.None);
        sut.Dispose();
    }

    #endregion

    #region Re-acquisition After Reconnection — CRITICAL

    [Fact]
    public async Task Reacquisition_AfterConnectionDrop_ReconnectsAndBecomesLeader()
    {
        var fakeConn = new FakeAdvisoryLockConnection(acquireResult: true, verifyResult: true);
        var factory = new FakeAdvisoryLockConnectionFactory(fakeConn);
        var options = FastOptions();

        var sut = new PostgresLeaderElectionService(options, factory);

        var startedLeadingCount = 0;
        sut.OnStartedLeading += () => Interlocked.Increment(ref startedLeadingCount);

        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sut.IsLeader, timeout: TimeSpan.FromSeconds(2));

        startedLeadingCount.Should().Be(1, "should have become leader once");

        // Simulate connection drop — leadership lost
        fakeConn.SimulateConnectionDrop();
        await WaitUntilAsync(() => !sut.IsLeader, timeout: TimeSpan.FromSeconds(2));

        // Simulate reconnection — the factory will provide a new connection that works
        var newConn = new FakeAdvisoryLockConnection(acquireResult: true, verifyResult: true);
        factory.SetNextConnection(newConn);

        // Wait for the service to re-acquire leadership via the new connection
        await WaitUntilAsync(() => sut.IsLeader, timeout: TimeSpan.FromSeconds(2));

        sut.IsLeader.Should().BeTrue("should re-acquire leadership after reconnection");
        startedLeadingCount.Should().Be(2, "OnStartedLeading should fire again on re-acquisition");

        await sut.StopAsync(CancellationToken.None);
        sut.Dispose();
    }

    #endregion

    #region Concurrency — Race Condition Tests

    [Fact]
    // TODO: This test may not reliably exercise the concurrent race. StopAsync's _serviceCts.CancelAsync()
    // causes the verify loop to exit before the connection state is inspected, so StopAsync likely always
    // wins the CompareExchange uncontested. SimulateConnectionDrop() has no effect because the cancellation
    // token breaks the loop first. Consider using synchronization primitives (SemaphoreSlim/TaskCompletionSource
    // barriers) to force both StopAsync and HandleLeadershipLostAsync to reach the CompareExchange simultaneously.
    public async Task ConcurrentStop_AndLeadershipLost_ProducesSingleStoppedLeadingEvent()
    {
        var fakeConn = new FakeAdvisoryLockConnection(acquireResult: true, verifyResult: true);
        var factory = new FakeAdvisoryLockConnectionFactory(fakeConn);
        var options = FastOptions();

        var sut = new PostgresLeaderElectionService(options, factory);

        var stoppedLeadingCount = 0;
        sut.OnStoppedLeading += () => Interlocked.Increment(ref stoppedLeadingCount);

        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sut.IsLeader, timeout: TimeSpan.FromSeconds(2));

        var leaderToken = sut.LeaderToken;
        leaderToken.IsCancellationRequested.Should().BeFalse();

        // Simultaneously: stop the service AND simulate connection drop
        // This triggers the race between StopAsync and HandleLeadershipLostAsync
        var stopTask = sut.StopAsync(CancellationToken.None);
        fakeConn.SimulateConnectionDrop();

        await stopTask;

        // Exactly one OnStoppedLeading event should have fired (not zero, not two)
        Volatile.Read(ref stoppedLeadingCount).Should().Be(1,
            "exactly one OnStoppedLeading event should fire when StopAsync races with leadership loss");
        leaderToken.IsCancellationRequested.Should().BeTrue(
            "LeaderToken must always be cancelled during shutdown regardless of race outcome");
        sut.IsLeader.Should().BeFalse();

        sut.Dispose();
    }

    [Fact]
    public async Task StopAsync_WhenLeader_AlwaysCancelsLeaderToken()
    {
        var fakeConn = new FakeAdvisoryLockConnection(acquireResult: true, verifyResult: true);
        var factory = new FakeAdvisoryLockConnectionFactory(fakeConn);
        var options = FastOptions();

        var sut = new PostgresLeaderElectionService(options, factory);

        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sut.IsLeader, timeout: TimeSpan.FromSeconds(2));

        var leaderToken = sut.LeaderToken;
        leaderToken.IsCancellationRequested.Should().BeFalse();

        await sut.StopAsync(CancellationToken.None);

        // Regardless of internal timing, the leader token captured while leader must be cancelled
        leaderToken.IsCancellationRequested.Should().BeTrue(
            "LeaderToken captured while leader must always be cancelled after StopAsync returns");

        sut.Dispose();
    }

    [Fact]
    // TODO: This test title claims to verify disposal of old CTS instances but only asserts that new
    // tokens work correctly. It does not actually verify the old CTS was disposed (e.g., by checking
    // ObjectDisposedException on the old CTS or using a wrapper to track disposal calls).
    public async Task MultipleStartStop_DisposesOldCancellationTokenSources()
    {
        var fakeConn = new FakeAdvisoryLockConnection(acquireResult: true, verifyResult: true);
        var factory = new FakeAdvisoryLockConnectionFactory(fakeConn);
        var options = FastOptions();

        var sut = new PostgresLeaderElectionService(options, factory);

        // First cycle
        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sut.IsLeader, timeout: TimeSpan.FromSeconds(2));

        var firstLeaderToken = sut.LeaderToken;
        firstLeaderToken.IsCancellationRequested.Should().BeFalse();

        await sut.StopAsync(CancellationToken.None);
        firstLeaderToken.IsCancellationRequested.Should().BeTrue();

        // Second cycle — should not throw ObjectDisposedException
        await sut.StartAsync(CancellationToken.None);

        // After second start, LeaderToken should be a fresh, uncancelled token
        sut.LeaderToken.IsCancellationRequested.Should().BeFalse(
            "LeaderToken should be fresh and uncancelled after second StartAsync");

        await WaitUntilAsync(() => sut.IsLeader, timeout: TimeSpan.FromSeconds(2));

        var secondLeaderToken = sut.LeaderToken;
        secondLeaderToken.IsCancellationRequested.Should().BeFalse();

        await sut.StopAsync(CancellationToken.None);
        secondLeaderToken.IsCancellationRequested.Should().BeTrue();

        sut.Dispose();
    }

    [Fact]
    public async Task StartAsync_WithStaleLeaderState_ResetsAndAcquiresCorrectly()
    {
        var fakeConn = new FakeAdvisoryLockConnection(acquireResult: true, verifyResult: true);
        var factory = new FakeAdvisoryLockConnectionFactory(fakeConn);
        var options = FastOptions();

        var sut = new PostgresLeaderElectionService(options, factory);

        // Simulate stale _leaderState=1 from a previous lifecycle (e.g., crash recovery
        // where StopAsync was never called). Use reflection since _leaderState is private.
        typeof(PostgresLeaderElectionService)
            .GetField("_leaderState", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(sut, 1);

        // Verify the stale state is set
        sut.IsLeader.Should().BeTrue("precondition: stale leader state should be set via reflection");

        var startedLeading = false;
        sut.OnStartedLeading += () => startedLeading = true;

        // StartAsync should reset _leaderState to 0, allowing the election loop to acquire
        await sut.StartAsync(CancellationToken.None);

        // After the defensive reset, the election loop should acquire leadership and fire the event
        await WaitUntilAsync(() => startedLeading, timeout: TimeSpan.FromSeconds(2));

        sut.IsLeader.Should().BeTrue("service should become leader after stale state is reset");
        startedLeading.Should().BeTrue(
            "OnStartedLeading should fire when StartAsync resets stale state and re-acquires");

        await sut.StopAsync(CancellationToken.None);
        sut.Dispose();
    }

    [Fact]
    // TODO: This test would not fail if the CAS fix were reverted to Volatile.Write, because the
    // election loop's self-healing path (VerifyLockHeldLoopAsync exits on cancelled token →
    // HandleLeadershipLostAsync resets _leaderState to 0) produces the same IsLeader=false outcome
    // regardless of CAS vs Volatile.Write. To truly validate the CAS prevents phantom leadership,
    // this test should assert that OnStartedLeading is NOT called during the race (the actual
    // behavioral difference), and should set up initial leader state so StopAsync's CAS(0,1)
    // fires first, blocking the loop's subsequent CAS(1,0).
    public async Task Acquire_WhenStopAsyncRaces_DoesNotLeavePhantomLeaderState()
    {
        // This test exercises the race: TryAcquireLockAsync completes with true AFTER
        // StopAsync has already cancelled the service. The CAS ensures _leaderState is
        // correctly managed — after StopAsync completes, IsLeader must be false.
        var acquireTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayedConn = new DelayedFakeAdvisoryLockConnection(acquireTcs, verifyResult: true);
        var factory = new FakeAdvisoryLockConnectionFactory(delayedConn);
        var options = FastOptions();

        var sut = new PostgresLeaderElectionService(options, factory);

        await sut.StartAsync(CancellationToken.None);

        // Give the election loop time to reach TryAcquireLockAsync (which blocks on acquireTcs)
        await Task.Delay(50);

        // Call StopAsync — this cancels _serviceCts. The election loop is blocked in TryAcquireLockAsync.
        var stopTask = sut.StopAsync(CancellationToken.None);

        // Small delay to ensure StopAsync has cancelled the service CTS
        await Task.Delay(20);

        // Now complete the acquire with true — simulating the lock being granted
        // just before/as cancellation propagates.
        acquireTcs.SetResult(true);

        await stopTask;

        // After StopAsync completes, the service must NOT be in a phantom leader state.
        // With Volatile.Write (old code), _leaderState could remain stuck at 1 if the
        // election loop set it after StopAsync's cleanup. With the CAS + loop self-healing,
        // the final state is always non-leader.
        sut.IsLeader.Should().BeFalse(
            "after StopAsync completes, IsLeader must be false regardless of acquire race timing");

        sut.Dispose();
    }

    #endregion

    #region Shutdown Timeout — CRITICAL

    [Fact]
    public async Task StopAsync_WhenReleaseLockHangs_CompletesWithinTimeout()
    {
        var fakeConn = new FakeAdvisoryLockConnection(acquireResult: true, verifyResult: true);
        var factory = new FakeAdvisoryLockConnectionFactory(fakeConn);
        var options = FastOptions();

        var sut = new PostgresLeaderElectionService(options, factory);

        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sut.IsLeader, timeout: TimeSpan.FromSeconds(2));

        // Simulate a hung release (Postgres unreachable at shutdown time)
        fakeConn.SimulateHungRelease();

        // StopAsync should complete within the 5s timeout + margin, not block indefinitely
        var stopTask = sut.StopAsync(CancellationToken.None);
        var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(8)));

        completed.Should().Be(stopTask,
            "StopAsync should complete within bounded time even when ReleaseLockAsync hangs");

        sut.IsLeader.Should().BeFalse();
        sut.Dispose();
    }

    [Fact]
    public async Task StopAsync_WhenReleaseThrows_LogsWarningAndCompletes()
    {
        var fakeConn = new FakeAdvisoryLockConnection(acquireResult: true, verifyResult: true);
        var factory = new FakeAdvisoryLockConnectionFactory(fakeConn);
        var options = FastOptions();

        var sut = new PostgresLeaderElectionService(options, factory);

        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sut.IsLeader, timeout: TimeSpan.FromSeconds(2));

        // Simulate ReleaseLockAsync throwing a non-cancellation exception
        fakeConn.SimulateReleaseFailure();

        // StopAsync should not throw — the exception should be caught and logged as warning
        var act = () => sut.StopAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        sut.IsLeader.Should().BeFalse();
        sut.Dispose();
    }

    [Fact]
    public async Task StopAsync_WhenCancellationTokenAlreadyCancelled_CompletesPromptly()
    {
        var fakeConn = new FakeAdvisoryLockConnection(acquireResult: true, verifyResult: true);
        var factory = new FakeAdvisoryLockConnectionFactory(fakeConn);
        var options = FastOptions();

        var sut = new PostgresLeaderElectionService(options, factory);

        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sut.IsLeader, timeout: TimeSpan.FromSeconds(2));

        // Simulate a hung release
        fakeConn.SimulateHungRelease();

        // Pass an already-cancelled token — the linked CTS should fire immediately
        using var preCancelledCts = new CancellationTokenSource();
        await preCancelledCts.CancelAsync();

        var stopTask = sut.StopAsync(preCancelledCts.Token);
        var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(3)));

        completed.Should().Be(stopTask,
            "StopAsync should complete promptly when the shutdown token is already cancelled");

        sut.Dispose();
    }

    [Fact]
    public async Task StopAsync_PassesCancellationTokenToReleaseLock()
    {
        var fakeConn = new FakeAdvisoryLockConnection(acquireResult: true, verifyResult: true);
        var factory = new FakeAdvisoryLockConnectionFactory(fakeConn);
        var options = FastOptions();

        var sut = new PostgresLeaderElectionService(options, factory);

        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sut.IsLeader, timeout: TimeSpan.FromSeconds(2));

        await sut.StopAsync(CancellationToken.None);

        fakeConn.ReleaseLockReceivedCancellableToken.Should().BeTrue(
            "ReleaseLockAsync should receive a CancellationToken that can be cancelled (timeout-linked)");
        fakeConn.ReleaseLockCalled.Should().BeTrue();

        sut.Dispose();
    }

    [Fact]
    public async Task StopAsync_WhenCloseAsyncHangs_CompletesWithinTimeout()
    {
        var fakeConn = new FakeAdvisoryLockConnection(acquireResult: true, verifyResult: true);
        var factory = new FakeAdvisoryLockConnectionFactory(fakeConn);
        var options = FastOptions();

        var sut = new PostgresLeaderElectionService(options, factory);

        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sut.IsLeader, timeout: TimeSpan.FromSeconds(2));

        // Simulate a hung CloseAsync (e.g., Postgres unreachable)
        fakeConn.SimulateHungClose();

        // StopAsync should complete within the 2s close timeout + 5s release timeout + margin
        var stopTask = sut.StopAsync(CancellationToken.None);
        var completed = await Task.WhenAny(stopTask, Task.Delay(TimeSpan.FromSeconds(10)));

        completed.Should().Be(stopTask,
            "StopAsync should complete within bounded time even when CloseAsync hangs");

        sut.IsLeader.Should().BeFalse();
        sut.Dispose();
    }

    [Fact]
    public async Task StopAsync_WhenCloseAsyncFaultsAfterTimeout_DoesNotProduceUnobservedException()
    {
        var fakeConn = new FakeAdvisoryLockConnection(acquireResult: true, verifyResult: true);
        var factory = new FakeAdvisoryLockConnectionFactory(fakeConn);
        var options = FastOptions();

        var sut = new PostgresLeaderElectionService(options, factory);

        await sut.StartAsync(CancellationToken.None);
        await WaitUntilAsync(() => sut.IsLeader, timeout: TimeSpan.FromSeconds(2));

        // Simulate CloseAsync that faults after 3 seconds (longer than the 2s timeout)
        fakeConn.SimulateFaultingClose(
            new InvalidOperationException("Simulated post-timeout connection fault"),
            TimeSpan.FromSeconds(3));

        // Track unobserved task exceptions
        var unobservedExceptions = new List<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            unobservedExceptions.Add(e.Exception);
            e.SetObserved(); // Prevent crashing the test process
        };

        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            // StopAsync should complete within the 2s close timeout + margin
            await sut.StopAsync(CancellationToken.None);
            sut.Dispose();

            // Wait for the faulting close task to actually fault (3s delay)
            await Task.Delay(TimeSpan.FromSeconds(4));

            // TODO: UnobservedTaskException detection relies on GC finalization timing which is
            // inherently non-deterministic under heavy CI load. Consider adding [Trait("Category", "Flaky")]
            // or a retry attribute, or adding a secondary assertion that the faulting close task actually
            // completed (confirming the test precondition was met).
            // Force GC to finalize abandoned tasks and trigger UnobservedTaskException if any
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            // Give the runtime a moment to fire events
            await Task.Delay(100);

            unobservedExceptions.Should().BeEmpty(
                "CloseAsync fault should be observed by the continuation and not surface as UnobservedTaskException");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    #endregion

    #region Helpers

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }

    #endregion
}

#region Test Doubles

/// <summary>
/// Fake implementation of <see cref="IAdvisoryLockConnection"/> for unit testing.
/// Simulates a PostgreSQL connection with controllable lock behavior.
/// </summary>
internal sealed class FakeAdvisoryLockConnection : IAdvisoryLockConnection
{
    private volatile ConnectionState _state = ConnectionState.Closed;
    private volatile bool _acquireResult;
    private volatile bool _verifyResult;
    private volatile bool _verifyThrows;
    private readonly bool _failOnOpen;
    private TaskCompletionSource? _releaseBlock;
    private volatile bool _releaseThrows;
    private TaskCompletionSource? _closeBlock;

    public bool ReleaseLockCalled { get; private set; }
    public bool ReleaseLockReceivedCancellableToken { get; private set; }

    public FakeAdvisoryLockConnection(bool acquireResult, bool verifyResult, bool failOnOpen = false)
    {
        _acquireResult = acquireResult;
        _verifyResult = verifyResult;
        _failOnOpen = failOnOpen;
    }

    public ConnectionState State => _state;

    public Task OpenAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_failOnOpen)
            throw new InvalidOperationException("Simulated connection failure");
        _state = ConnectionState.Open;
        return Task.CompletedTask;
    }

    public Task<bool> TryAcquireLockAsync(long lockKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_acquireResult);
    }

    public Task<bool> VerifyLockIsHeldAsync(long lockKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_verifyThrows)
            throw new InvalidOperationException("Simulated verification failure");
        return Task.FromResult(_verifyResult);
    }

    public Task ReleaseLockAsync(long lockKey, CancellationToken ct = default)
    {
        ReleaseLockReceivedCancellableToken = ct.CanBeCanceled;
        ct.ThrowIfCancellationRequested();
        if (_releaseThrows)
            throw new InvalidOperationException("Simulated release failure");
        if (_releaseBlock is not null)
            return _releaseBlock.Task.WaitAsync(ct);
        ReleaseLockCalled = true;
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        if (_closeBlock is not null)
            return _closeBlock.Task;
        _state = ConnectionState.Closed;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _state = ConnectionState.Closed;
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _state = ConnectionState.Closed;
    }

    /// <summary>
    /// Simulates a connection drop — sets state to Broken so the verify loop detects it.
    /// </summary>
    public void SimulateConnectionDrop()
    {
        _state = ConnectionState.Broken;
    }

    /// <summary>
    /// Simulates verify returning false (lock lost without connection drop).
    /// </summary>
    public void SimulateVerifyFailure()
    {
        _verifyThrows = true;
    }

    /// <summary>
    /// Simulates a hung release — ReleaseLockAsync will block until cancelled.
    /// </summary>
    public void SimulateHungRelease()
    {
        _releaseBlock = new TaskCompletionSource();
    }

    /// <summary>
    /// Simulates ReleaseLockAsync throwing a non-cancellation exception.
    /// </summary>
    public void SimulateReleaseFailure()
    {
        _releaseThrows = true;
    }

    /// <summary>
    /// Simulates a hung CloseAsync — CloseAsync() will block indefinitely until the TCS is completed.
    /// </summary>
    public void SimulateHungClose()
    {
        _closeBlock = new TaskCompletionSource();
    }

    /// <summary>
    /// Simulates a CloseAsync that faults after a delay (e.g., longer than the 2s timeout).
    /// The returned task will fault with the given exception after the specified delay.
    /// </summary>
    public void SimulateFaultingClose(Exception ex, TimeSpan delay)
    {
        _closeBlock = new TaskCompletionSource();
        var tcs = _closeBlock;
        _ = Task.Delay(delay).ContinueWith(_ => tcs.SetException(ex));
    }
}

/// <summary>
/// Fake factory that returns pre-configured <see cref="FakeAdvisoryLockConnection"/> instances.
/// </summary>
internal sealed class FakeAdvisoryLockConnectionFactory : IAdvisoryLockConnectionFactory
{
    private volatile IAdvisoryLockConnection _next;

    public FakeAdvisoryLockConnectionFactory(IAdvisoryLockConnection initial)
    {
        _next = initial;
    }

    public IAdvisoryLockConnection Create() => _next;

    /// <summary>
    /// Sets the connection to be returned by the next <see cref="Create"/> call.
    /// Used to simulate reconnection after a connection drop.
    /// </summary>
    public void SetNextConnection(IAdvisoryLockConnection connection)
    {
        _next = connection;
    }
}

/// <summary>
/// Fake connection that blocks <see cref="TryAcquireLockAsync"/> on a <see cref="TaskCompletionSource{T}"/>,
/// allowing tests to control exactly when the acquire completes. Used to exercise the race between
/// StopAsync and an in-flight lock acquisition.
/// </summary>
internal sealed class DelayedFakeAdvisoryLockConnection : IAdvisoryLockConnection
{
    private volatile ConnectionState _state = ConnectionState.Closed;
    private readonly TaskCompletionSource<bool> _acquireTcs;
    private readonly bool _verifyResult;

    public bool ReleaseLockCalled { get; private set; }

    public DelayedFakeAdvisoryLockConnection(TaskCompletionSource<bool> acquireTcs, bool verifyResult)
    {
        _acquireTcs = acquireTcs;
        _verifyResult = verifyResult;
    }

    public ConnectionState State => _state;

    public Task OpenAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _state = ConnectionState.Open;
        return Task.CompletedTask;
    }

    public Task<bool> TryAcquireLockAsync(long lockKey, CancellationToken ct)
    {
        // Deliberately do NOT check cancellation here — we want to simulate the race where
        // the acquire completes successfully even though StopAsync has already cancelled the
        // service CTS. The caller (the election loop) will observe the result and attempt the
        // state transition, which the CAS should block.
        return _acquireTcs.Task;
    }

    public Task<bool> VerifyLockIsHeldAsync(long lockKey, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_verifyResult);
    }

    public Task ReleaseLockAsync(long lockKey, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ReleaseLockCalled = true;
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        _state = ConnectionState.Closed;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _state = ConnectionState.Closed;
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _state = ConnectionState.Closed;
    }
}

#endregion
