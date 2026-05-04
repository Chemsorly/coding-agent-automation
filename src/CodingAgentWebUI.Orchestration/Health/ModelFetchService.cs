using System.Collections.Concurrent;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Health;

/// <summary>
/// Manages "Fetch Models" requests by delegating to a connected agent via <see cref="IAgentCommunication"/>.
/// Caches results after the first successful fetch.
/// </summary>
public sealed class ModelFetchService
{
    private readonly AgentRegistryService _registry;
    private readonly IAgentCommunication _agentComm;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<FetchModelsResponse>> _pending = new();
    private IReadOnlyList<AgentModelInfo>? _cachedModels;

    public ModelFetchService(
        AgentRegistryService registry,
        IAgentCommunication agentComm,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(agentComm);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _agentComm = agentComm;
        _logger = logger;
    }

    /// <summary>
    /// Returns cached models if available, otherwise delegates to a connected agent.
    /// </summary>
    public async Task<(IReadOnlyList<AgentModelInfo> Models, string? Error)> FetchModelsAsync(CancellationToken ct)
    {
        if (_cachedModels is not null)
            return (_cachedModels, null);

        var agents = _registry.GetAllAgents()
            .Where(a => a.Status == AgentStatus.Idle || a.Status == AgentStatus.Busy)
            .ToList();

        if (agents.Count == 0)
            return ([], "No agents available — connect an agent to fetch models.");

        var agent = agents.FirstOrDefault(a => a.Status == AgentStatus.Idle) ?? agents[0];
        var requestId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<FetchModelsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        try
        {
            await _agentComm.RequestFetchModelsAsync(
                agent.ConnectionId, new FetchModelsRequest { RequestId = requestId }, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            await using var reg = timeoutCts.Token.Register(() => tcs.TrySetCanceled());

            var response = await tcs.Task;

            if (response.Error is not null)
                return ([], response.Error);

            _cachedModels = response.Models;
            return (response.Models, null);
        }
        catch (OperationCanceledException)
        {
            return ([], "Request timed out — the agent did not respond in time.");
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
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
        else
        {
            _logger.Warning("Received FetchModelsResponse for unknown request {RequestId}", response.RequestId);
        }
    }
}
