using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.LeaderElection;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="PostgresLeaderElectionService"/>.
/// Tests the state machine logic, event firing, and lifecycle management.
/// Does NOT require a real Postgres connection — tests exercise the service's
/// public contract and internal coordination via controlled timing.
/// </summary>
// TODO: [REVIEW] Tests for TryAcquireLockAsync and VerifyLockHeldAsync only cover the null-guard
// early return paths (no connection → return false). The actual SQL execution paths
// (pg_try_advisory_lock returning false when another replica holds the lock, pg_locks query returning
// false when lock is lost) are exercised only via test hooks in the state machine tests above.
// Consider adding integration tests with a real Postgres instance (e.g., Testcontainers) to verify
// the actual SQL commands work correctly end-to-end.
public sealed class PostgresLeaderElectionServiceTests : IDisposable
{
    private readonly PostgresLeaderElectionOptions _options = new()
    {
        RenewalInterval = TimeSpan.FromMilliseconds(50),
        RetryInterval = TimeSpan.FromMilliseconds(50)
    };

    // We use "Host=__invalid__" to ensure no real connection is made.
    // The service will fail to connect and stay as non-leader.
    private const string InvalidConnectionString = "Host=__invalid__;Port=5432;Database=test;Username=test;Password=test";
    private const string LocalhostConnectionString = "Host=localhost;Port=5432;Database=test;Username=test;Password=test";

    public void Dispose()
    {
        // No-op, individual tests dispose their services
    }

