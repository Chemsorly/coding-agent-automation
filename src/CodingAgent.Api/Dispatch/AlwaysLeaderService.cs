using CodingAgent.Pipeline.LeaderElection;

namespace CodingAgent.Api.Dispatch;

/// <summary>
/// An <see cref="ILeaderElectionService"/> implementation that is always the leader.
/// Used in the API host where all replicas are allowed to dispatch Pending WorkItems.
/// Race safety is provided by the CAS <c>TransitionIfAsync(Pending → Dispatched)</c>
/// in <see cref="DispatchLifecycleService"/> — only one replica will succeed claiming
/// the same Pending item; the second gets a no-op and moves on.
/// </summary>
internal sealed class AlwaysLeaderService : ILeaderElectionService
{
    /// <inheritdoc />
    public bool IsLeader => true;

    /// <inheritdoc />
    public CancellationToken LeaderToken => CancellationToken.None;
}
