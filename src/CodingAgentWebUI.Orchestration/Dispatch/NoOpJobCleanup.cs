namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// No-op implementation of <see cref="IJobCleanupStrategy"/> for SignalR/Legacy modes
/// where no K8s Jobs exist to clean up. Also useful as a test utility.
/// </summary>
public sealed class NoOpJobCleanup : IJobCleanupStrategy
{
    /// <inheritdoc />
    public Task TryDeleteJobForRunAsync(string runId, CancellationToken ct) => Task.CompletedTask;
}
