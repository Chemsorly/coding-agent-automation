namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Strategy interface for cleaning up K8s Jobs when a run is cancelled.
/// Registered at DI time to resolve mode differences (K8s vs SignalR/Legacy)
/// without runtime null-checks in <see cref="RunLifecycleManager"/>.
/// </summary>
public interface IJobCleanupStrategy
{
    /// <summary>
    /// Attempts to delete the infrastructure job associated with a cancelled run.
    /// Implementations must be non-throwing (graceful handling of 404, timeouts, etc.).
    /// </summary>
    Task TryDeleteJobForRunAsync(string runId, CancellationToken ct);
}
