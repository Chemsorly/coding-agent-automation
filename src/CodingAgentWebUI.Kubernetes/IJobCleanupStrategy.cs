using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Kubernetes;

/// <summary>
/// Strategy interface for cleaning up K8s Jobs when a run is cancelled.
/// Registered at DI time so that <see cref="RunLifecycleManager"/> has a
/// non-null cleanup path without runtime null-checks.
/// </summary>
public interface IJobCleanupStrategy
{
    /// <summary>
    /// Attempts to delete the infrastructure job associated with a cancelled run.
    /// Implementations must be non-throwing (graceful handling of 404, timeouts, etc.).
    /// </summary>
    Task TryDeleteJobForRunAsync(RunId runId, CancellationToken ct);
}
