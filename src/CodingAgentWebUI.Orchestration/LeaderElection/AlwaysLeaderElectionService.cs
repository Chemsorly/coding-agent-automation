namespace CodingAgentWebUI.Orchestration.LeaderElection;

/// <summary>
/// Single-instance no-op leader election service for deployments without a database
/// or Kubernetes lease. Always reports itself as the leader.
///
/// Used as a fallback in SignalR work-distribution mode when no Postgres connection
/// string is configured. <see cref="LeaderElectedPollingService"/> polls
/// <see cref="IsLeader"/> directly, so this implementation requires no
/// <see cref="Microsoft.Extensions.Hosting.IHostedService"/> — it is a pure null-object.
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

#pragma warning disable CS0067 // Event never used — intentional no-op
    /// <inheritdoc />
    // TODO: OnStartedLeading is never fired. The interface contract documents it as "fires when
    // leadership is acquired." For this always-leader implementation there is no natural firing
    // point (the instance is leader from construction). Since this class is deliberately not an
    // IHostedService, there is no StartAsync in which to fire it. Any future consumer that
    // subscribes to ILeaderElectionService.OnStartedLeading to trigger leader-only initialization
    // will silently not run. If a new consumer depends on this event, convert this class to
    // IHostedService and fire OnStartedLeading from StartAsync.
    public event Action? OnStartedLeading;

    /// <inheritdoc />
    public event Action? OnStoppedLeading;
#pragma warning restore CS0067
}
