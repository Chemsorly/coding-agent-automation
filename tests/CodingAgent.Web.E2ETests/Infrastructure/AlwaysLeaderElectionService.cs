using CodingAgent.Pipeline.LeaderElection;

namespace CodingAgent.Web.E2ETests.Infrastructure;

/// <summary>
/// Test-only stub for <see cref="ILeaderElectionService"/> that always reports this instance
/// as the leader. Used by <see cref="JobControllerE2EWebApplicationFactory"/> to avoid the
/// Kubernetes lease acquisition that the production <see cref="CodingAgent.Pipeline.LeaderElection.LeaderElectionService"/>
/// requires.
///
/// <para>
/// <c>LeaderElectionService</c> is <c>sealed</c> — it cannot be subclassed. This stub
/// is the only correct approach for in-process E2E hosting without a live K8s cluster.
/// </para>
/// </summary>
// TODO [WARNING]: AlwaysLeaderElectionService does not implement IDisposable, so _cts
// (which holds an unmanaged kernel wait handle) is never released. Add IDisposable and
// call _cts.Dispose() there, or replace _cts with CancellationToken.None since the token
// is permanently non-cancellable by design.
internal sealed class AlwaysLeaderElectionService : ILeaderElectionService
{
    // A CTS that is never cancelled — LeaderToken is permanently valid (never fires leadership loss).
    private readonly CancellationTokenSource _cts = new();

    /// <inheritdoc/>
    public bool IsLeader => true;

    /// <inheritdoc/>
    public CancellationToken LeaderToken => _cts.Token;
}
