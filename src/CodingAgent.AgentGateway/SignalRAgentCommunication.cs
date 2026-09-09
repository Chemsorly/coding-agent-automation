using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;

namespace CodingAgent.AgentGateway;

/// <summary>
/// Implements <see cref="IAgentCommunication"/> by delegating to the SignalR
/// <see cref="IHubContext{THub,T}"/> for <see cref="AgentHub"/>.
/// Registered as a singleton in DI.
/// </summary>
public sealed class SignalRAgentCommunication : IAgentCommunication
{
    private readonly IHubContext<AgentHub, IAgentHubClient> _hubContext;

    public SignalRAgentCommunication(IHubContext<AgentHub, IAgentHubClient> hubContext)
    {
        ArgumentNullException.ThrowIfNull(hubContext);
        _hubContext = hubContext;
    }

    /// <inheritdoc />
    public Task RequestFetchModelsAsync(string connectionId, FetchModelsRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        ArgumentNullException.ThrowIfNull(request);
        return _hubContext.Clients.Client(connectionId).RequestFetchModels(request);
    }

    /// <inheritdoc />
    public Task CancelJobAsync(string connectionId, string jobId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        ArgumentNullException.ThrowIfNull(jobId);
        return _hubContext.Clients.Client(connectionId).CancelJob(jobId);
    }

    /// <inheritdoc />
    public Task AssignConsolidationJobAsync(string connectionId, AgentId agentId, ConsolidationJobMessage job, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        // TODO: Replace ArgumentNullException.ThrowIfNull(agentId.Value) with
        // ArgumentException.ThrowIfNullOrEmpty(agentId.Value, nameof(agentId)) — ThrowIfNull on a struct
        // field reports "Value" as the parameter name in exceptions rather than "agentId".
        ArgumentNullException.ThrowIfNull(agentId.Value);
        ArgumentNullException.ThrowIfNull(job);
        return _hubContext.Clients.Client(connectionId).AssignConsolidationJob(agentId, job);
    }
}
