using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.LeaderElection;
using Microsoft.Extensions.Options;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

public class PostgresLeaderElectionServiceTests
{
    private static PostgresLeaderElectionOptions CreateOptions(Action<PostgresLeaderElectionOptions>? configure = null)
    {
        var opts = new PostgresLeaderElectionOptions();
        configure?.Invoke(opts);
        return opts;
    }

    [Fact]
    public void IsLeader_DefaultsToFalse()
    {
        var sut = new PostgresLeaderElectionService("Host=localhost;Database=test", CreateOptions());
        sut.IsLeader.Should().BeFalse();
    }

    [Fact]
    public void LeaderToken_WhenNotStarted_IsCancelled()
    {
        var sut = new PostgresLeaderElectionService("Host=localhost;Database=test", CreateOptions());
        sut.LeaderToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void Constructor_ThrowsOnNullConnectionString()
    {
        var act = () => new PostgresLeaderElectionService(null!, CreateOptions());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_ThrowsOnEmptyConnectionString()
    {
        var act = () => new PostgresLeaderElectionService("", CreateOptions());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_ThrowsOnWhitespaceConnectionString()
    {
        var act = () => new PostgresLeaderElectionService("   ", CreateOptions());
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Options_DefaultValues_AreCorrect()
    {
        var opts = new PostgresLeaderElectionOptions();

        opts.RenewalInterval.Should().Be(TimeSpan.FromSeconds(5));
        opts.AcquireRetryInterval.Should().Be(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void LockKey_IsWellKnownConstant()
    {
        // Verify the lock key is a distinctive constant (0xCAA_1EAD)
        PostgresLeaderElectionService.LockKey.Should().Be(0xCAA_1EAD);
    }

    [Fact]
    public void Dispose_WithoutStart_DoesNotThrow()
    {
        var sut = new PostgresLeaderElectionService("Host=localhost;Database=test", CreateOptions());

        var act = () => sut.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public async Task StopAsync_WithoutStart_DoesNotThrow()
    {
        var sut = new PostgresLeaderElectionService("Host=localhost;Database=test", CreateOptions());

        var act = () => sut.StopAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_SetsUpElectionLoop()
    {
        // StartAsync should not throw even if the DB is unreachable
        // (the loop handles connection errors internally)
        var sut = new PostgresLeaderElectionService("Host=localhost;Port=1;Database=test;Timeout=1", CreateOptions(o =>
        {
            o.AcquireRetryInterval = TimeSpan.FromMilliseconds(50);
        }));

        await sut.StartAsync(CancellationToken.None);

        // IsLeader remains false since connection cannot be established
        sut.IsLeader.Should().BeFalse();

        // Clean stop
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await sut.StopAsync(cts.Token);
        sut.Dispose();
    }

    [Fact]
    public async Task StartAsync_ThenStopAsync_LeaderTokenCancelled()
    {
        var sut = new PostgresLeaderElectionService("Host=localhost;Port=1;Database=test;Timeout=1", CreateOptions(o =>
        {
            o.AcquireRetryInterval = TimeSpan.FromMilliseconds(50);
        }));

        await sut.StartAsync(CancellationToken.None);

        // After stop, the leader token should be cancelled
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await sut.StopAsync(cts.Token);

        sut.LeaderToken.IsCancellationRequested.Should().BeTrue();
        sut.Dispose();
    }

    // TODO: This test exercises the SimulateAcquireForTesting helper, not the real acquisition path
    // (RunElectionLoopAsync). It would not fail if the real loop forgot to fire OnStartedLeading after
    // pg_try_advisory_lock succeeds. Integration tests with a real Postgres instance are needed to
    // cover the actual event-firing path.
    [Fact]
    public void OnStartedLeading_FiresWhenLeadershipAcquired()
    {
        var sut = new PostgresLeaderElectionService("Host=localhost;Database=test", CreateOptions());
        var fired = false;
        sut.OnStartedLeading += () => fired = true;

        // Simulate leadership acquisition to verify the event fires
        sut.SimulateAcquireForTesting();

        sut.IsLeader.Should().BeTrue();
        fired.Should().BeTrue("OnStartedLeading should fire when leadership is acquired");
        sut.Dispose();
    }

    [Fact]
    public async Task OnStoppedLeading_FiresWhenLeadershipLost()
    {
        var sut = new PostgresLeaderElectionService("Host=localhost;Database=test", CreateOptions());
        var firedStarted = false;
        var firedStopped = false;
        sut.OnStartedLeading += () => firedStarted = true;
        sut.OnStoppedLeading += () => firedStopped = true;

        // Acquire then lose leadership to verify events fire in sequence
        sut.SimulateAcquireForTesting();
        firedStarted.Should().BeTrue();

        await sut.SimulateLoseForTestingAsync();

        sut.IsLeader.Should().BeFalse();
        firedStopped.Should().BeTrue("OnStoppedLeading should fire when leadership is lost");
        sut.Dispose();
    }

    // TODO: This test is a duplicate of PostgresLeaderElectionService_ImplementsInterface in ILeaderElectionServiceTests.cs. Consider removing.
    [Fact]
    public void ImplementsILeaderElectionService()
    {
        var sut = new PostgresLeaderElectionService("Host=localhost;Database=test", CreateOptions());
        sut.Should().BeAssignableTo<ILeaderElectionService>();
        sut.Dispose();
    }

    // TODO: This test only exercises SimulateAcquire/SimulateLose helpers — it doesn't test the real
    // connection-drop detection in RunElectionLoopAsync. The test would pass even if the election loop
    // silently swallowed connection failures. Integration tests with a real Postgres are needed to verify
    // actual connection-drop detection and state transition.
    [Fact]
    public async Task ConnectionDrop_LosesLeadership()
    {
        // Verify the contract: when leadership is held and then lost (simulating connection drop),
        // IsLeader transitions to false, LeaderToken is cancelled, and OnStoppedLeading fires.
        var sut = new PostgresLeaderElectionService("Host=localhost;Database=test", CreateOptions());
        var stoppedFired = false;
        sut.OnStoppedLeading += () => stoppedFired = true;

        // Simulate having leadership
        sut.SimulateAcquireForTesting();
        sut.IsLeader.Should().BeTrue();
        var leaderToken = sut.LeaderToken;
        leaderToken.IsCancellationRequested.Should().BeFalse();

        // Simulate losing leadership (as happens on connection drop)
        await sut.SimulateLoseForTestingAsync();

        sut.IsLeader.Should().BeFalse("leadership should be lost when connection drops");
        leaderToken.IsCancellationRequested.Should().BeTrue("the LeaderToken held during leadership should be cancelled");
        stoppedFired.Should().BeTrue("OnStoppedLeading should fire when leadership is lost");
        sut.Dispose();
    }

    // TODO: This test is nearly identical to StartAsync_ThenStopAsync_LeaderTokenCancelled. Consider consolidating.
    // TODO: Missing test for leadership re-acquisition after connection loss (acceptance criteria requirement).
    // A test should verify: acquire → lose → re-acquire state machine round-trip, confirming IsLeader,
    // LeaderToken, and OnStartedLeading/OnStoppedLeading events fire correctly through the full cycle.
    [Fact]
    public async Task MultipleStartStop_DoesNotThrow()
    {
        var sut = new PostgresLeaderElectionService(
            "Host=localhost;Port=1;Database=test;Timeout=1",
            CreateOptions(o => o.AcquireRetryInterval = TimeSpan.FromMilliseconds(50)));

        // Start, stop, verify clean lifecycle
        await sut.StartAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await sut.StopAsync(cts.Token);

        sut.IsLeader.Should().BeFalse();
        sut.LeaderToken.IsCancellationRequested.Should().BeTrue();
        sut.Dispose();
    }
}
