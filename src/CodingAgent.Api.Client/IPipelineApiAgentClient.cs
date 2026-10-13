using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Client;

/// <summary>
/// Typed HTTP client for the <c>/api/agents</c> endpoint group.
///
/// The Pipeline API owns the agent registry (agents register over its SignalR hub), so this is
/// the only way another process can observe agent presence or direct commands to agents.
/// </summary>
public interface IPipelineApiAgentClient
{
    /// <summary>
    /// Returns every agent currently registered with the Pipeline API, regardless of status,
    /// enriched with issue/run/PR context from the active run service.
    /// </summary>
    /// <remarks>
    /// The endpoint requires the operator (master) key — a per-pod derived agent key is rejected
    /// with 403. Callers holding only a derived key must not use this client.
    /// </remarks>
    Task<IReadOnlyList<AgentEntryDto>> GetAgentsAsync(CancellationToken ct = default);
}
