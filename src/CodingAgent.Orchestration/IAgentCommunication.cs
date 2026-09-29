using CodingAgent.Pipeline.Models;

namespace CodingAgent.Orchestration;

/// <summary>
/// Abstracts agent communication over SignalR so that services in the Orchestration
/// project can send messages to agents without depending on <c>Microsoft.AspNetCore.SignalR</c>.
/// The WebUI project implements this via <c>IHubContext&lt;AgentHub, IAgentHubClient&gt;</c>.
/// </summary>
public interface IAgentCommunication
{
    /// <summary>
    /// Requests the agent to fetch its available models.
    /// </summary>
    Task RequestFetchModelsAsync(string connectionId, FetchModelsRequest request, CancellationToken ct = default);

    /// <summary>
    /// Cancels an active job on the agent.
    /// </summary>
    Task CancelJobAsync(string connectionId, string jobId, CancellationToken ct = default);
}
