using System.Collections.Concurrent;
using System.Text.Json;
using CodingAgent.Infrastructure.Common;
using CodingAgent.Orchestration.Redis;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration.Health;

/// <summary>
/// Manages "Fetch Models" requests by delegating to a connected agent via <see cref="IAgentCommunication"/>.
/// Caches results after the first successful fetch.
///
/// <para>
/// In a multi-replica deployment, the agent may report its result to a different API replica
/// than the one that sent the request. When <paramref name="redis"/> is configured, a local
/// miss in <see cref="_pending"/> causes the result to be stored in Redis under
/// <c>fetch-models:result:{requestId}</c> with a 2-minute expiry. The originating replica
/// polls that key every 500 ms until it finds the result or the 30 s response timeout expires.
/// </para>
/// </summary>
public sealed class ModelFetchService : IModelFetchReceiver
{
    private const string ResultKeyPrefix = "fetch-models:result:";
    private static readonly TimeSpan ResultExpiry = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    private readonly IAgentRegistryService _registry;
    private readonly IAgentCommunication _agentComm;
    private readonly ILogger _logger;
    private readonly IRedisStore? _redis;
    private readonly TimeSpan _responseTimeout;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<FetchModelsResponse>> _pending = new();
    private IReadOnlyList<AgentModelInfo>? _cachedModels;

    public ModelFetchService(
        IAgentRegistryService registry,
        IAgentCommunication agentComm,
        ILogger logger,
        IRedisStore? redis = null,
        TimeSpan? responseTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(agentComm);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _agentComm = agentComm;
        _logger = logger;
        _redis = redis;
        _responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Returns cached models if available, otherwise delegates to a connected agent.
    /// </summary>
    public async Task<(IReadOnlyList<AgentModelInfo> Models, string? Error)> FetchModelsAsync(CancellationToken ct)
    {
        if (_cachedModels is not null)
            return (_cachedModels, null);

        var agents = (await _registry.GetAllAgentsAsync(ct))
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
            agent = (await _registry.GetAllAgentsAsync(token))
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
            timeoutCts.CancelAfter(_responseTimeout);
            // NOTE (issue #3550): Passing the outer `ct` to TrySetCanceled is non-standard; standard pattern
            // is to pass `timeoutCts.Token` so the CancellationToken on the resulting OCE matches the
            // token that actually fired. The current value is correct but may produce confusing diagnostics.
            await using var reg = timeoutCts.Token.Register(() => tcs.TrySetCanceled(ct));

            FetchModelsResponse response;

            if (_redis is not null)
            {
                // Race the local TCS against a Redis poll for the cross-replica case.
                // PollRedisAsync returns null only when the token is cancelled (timeout or ct).
                var redisKey = ResultKeyPrefix + requestId;
                var pollTask = PollRedisAsync(redisKey, timeoutCts.Token);

                // Use the non-generic WhenAny to avoid nullability conflicts between
                // Task<FetchModelsResponse> (tcs.Task) and Task<FetchModelsResponse?> (pollTask).
                var winner = await Task.WhenAny((Task)tcs.Task, pollTask);

                // Cancel the timeout CTS to stop the loser task promptly, then drain it
                // (suppressing OperationCanceledException) so it does not leak.
                timeoutCts.Cancel();
                // NOTE (issue #3550): The drain only suppresses OperationCanceledException. If PollRedisAsync
                // throws a non-OCE (e.g. JsonException from a malformed Redis value, or a Redis fault)
                // while it is the loser, that exception escapes here and fails an otherwise-successful
                // same-replica fetch. Consider broadening the catch to swallow/log any loser-task fault.
                try { await pollTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* expected on cancellation */ }

                if (winner == tcs.Task)
                {
                    // Same-replica path: TCS was completed by CompleteRequestAsync on this replica.
                    // The Redis key (if any) will expire naturally via its TTL.
                    response = await tcs.Task;
                }
                else
                {
                    // Cross-replica path: Redis poll found the result.
                    // NOTE (issue #3550): pollTask is awaited twice: once in the drain above (when it's the
                    // loser) and once here (when it's the winner). Awaiting a completed Task<T> multiple
                    // times is safe in .NET (returns cached result), but is confusing for maintainers.
                    // Consider storing the poll result before the drain to make the intent explicit.
                    var pollResult = await pollTask.ConfigureAwait(false);
                    if (pollResult is null)
                    {
                        // Poll was cancelled by timeout before finding a result — treat as timeout.
                        throw new OperationCanceledException();
                    }

                    await _redis.DeleteAsync(redisKey);
                    response = pollResult;
                }
            }
            else
            {
                // No Redis: wait only on the local TCS.
                response = await tcs.Task;
            }

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
    /// Polls the Redis key <paramref name="key"/> every 500 ms until a value is found or
    /// <paramref name="ct"/> is cancelled. Returns the deserialized response, or <c>null</c>
    /// if the token was cancelled before any value appeared.
    /// </summary>
    private async Task<FetchModelsResponse?> PollRedisAsync(string key, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            // NOTE (issue #3550): The null-forgiving operator `_redis!` suppresses the compiler warning but
            // provides no runtime guard. PollRedisAsync is only called from the `if (_redis is not null)`
            // branch, so _redis is guaranteed non-null here. Consider adding `Debug.Assert(_redis is not null)`
            // to make the invariant explicit and catch any future misuse early.
            var json = await _redis!.GetAsync(key);
            if (json is not null)
            {
                // NOTE (issue #3550): PollRedisAsync uses PipelineJsonOptions.Lenient while CompleteRequestAsync
                // serializes with PipelineJsonOptions.Default (no NumberHandling = AllowNamedFloatingPointLiterals
                // on the read side). A NaN/Infinity RateMultiplier would fail to deserialize here with an
                // unhandled JsonException (not caught by the OperationCanceledException filter). Also, a
                // JSON literal `null` stored in Redis would deserialize to null, silently treated as a
                // timeout rather than a distinct error. Consider using the same options on both sides and
                // adding a null-result guard with a specific error message.
                return JsonSerializer.Deserialize<FetchModelsResponse>(json, PipelineJsonOptions.Lenient);
            }
        }

        return null;
    }

    /// <summary>
    /// Clears the cached model list. Called by integration tests between test runs
    /// to prevent cache bleed from one test affecting the next.
    /// </summary>
    internal void ResetCache() => _cachedModels = null;

    /// <summary>
    /// Called by the hub when an agent reports fetch models results.
    /// When Redis is configured and this replica is not waiting for the request,
    /// the result is stored in Redis for the waiting replica to pick up.
    /// </summary>
    public async Task CompleteRequestAsync(FetchModelsResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        if (_pending.TryRemove(response.RequestId, out var tcs))
        {
            // Same-replica path: complete the TCS directly.
            tcs.TrySetResult(response);
        }
        else if (_redis is not null)
        {
            // Cross-replica path: store the result in Redis for the waiting replica to poll.
            var key = ResultKeyPrefix + response.RequestId;
            var json = JsonSerializer.Serialize(response, PipelineJsonOptions.Default);
            await _redis.SetAsync(key, json, ResultExpiry);
        }
        else
        {
            // No Redis and no local pending entry — log the warning as before.
            _logger.Warning("Received FetchModelsResponse for unknown request {RequestId}",
                LogSanitizer.SanitizeForLog(response.RequestId));
        }
    }
}
