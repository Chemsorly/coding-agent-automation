using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// Typed HTTP client for the <c>/api/agents</c> endpoint group.
///
/// The Pipeline API owns the agent registry (agents register over its SignalR hub), so this is
/// the only way another process can observe agent presence.
/// </summary>
public interface IPipelineApiAgentClient
{
    /// <summary>
    /// Returns every agent currently registered with the Pipeline API, regardless of status.
    /// </summary>
    /// <remarks>
    /// The endpoint requires the operator (master) key — a per-pod derived agent key is rejected
    /// with 403. Callers holding only a derived key must not use this client.
    /// </remarks>
    Task<IReadOnlyList<AgentEntry>> GetAgentsAsync(CancellationToken ct = default);
}
