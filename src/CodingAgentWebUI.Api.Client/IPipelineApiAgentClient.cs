using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// Typed HTTP client for the <c>/api/agents</c> endpoint group.
///
/// The Pipeline API owns the agent registry (agents register over its SignalR hub), so this is
/// the only way another process can observe agent presence or direct commands to agents.
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

    /// <summary>
    /// Delivers a chat prompt to the agent identified by <paramref name="agentId"/> via
    /// <c>POST /api/agents/{agentId}/chat-prompt</c>.
    ///
    /// <para>
    /// The API resolves the agent's SignalR <c>ConnectionId</c> from its live in-process registry
    /// and calls <c>IHubContext.Clients.Client(...).AssignChatPrompt</c> — the only way to reach
    /// an agent after the hub moved from the monolith to the API in Spec 044.
    /// </para>
    /// </summary>
    /// <exception cref="HttpRequestException">
    /// Thrown when the API returns a non-success status code (404 agent not found, 409 disconnected).
    /// </exception>
    Task AssignChatPromptAsync(string agentId, ChatPromptMessage message, CancellationToken ct = default);
}
