using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Orchestration.Health;

/// <summary>
/// Abstraction over the "wait for a specific fetch-job agent and request models" path.
/// Extracted to allow <c>ModelFetchJobService</c> to be unit-tested without a live
/// SignalR hub or real agent registry.
/// </summary>
public interface IModelFetchReceiver
{
    /// <summary>
    /// Waits for an agent whose ID starts with <paramref name="agentIdPrefix"/> to appear
    /// in the registry, sends it a <c>RequestFetchModels</c>, and returns the result.
    /// </summary>
    Task<(IReadOnlyList<AgentModelInfo> Models, string? Error)> WaitAndFetchAsync(
        string agentIdPrefix,
        int timeoutSeconds,
        int pollIntervalMs,
        CancellationToken ct);
}
