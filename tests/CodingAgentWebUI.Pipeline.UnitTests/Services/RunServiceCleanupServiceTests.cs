using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.TestUtilities;
using Moq;
using Serilog;
using StackExchange.Redis;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="RunServiceCleanupService"/>.
/// Each test gets a fresh <see cref="FakeRedisStore"/> via xUnit's per-test class instantiation —
/// no shared state, no <c>Reset()</c> needed.
/// </summary>
public sealed class RunServiceCleanupServiceTests
{
    private readonly FakeRedisStore _store = new();
    private readonly Mock<ILeaderElectionService> _leaderMock = new();

    private RunServiceCleanupService MakeService(ILeaderElectionService? leaderElection = null)
        => new(_store, Log.Logger, leaderElection);

    // ── Sweep logic ────────────────────────────────────────────────────────

    [Fact]
    public async Task SweepAsync_ExpiredRunHash_RemovedFromActiveSet()
    {
        // Arrange: run ID in runs:active but hash gone (hash TTL fired or Lua crashed)
        await _store.SetAddAsync("runs:active", "run-expired");
        // no HashSetAsync → run:run-expired does not exist

        await MakeService().SweepAsync(CancellationToken.None);

        Assert.DoesNotContain("run-expired", await _store.SetMembersAsync("runs:active"));
    }

    [Fact]
    public async Task SweepAsync_LiveRunHash_NotRemovedFromActiveSet()
    {
        await _store.SetAddAsync("runs:active", "run-alive");
        await _store.HashSetAsync("run:run-alive",
        [
            new HashEntry("runId", "run-alive"),
            new HashEntry("issueIdentifier", "org/repo#1")
        ]);

        await MakeService().SweepAsync(CancellationToken.None);

        Assert.Contains("run-alive", await _store.SetMembersAsync("runs:active"));
    }

    [Fact]
    public async Task SweepAsync_MixedRuns_OnlyExpiredRemoved()
    {
        await _store.SetAddAsync("runs:active", "run-alive");
        await _store.HashSetAsync("run:run-alive",
            [new HashEntry("runId", "run-alive")]);

        await _store.SetAddAsync("runs:active", "run-dead");
        // no hash for run-dead

        await MakeService().SweepAsync(CancellationToken.None);

        var active = await _store.SetMembersAsync("runs:active");
        Assert.Contains("run-alive", active);
        Assert.DoesNotContain("run-dead", active);
    }

    [Fact]
    public async Task SweepAsync_EmptyActiveSet_NoOp()
    {
        // No runs — should complete without exception
        var ex = await Record.ExceptionAsync(() => MakeService().SweepAsync(CancellationToken.None));
        Assert.Null(ex);
    }

    [Fact]
    public async Task SweepAsync_AfterSweep_LiveRunAddedBetweenSweeps_NotRemovedOnSecondSweep()
    {
        // First sweep removes expired run. A run added between sweeps must survive.
        await _store.SetAddAsync("runs:active", "run-old-expired");

        var sut = MakeService();
        await sut.SweepAsync(CancellationToken.None);

        // New live run added after first sweep
        await _store.SetAddAsync("runs:active", "run-new-live");
        await _store.HashSetAsync("run:run-new-live",
            [new HashEntry("runId", "run-new-live")]);

        await sut.SweepAsync(CancellationToken.None);

        var active = await _store.SetMembersAsync("runs:active");
        Assert.DoesNotContain("run-old-expired", active);
        Assert.Contains("run-new-live", active);
    }

    [Fact]
    public async Task SweepAsync_CancelledToken_ThrowsOperationCancelledException()
    {
        // Arrange: put something in runs:active so the foreach body is entered
        await _store.SetAddAsync("runs:active", "run-1");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => MakeService().SweepAsync(cts.Token));
    }

    // ── Leader-election gating ─────────────────────────────────────────────

    [Fact]
    public async Task SweepAsync_WhenLeader_RunsSweep()
    {
        await _store.SetAddAsync("runs:active", "run-stale");

        _leaderMock.Setup(l => l.IsLeader).Returns(true);
        await MakeService(_leaderMock.Object).SweepAsync(CancellationToken.None);

        Assert.DoesNotContain("run-stale", await _store.SetMembersAsync("runs:active"));
    }

    [Fact]
    public async Task SweepAsync_WhenNotLeader_SkipsSweep()
    {
        await _store.SetAddAsync("runs:active", "run-stale");

        _leaderMock.Setup(l => l.IsLeader).Returns(false);
        await MakeService(_leaderMock.Object).SweepAsync(CancellationToken.None);

        Assert.Contains("run-stale", await _store.SetMembersAsync("runs:active"));
    }

    [Fact]
    public async Task SweepAsync_WhenNoLeaderService_AlwaysSweeps()
    {
        // null service = local dev / single-replica → sweep unconditionally
        await _store.SetAddAsync("runs:active", "run-stale");

        await MakeService(leaderElection: null).SweepAsync(CancellationToken.None);

        Assert.DoesNotContain("run-stale", await _store.SetMembersAsync("runs:active"));
    }

    [Fact]
    public async Task SweepAsync_LeadershipLostBetweenSweeps_SecondSweepSkipped()
    {
        await _store.SetAddAsync("runs:active", "run-a");

        _leaderMock.SetupSequence(l => l.IsLeader)
            .Returns(true)   // first sweep: leader
            .Returns(false); // second sweep: no longer leader

        var sut = MakeService(_leaderMock.Object);

        // First sweep (leader): removes run-a
        await sut.SweepAsync(CancellationToken.None);
        Assert.DoesNotContain("run-a", await _store.SetMembersAsync("runs:active"));

        // Expired run added AFTER first sweep — second sweep must not touch it
        await _store.SetAddAsync("runs:active", "run-b");

        // Second sweep (not leader): skips
        await sut.SweepAsync(CancellationToken.None);
        Assert.Contains("run-b", await _store.SetMembersAsync("runs:active"));
    }
}
