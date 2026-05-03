using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;

namespace CodingAgentWebUI.Services;

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
    public Task AssignJobAsync(string connectionId, JobAssignmentMessage job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        ArgumentNullException.ThrowIfNull(job);
        return _hubContext.Clients.Client(connectionId).AssignJob(job);
    }

    /// <inheritdoc />
    public Task RequestFetchModelsAsync(string connectionId, FetchModelsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        ArgumentNullException.ThrowIfNull(request);
        return _hubContext.Clients.Client(connectionId).RequestFetchModels(request);
    }

    /// <inheritdoc />
    public Task ForceDisconnectAsync(string connectionId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        return _hubContext.Clients.Client(connectionId).ForceDisconnect();
    }

    /// <inheritdoc />
    public Task CancelJobAsync(string connectionId, string jobId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        ArgumentNullException.ThrowIfNull(jobId);
        return _hubContext.Clients.Client(connectionId).CancelJob(jobId);
    }
}
