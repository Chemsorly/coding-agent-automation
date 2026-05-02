using System.Collections.Concurrent;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Coordinates fetching available models from a connected agent via SignalR.
/// Caches results after the first successful fetch.
/// </summary>
public sealed class ModelFetchService
{
    private readonly AgentRegistryService _registry;
    private readonly IHubContext<AgentHub, IAgentHubClient> _hubContext;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<FetchModelsResponse>> _pending = new();
    private IReadOnlyList<AgentModelInfo>? _cachedModels;

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
    /// Returns cached models if available, otherwise fetches from a connected agent.
    /// </summary>
    // TODO: [AGT-14] executablePath is no longer used by the agent (uses its own configured path) — consider removing the parameter
    public async Task<(IReadOnlyList<AgentModelInfo> Models, string? Error)> FetchModelsAsync(
        string executablePath, CancellationToken ct)
    {
        if (_cachedModels is not null)
            return (_cachedModels, null);

        var idleAgents = _registry.GetIdleAgents();
        if (idleAgents.Count == 0)
        {
            // Fall back to any connected (non-disconnected) agent
            var allAgents = _registry.GetAllAgents();
            var connected = allAgents.FirstOrDefault(a => a.Status != AgentStatus.Disconnected);
            if (connected is null)
                return ([], "No agents available — connect an agent to fetch models.");

            return await SendFetchRequestAsync(connected, executablePath, ct);
        }

        return await SendFetchRequestAsync(idleAgents[0], executablePath, ct);
    }

    /// <summary>
    /// Called by the hub when an agent reports fetch models results.
    /// </summary>
    public void CompleteRequest(FetchModelsResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (_pending.TryRemove(response.RequestId, out var tcs))
        {
            tcs.TrySetResult(response);
        }
    }

    // TODO: [AGT-14] Agent may disconnect between selection and send — consider verifying connection state or providing a more specific timeout error
    private async Task<(IReadOnlyList<AgentModelInfo> Models, string? Error)> SendFetchRequestAsync(
        AgentEntry agent, string executablePath, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<FetchModelsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        try
        {
            var request = new FetchModelsRequest
            {
                RequestId = requestId,
                ExecutablePath = executablePath
            };

            await _hubContext.Clients.Client(agent.ConnectionId).RequestFetchModels(request);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            // TODO: [AGT-14] Should use tcs.TrySetCanceled() without token — passing ct is semantically incorrect on timeout
            await using var reg = timeoutCts.Token.Register(() => tcs.TrySetCanceled(ct));

            var response = await tcs.Task;

            if (response.Error is not null)
                return ([], response.Error);

            if (response.Models.Count > 0)
                _cachedModels = response.Models;

            return (response.Models, null);
        }
        catch (OperationCanceledException)
        {
            return ([], "Request timed out waiting for agent response.");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to fetch models from agent {AgentId}", agent.AgentId);
            return ([], $"Failed to fetch models: {ex.Message}");
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }
}
