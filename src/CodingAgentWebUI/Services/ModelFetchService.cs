using System.Collections.Concurrent;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Delegates model list fetching to a connected agent via SignalR and caches the result.
/// </summary>
public sealed class ModelFetchService
{
    private readonly AgentRegistryService _registry;
    private readonly IHubContext<AgentHub, IAgentHubClient> _hubContext;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<ModelListResponse>> _pending = new();
    private IReadOnlyList<ModelInfo>? _cachedModels;

    public ModelFetchService(
        AgentRegistryService registry,
        IHubContext<AgentHub, IAgentHubClient> hubContext,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(hubContext);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _hubContext = hubContext;
        _logger = logger;
    }

    /// <summary>
    /// Returns the cached model list, or fetches from a connected agent if not yet cached.
    /// </summary>
    public async Task<IReadOnlyList<ModelInfo>> FetchModelsAsync(CancellationToken ct)
    {
        if (_cachedModels is not null)
            return _cachedModels;

        var agents = _registry.GetAllAgents()
            .Where(a => a.Status != AgentStatus.Disconnected)
            .ToList();

        if (agents.Count == 0)
            throw new InvalidOperationException("No agents available — connect an agent to fetch models.");

        var agent = agents.FirstOrDefault(a => a.Status == AgentStatus.Idle) ?? agents[0];

        var requestId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<ModelListResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        try
        {
            var request = new ModelListRequest { RequestId = requestId };
            await _hubContext.Clients.Client(agent.ConnectionId).RequestModelList(request);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            await using var reg = timeoutCts.Token.Register(() => tcs.TrySetCanceled(timeoutCts.Token));

            var response = await tcs.Task;

            if (response.Error is not null)
                throw new InvalidOperationException($"Agent failed to fetch models: {response.Error}");

            _cachedModels = response.Models;
            return _cachedModels;
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Called by the hub when an agent reports model list results.
    /// </summary>
    internal void CompleteRequest(ModelListResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (_pending.TryGetValue(response.RequestId, out var tcs))
        {
            tcs.TrySetResult(response);
        }
        else
        {
            _logger.Warning("Received ModelListResponse for unknown request {RequestId}", response.RequestId);
        }
    }
}