    [Fact]
    public void InitialState_IsNotLeader()
    {
        using var svc = new PostgresLeaderElectionService(InvalidConnectionString, _options);

        svc.IsLeader.Should().BeFalse();
        svc.LeaderToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void LockKey_IsWellKnownConstant()
    {
        // Verify the lock key is the expected well-known constant
        PostgresLeaderElectionService.LockKey.Should().Be(0x0CAA_1EAD);
    }

    [Fact]
    public async Task StartAsync_DoesNotThrow_WithInvalidConnection()
    {
        using var svc = new PostgresLeaderElectionService(InvalidConnectionString, _options);

        // StartAsync should not throw — it kicks off the background loop
        await svc.StartAsync(CancellationToken.None);

        // Service is running but not leader (can't connect)
        svc.IsLeader.Should().BeFalse();

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_CancelsLeaderToken_WhenNotLeader()
    {
        using var svc = new PostgresLeaderElectionService(InvalidConnectionString, _options);

        await svc.StartAsync(CancellationToken.None);

        // LeaderToken should be cancelled (never became leader)
        svc.LeaderToken.IsCancellationRequested.Should().BeTrue();

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_CompletesGracefully_WhenServiceNeverStarted()
    {
        using var svc = new PostgresLeaderElectionService(InvalidConnectionString, _options);

        // Stopping without starting should be no-op
        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_FiresStoppedLeading_WhenLeader()
    {
        using var svc = new PostgresLeaderElectionService(InvalidConnectionString, _options);
        svc.TestEnsureConnectionHook = _ => Task.FromResult(true);
        svc.TestTryAcquireLockHook = _ => Task.FromResult(true);
        svc.TestVerifyLockHeldHook = _ => Task.FromResult(true);

        var stoppedFired = false;
        svc.OnStoppedLeading += () => stoppedFired = true;

        await svc.StartAsync(CancellationToken.None);

        // Wait for leadership to be acquired
        await WaitForConditionAsync(() => svc.IsLeader, timeout: TimeSpan.FromSeconds(2));
        svc.IsLeader.Should().BeTrue();

        await svc.StopAsync(CancellationToken.None);

        stoppedFired.Should().BeTrue();
        svc.IsLeader.Should().BeFalse();
    }

    [Fact]
    public async Task ConnectionFailure_KeepsServiceRunning_WithoutBecomingLeader()
    {
        using var svc = new PostgresLeaderElectionService(InvalidConnectionString, _options);

        var startedCount = 0;
        svc.OnStartedLeading += () => Interlocked.Increment(ref startedCount);

        await svc.StartAsync(CancellationToken.None);

        // Wait for several retry cycles
        await Task.Delay(300);

        // Should never become leader
        svc.IsLeader.Should().BeFalse();
        startedCount.Should().Be(0);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LeaderToken_IsCancelled_WhenNotLeader()
    {
        using var svc = new PostgresLeaderElectionService(InvalidConnectionString, _options);

        await svc.StartAsync(CancellationToken.None);
        await Task.Delay(100);

        // Should have a cancelled token since we never became leader
        svc.LeaderToken.IsCancellationRequested.Should().BeTrue();

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Constructor_ThrowsOnNullConnectionString()
    {
        var act = () => new PostgresLeaderElectionService(null!, _options);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyConnectionString()
    {
        var act = () => new PostgresLeaderElectionService("", _options);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_ThrowsOnWhitespaceConnectionString()
    {
        var act = () => new PostgresLeaderElectionService("   ", _options);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task MultipleStartStop_DoesNotThrow()
    {
        using var svc = new PostgresLeaderElectionService(InvalidConnectionString, _options);

        // Start/Stop cycles should not throw or leak
        await svc.StartAsync(CancellationToken.None);
        await svc.StopAsync(CancellationToken.None);

        // Second cycle (re-create needed since CTS are disposed)
        using var svc2 = new PostgresLeaderElectionService(InvalidConnectionString, _options);
        await svc2.StartAsync(CancellationToken.None);
        await svc2.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EnsureConnectionAsync_ReturnsFalse_WhenCannotConnect()
    {
        using var svc = new PostgresLeaderElectionService(InvalidConnectionString, _options);

        var result = await svc.EnsureConnectionAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TryAcquireLockAsync_ReturnsFalse_WhenNoConnection()
    {
        using var svc = new PostgresLeaderElectionService(InvalidConnectionString, _options);

        var result = await svc.TryAcquireLockAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    // TODO: TryAcquireLockAsync_ReturnsFalse_WhenNoConnection only tests the null-guard early return.
    // It does not test the SQL execution path where pg_try_advisory_lock returns false (another
    // replica holds the lock). The production code branch handling acquired==false is exercised
    // by AcquireLock_ReturnsTrue_WhenHookReturnsTrue below via the test hook.

    [Fact]
    public async Task VerifyLockHeldAsync_ReturnsFalse_WhenNoConnection()
    {
        using var svc = new PostgresLeaderElectionService(InvalidConnectionString, _options);

        var result = await svc.VerifyLockHeldAsync(CancellationToken.None);

        result.Should().BeFalse();
    }

    // TODO: VerifyLockHeldAsync_ReturnsFalse_WhenNoConnection only tests the null-guard. The actual
    // pg_locks query path and the scenario where verification returns false (lock lost) are tested
    // via the test hooks in the state machine tests below.

    [Fact]
    public void ILeaderElectionService_Contract_IsImplemented()
    {
        // Verify the service implements the interface
        ILeaderElectionService svc = new PostgresLeaderElectionService(InvalidConnectionString, _options);
        svc.Should().NotBeNull();
        svc.IsLeader.Should().BeFalse();
        svc.LeaderToken.IsCancellationRequested.Should().BeTrue();
        ((IDisposable)svc).Dispose();
    }

    [Fact]
    // TODO: [REVIEW] This test is tautological — it subscribes handlers, immediately unsubscribes,
    // then asserts handlers were never called. Since nothing triggers the events, the assertion passes
    // regardless of whether unsubscription works. To be meaningful, this should trigger an event after
    // unsubscribing and verify the handler is NOT called.
    public void Events_CanBeSubscribedAndUnsubscribed()
    {
        using var svc = new PostgresLeaderElectionService(InvalidConnectionString, _options);

        var started = false;
        var stopped = false;

        Action startHandler = () => started = true;
        Action stopHandler = () => stopped = true;

        svc.OnStartedLeading += startHandler;
        svc.OnStoppedLeading += stopHandler;

        // Unsubscribe
        svc.OnStartedLeading -= startHandler;
        svc.OnStoppedLeading -= stopHandler;

        started.Should().BeFalse();
        stopped.Should().BeFalse();
    }

    [Fact]
    public async Task Dispose_IsIdempotent()
    {
        var svc = new PostgresLeaderElectionService(InvalidConnectionString, _options);
        await svc.StartAsync(CancellationToken.None);
        await svc.StopAsync(CancellationToken.None);

        // Should not throw on double-dispose
        svc.Dispose();
        svc.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // State Machine Tests — exercise successful leadership acquisition,
    // loss, and re-acquisition using internal test hooks.
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task AcquireLock_BecomesLeader_WhenConnectionAndLockSucceed()
    {
        using var svc = new PostgresLeaderElectionService(LocalhostConnectionString, _options);
        svc.TestEnsureConnectionHook = _ => Task.FromResult(true);
        svc.TestTryAcquireLockHook = _ => Task.FromResult(true);
        svc.TestVerifyLockHeldHook = _ => Task.FromResult(true);

        var startedFired = false;
        svc.OnStartedLeading += () => startedFired = true;

        await svc.StartAsync(CancellationToken.None);

        // Wait for leadership acquisition
        await WaitForConditionAsync(() => svc.IsLeader, timeout: TimeSpan.FromSeconds(2));

        svc.IsLeader.Should().BeTrue();
        svc.LeaderToken.IsCancellationRequested.Should().BeFalse();
        startedFired.Should().BeTrue();

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LeaderToken_IsValid_WhenLeader()
    {
        using var svc = new PostgresLeaderElectionService(LocalhostConnectionString, _options);
        svc.TestEnsureConnectionHook = _ => Task.FromResult(true);
        svc.TestTryAcquireLockHook = _ => Task.FromResult(true);
        svc.TestVerifyLockHeldHook = _ => Task.FromResult(true);

        await svc.StartAsync(CancellationToken.None);
        await WaitForConditionAsync(() => svc.IsLeader, timeout: TimeSpan.FromSeconds(2));

        // LeaderToken should be live (not cancelled)
        svc.LeaderToken.IsCancellationRequested.Should().BeFalse();

        // After stop, it should be cancelled
        await svc.StopAsync(CancellationToken.None);
        svc.LeaderToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task LeadershipLost_WhenVerifyLockFails()
    {
        var verifyResult = true;
        using var svc = new PostgresLeaderElectionService(LocalhostConnectionString, _options);
        svc.TestEnsureConnectionHook = _ => Task.FromResult(true);
        svc.TestTryAcquireLockHook = _ => Task.FromResult(true);
        svc.TestVerifyLockHeldHook = _ => Task.FromResult(Volatile.Read(ref verifyResult));

        var stoppedFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.OnStoppedLeading += () => stoppedFired.TrySetResult();

        await svc.StartAsync(CancellationToken.None);
        await WaitForConditionAsync(() => svc.IsLeader, timeout: TimeSpan.FromSeconds(2));

        svc.IsLeader.Should().BeTrue();

        // Simulate lock verification failure (e.g., connection dropped)
        Volatile.Write(ref verifyResult, false);

        // Wait for the service to detect and fire OnStoppedLeading
        await stoppedFired.Task.WaitAsync(TimeSpan.FromSeconds(2));

        svc.IsLeader.Should().BeFalse();
        svc.LeaderToken.IsCancellationRequested.Should().BeTrue();

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LeadershipLost_WhenConnectionDrops()
    {
        var connectionAlive = true;
        using var svc = new PostgresLeaderElectionService(LocalhostConnectionString, _options);
        svc.TestEnsureConnectionHook = _ => Task.FromResult(Volatile.Read(ref connectionAlive));
        svc.TestTryAcquireLockHook = _ => Task.FromResult(true);
        svc.TestVerifyLockHeldHook = _ =>
        {
            if (!Volatile.Read(ref connectionAlive))
                throw new InvalidOperationException("Connection broken");
            return Task.FromResult(true);
        };

        var stoppedFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        svc.OnStoppedLeading += () => stoppedFired.TrySetResult();

        await svc.StartAsync(CancellationToken.None);
        await WaitForConditionAsync(() => svc.IsLeader, timeout: TimeSpan.FromSeconds(2));

        svc.IsLeader.Should().BeTrue();

        // Simulate connection drop
        Volatile.Write(ref connectionAlive, false);

        // Wait for the service to detect and fire OnStoppedLeading
        await stoppedFired.Task.WaitAsync(TimeSpan.FromSeconds(2));

        svc.IsLeader.Should().BeFalse();

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ReacquiresLeadership_AfterConnectionDrop()
    {
        var connectionAlive = true;
        var startedCount = 0;
        var stoppedCount = 0;

        using var svc = new PostgresLeaderElectionService(LocalhostConnectionString, _options);
        svc.TestEnsureConnectionHook = _ => Task.FromResult(Volatile.Read(ref connectionAlive));
        svc.TestTryAcquireLockHook = _ => Task.FromResult(Volatile.Read(ref connectionAlive));
        svc.TestVerifyLockHeldHook = _ =>
        {
            if (!Volatile.Read(ref connectionAlive))
                throw new InvalidOperationException("Connection broken");
            return Task.FromResult(true);
        };

        svc.OnStartedLeading += () => Interlocked.Increment(ref startedCount);
        svc.OnStoppedLeading += () => Interlocked.Increment(ref stoppedCount);

        await svc.StartAsync(CancellationToken.None);

        // Phase 1: Acquire leadership
        await WaitForConditionAsync(() => svc.IsLeader, timeout: TimeSpan.FromSeconds(2));
        svc.IsLeader.Should().BeTrue();
        Interlocked.CompareExchange(ref startedCount, 0, 0).Should().Be(1);

        // Phase 2: Drop connection → lose leadership
        Volatile.Write(ref connectionAlive, false);
        await WaitForConditionAsync(() => !svc.IsLeader, timeout: TimeSpan.FromSeconds(2));
        svc.IsLeader.Should().BeFalse();
        Interlocked.CompareExchange(ref stoppedCount, 0, 0).Should().Be(1);

        // Phase 3: Restore connection → re-acquire leadership
        Volatile.Write(ref connectionAlive, true);
        await WaitForConditionAsync(() => svc.IsLeader, timeout: TimeSpan.FromSeconds(2));
        svc.IsLeader.Should().BeTrue();
        Interlocked.CompareExchange(ref startedCount, 0, 0).Should().Be(2);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OnStartedLeading_FiresExactlyOnce_PerAcquisition()
    {
        using var svc = new PostgresLeaderElectionService(LocalhostConnectionString, _options);
        svc.TestEnsureConnectionHook = _ => Task.FromResult(true);
        svc.TestTryAcquireLockHook = _ => Task.FromResult(true);
        svc.TestVerifyLockHeldHook = _ => Task.FromResult(true);

        var startedCount = 0;
        svc.OnStartedLeading += () => Interlocked.Increment(ref startedCount);

        await svc.StartAsync(CancellationToken.None);
        await WaitForConditionAsync(() => svc.IsLeader, timeout: TimeSpan.FromSeconds(2));

        // Wait a bit to ensure no double-firing during renewal cycles
        await Task.Delay(200);

        Interlocked.CompareExchange(ref startedCount, 0, 0).Should().Be(1);

        await svc.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task OnStoppedLeading_FiresExactlyOnce_OnStop()
    {
        using var svc = new PostgresLeaderElectionService(LocalhostConnectionString, _options);
        svc.TestEnsureConnectionHook = _ => Task.FromResult(true);
        svc.TestTryAcquireLockHook = _ => Task.FromResult(true);
        svc.TestVerifyLockHeldHook = _ => Task.FromResult(true);

        var stoppedCount = 0;
        svc.OnStoppedLeading += () => Interlocked.Increment(ref stoppedCount);

        await svc.StartAsync(CancellationToken.None);
        await WaitForConditionAsync(() => svc.IsLeader, timeout: TimeSpan.FromSeconds(2));

        await svc.StopAsync(CancellationToken.None);

        Interlocked.CompareExchange(ref stoppedCount, 0, 0).Should().Be(1);
    }

    [Fact]
    public async Task LockNotAcquired_StaysNonLeader_AndRetries()
    {
        var acquireCount = 0;
        using var svc = new PostgresLeaderElectionService(LocalhostConnectionString, _options);
        svc.TestEnsureConnectionHook = _ => Task.FromResult(true);
        svc.TestTryAcquireLockHook = _ =>
        {
            Interlocked.Increment(ref acquireCount);
            return Task.FromResult(false); // Another replica holds the lock
        };
        svc.TestVerifyLockHeldHook = _ => Task.FromResult(true);

        await svc.StartAsync(CancellationToken.None);

        // Wait for several retry cycles
        await Task.Delay(250);

        svc.IsLeader.Should().BeFalse();
        // Should have retried multiple times
        Interlocked.CompareExchange(ref acquireCount, 0, 0).Should().BeGreaterThan(2);

        await svc.StopAsync(CancellationToken.None);
    }

    // TODO: EnsureConnectionAsync_ReturnsFalse_WhenCannotConnect only validates the failure path.
    // The success path (connection opens, returns true) is tested via hooks in the state machine tests.

    // TODO: ConnectionFailure_KeepsServiceRunning_WithoutBecomingLeader waits 300ms then asserts
    // IsLeader is false — this would pass even if the election loop crashed. The LockNotAcquired
    // test above verifies the loop is actively retrying via acquire count tracking.

    // TODO: MultipleStartStop_DoesNotThrow creates a NEW instance for the second cycle instead of
    // restarting the same one. Comment explains CTS are disposed after stop (design limitation).

    // ═══════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
    }
}
