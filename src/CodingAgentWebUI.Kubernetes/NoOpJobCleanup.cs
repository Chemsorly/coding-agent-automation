using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Kubernetes;

/// <summary>
/// No-op implementation of <see cref="IJobCleanupStrategy"/> for environments
/// where no K8s Jobs exist to clean up (e.g., local dev without a cluster, test environments).
/// </summary>
public sealed class NoOpJobCleanup : IJobCleanupStrategy
{
    /// <inheritdoc />
    public Task TryDeleteJobForRunAsync(RunId runId, CancellationToken ct) => Task.CompletedTask;
}
