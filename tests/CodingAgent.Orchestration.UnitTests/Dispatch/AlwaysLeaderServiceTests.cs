using AwesomeAssertions;
using CodingAgent.Api.Dispatch;
using Xunit;

namespace CodingAgent.Orchestration.UnitTests.Dispatch;

/// <summary>
/// Contract tests for <see cref="AlwaysLeaderService"/>: it is permanently the leader and never
/// signals leadership loss. This is what lets every API replica run
/// <see cref="WorkItemDispatchService"/> simultaneously — race safety comes from the CAS
/// Pending→Dispatched transition, not from leader election, so the leader token must never fire.
/// </summary>
[Trait("Feature", "WorkItemDispatchService")]
public class AlwaysLeaderServiceTests
{
    [Fact]
    public void IsLeader_IsAlwaysTrue()
    {
        var sut = new AlwaysLeaderService();

        sut.IsLeader.Should().BeTrue("all API replicas are permitted to dispatch Pending WorkItems");
    }

    [Fact]
    public void LeaderToken_IsNeverCancelled()
    {
        var sut = new AlwaysLeaderService();

        // Leadership is never lost; the host lifetime token is the sole stop signal, so the
        // leader token must be CancellationToken.None and never enter the cancelled state.
        sut.LeaderToken.Should().Be(CancellationToken.None);
        sut.LeaderToken.IsCancellationRequested.Should().BeFalse();
    }
}
