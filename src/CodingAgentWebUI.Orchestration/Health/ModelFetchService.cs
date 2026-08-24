using System.Collections.Concurrent;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Health;

/// <summary>
/// Manages "Fetch Models" requests by delegating to a connected agent via <see cref="IAgentCommunication"/>.
/// Caches results after the first successful fetch.
/// </summary>
public sealed class ModelFetchService : IModelFetchReceiver
{
    private readonly IAgentRegistryService _registry;
    private readonly IAgentCommunication _agentComm;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<FetchModelsResponse>> _pending = new();
    private IReadOnlyList<AgentModelInfo>? _cachedModels;

    public ModelFetchService(
        IAgentRegistryService registry,
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
        return await SendFetchRequestAsync(agent, ct);
    }

    /// <summary>
    /// Waits for an agent whose ID starts with <paramref name="agentIdPrefix"/> to appear in
    /// the registry, then sends it a <c>RequestFetchModels</c> and awaits the response.
    /// Used by <c>ModelFetchJobService</c> in Kubernetes mode: the one-shot job pod registers
    /// as an agent, receives the request, runs <c>kiro-cli --list-models</c>, and reports back.
    /// No pod log reads or extra RBAC are required.
    /// </summary>
    /// <param name="agentIdPrefix">
    /// Prefix of the expected agent ID (typically the k8s Job name, e.g. <c>caa-models-3fd31615</c>).
    /// The pod name includes a random suffix (<c>caa-models-3fd31615-xhh84</c>) injected as
    /// <c>AGENT_ID</c> via <c>metadata.name</c> in the pod spec.
    /// </param>
    /// <param name="timeoutSeconds">Total wall-clock budget for connection + fetch.</param>
    /// <param name="pollIntervalMs">How often to poll the registry while waiting.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<(IReadOnlyList<AgentModelInfo> Models, string? Error)> WaitAndFetchAsync(
        string agentIdPrefix,
        int timeoutSeconds,
        int pollIntervalMs,
        CancellationToken ct)
    {
        if (_cachedModels is not null)
            return (_cachedModels, null);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var token = timeoutCts.Token;

        // Poll until the fetch-job agent appears in the registry.
        AgentEntry? agent = null;
        while (!token.IsCancellationRequested)
        {
            agent = _registry.GetAllAgents()
                .FirstOrDefault(a =>
                    a.AgentId.Value.StartsWith(agentIdPrefix, StringComparison.Ordinal) &&
                    (a.Status == AgentStatus.Idle || a.Status == AgentStatus.Busy));

            if (agent is not null)
                break;

            try { await Task.Delay(pollIntervalMs, token); }
            catch (OperationCanceledException) { break; }
        }

        if (agent is null)
        {
            return ct.IsCancellationRequested
                ? ([], "Fetch models was cancelled.")
                : ([], $"Fetch models agent did not connect within {timeoutSeconds}s. " +
                       "The pod may be slow to start or failing to schedule.");
        }

        _logger.Debug("ModelFetchService: fetch-job agent {AgentId} connected, sending RequestFetchModels",
            agent.AgentId);

        return await SendFetchRequestAsync(agent, ct);
    }

    private async Task<(IReadOnlyList<AgentModelInfo> Models, string? Error)> SendFetchRequestAsync(
        AgentEntry agent, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<FetchModelsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = tcs;

        try
        {
            await _agentComm.RequestFetchModelsAsync(
                agent.ConnectionId, new FetchModelsRequest { RequestId = requestId }, ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            await using var reg = timeoutCts.Token.Register(() => tcs.TrySetCanceled(ct));

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
    /// Clears the cached model list. Called by integration tests between test runs
    /// to prevent cache bleed from one test affecting the next.
    /// </summary>
    internal void ResetCache() => _cachedModels = null;

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
