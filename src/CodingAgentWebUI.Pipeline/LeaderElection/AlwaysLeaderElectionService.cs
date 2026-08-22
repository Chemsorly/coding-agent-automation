namespace CodingAgentWebUI.Pipeline.LeaderElection;

/// <summary>
/// Single-instance no-op leader election service for deployments without Kubernetes
/// lease infrastructure. Always reports itself as the leader.
/// Used in test environments and single-instance deployments where
/// <see cref="LeaderElectedPollingService"/> needs an <see cref="ILeaderElectionService"/>
/// but real leader election is not required. This is a pure null-object —
/// it does not implement <see cref="Microsoft.Extensions.Hosting.IHostedService"/>.
/// </summary>
public sealed class AlwaysLeaderElectionService : ILeaderElectionService
{
    /// <inheritdoc />
    public bool IsLeader => true;

    /// <inheritdoc />
    /// <remarks>
    /// Returns <see cref="CancellationToken.None"/>. The linked cancellation token in
    /// <see cref="LeaderElectedPollingService"/> will therefore only cancel on host stop,
    /// which is correct for a single-instance deployment.
    /// </remarks>
    // TODO: LeaderToken returns CancellationToken.None, which subtly deviates from the
    // ILeaderElectionService contract ("cancelled when leadership is lost or the service is
    // stopping"). For the single-instance always-leader case this is safe because leadership
    // is never lost. However, any future consumer that calls LeaderToken.Register(...) expecting
    // a callback on leadership loss (not the host-stop signal) will silently never receive it.
    // If a new consumer subscribes to LeaderToken.Register, revisit this and consider returning
    // a token that is cancelled when the host stops (e.g. via IHostApplicationLifetime).
    public CancellationToken LeaderToken => CancellationToken.None;

#pragma warning disable CS0067 // Events never used — intentional no-op for always-leader implementation
    /// <inheritdoc />
    // OnStartedLeading is never fired by this implementation because AlwaysLeaderElectionService
    // is leader from construction with no IHostedService lifecycle. Zero external subscribers
    // exist in the codebase (verified 2026-08-22). The events are kept on the interface as
    // a contract for implementations that do have a leadership transition (K8s Lease, Postgres).
    // If a future consumer subscribes to OnStartedLeading, convert this class to IHostedService
    // and fire the event from StartAsync.
    public event Action? OnStartedLeading;

    /// <inheritdoc />
    // Same rationale as OnStartedLeading. Zero external subscribers.
    public event Action? OnStoppedLeading;
#pragma warning restore CS0067
}
