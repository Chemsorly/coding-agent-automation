using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Orchestration;

/// <summary>
/// Abstracts agent communication over SignalR so that services in the Orchestration
/// project can send messages to agents without depending on <c>Microsoft.AspNetCore.SignalR</c>.
/// The WebUI project implements this via <c>IHubContext&lt;AgentHub, IAgentHubClient&gt;</c>.
/// </summary>
public interface IAgentCommunication
{
    /// <summary>
    /// Sends a job assignment to the agent identified by <paramref name="connectionId"/>.
    /// </summary>
    Task AssignJobAsync(string connectionId, JobAssignmentMessage job, CancellationToken ct = default);

    /// <summary>
    /// Requests the agent to fetch its available models.
    /// </summary>
    Task RequestFetchModelsAsync(string connectionId, FetchModelsRequest request, CancellationToken ct = default);

    /// <summary>
    /// Forces the agent to disconnect gracefully.
    /// </summary>
    Task ForceDisconnectAsync(string connectionId, CancellationToken ct = default);

    /// <summary>
    /// Cancels an active job on the agent.
    /// </summary>
    Task CancelJobAsync(string connectionId, string jobId, CancellationToken ct = default);
}
