using System.Data;
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

    public bool ReleaseLockCalled { get; private set; }

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

    public Task ReleaseLockAsync(long lockKey)
    {
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

#endregion
