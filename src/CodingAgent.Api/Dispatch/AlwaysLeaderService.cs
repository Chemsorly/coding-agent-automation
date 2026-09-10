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

    /// <summary>
    /// Returns <see cref="CancellationToken.None"/> intentionally.
    /// <para>
    /// In a real <see cref="LeaderElectionService"/> this token is cancelled when leadership
    /// is lost, allowing in-flight work to abort. <see cref="WorkItemDispatchService"/> runs
    /// on all API replicas simultaneously (no single leader) — shutdown is signalled exclusively
    /// via the host <c>CancellationToken</c> passed to <c>OnPollCycleAsync</c>. The
    /// <c>!leaderElection.IsLeader</c> yield-break guard in
    /// <see cref="CodingAgent.Api.Dispatch.DispatchStateBuilder.GetEligibleCandidatesAsync"/>
    /// never fires because <see cref="IsLeader"/> is permanently <c>true</c>; the host lifetime
    /// token is the sole stop signal.
    /// </para>
    /// </summary>
    public CancellationToken LeaderToken => CancellationToken.None;
}
