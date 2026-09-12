using CodingAgent.Pipeline.LeaderElection;

namespace CodingAgent.Api.Dispatch;

/// <summary>
/// An <see cref="ILeaderElectionService"/> implementation that is always the leader.
/// Used in the API host where all replicas are allowed to dispatch Pending WorkItems.
/// </summary>
/// <remarks>
/// <para>
/// <b>Race safety (single item):</b> the CAS <c>TransitionIfAsync(Pending → Dispatched)</c>
/// in <see cref="DispatchLifecycleService"/> prevents the same Pending item from being claimed
/// by two replicas simultaneously — only the first write succeeds; the second is a no-op.
/// </para>
/// <para>
/// <b>Known limitation (concurrency cap across items):</b> with multiple API replicas,
/// <c>maxConcurrent</c> per selector is not guaranteed. Each replica builds its concurrency
/// snapshot independently from the DB. Two replicas can both read <c>current = N &lt; maxConcurrent</c>
/// and each dispatch a different Pending item for the same selector, resulting in
/// <c>N+2</c> active pods where <c>N+1</c> is the cap. This is an accepted trade-off for the
/// single-process deployment target; a distributed advisory lock (e.g. Postgres advisory lock)
/// would be required to close the gap in a true multi-replica setup.
/// </para>
/// </remarks>
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
