using CodingAgent.Orchestration.Redis;
using CodingAgent.Kubernetes;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration.Dispatch;

/// <summary>
/// Manages Redis-side heartbeat storage for chat session liveness tracking.
/// Stores and retrieves cross-replica heartbeat timestamps so any API replica
/// can determine whether a client keepalive has been received recently, regardless
/// of which replica the keepalive POST landed on.
/// </summary>
/// <remarks>
/// Only instantiated when Redis is configured. When Redis is absent, the caller
/// (<see cref="ChatJobDispatcher"/>) passes <c>null</c> for <see cref="IChatHeartbeatTracker"/>
/// and falls back to in-process local ticks exclusively.
/// </remarks>
internal interface IChatHeartbeatTracker
{
    /// <summary>
    /// Writes a heartbeat timestamp for <paramref name="agentId"/> to Redis.
    /// Fire-and-forget — failures are logged as warnings but do not throw.
    /// </summary>
    Task WriteRedisHeartbeatAsync(string agentId);

    /// <summary>
    /// Reads the cross-replica heartbeat timestamp from Redis.
    /// Returns <c>(Available: true, Heartbeat: value)</c> when Redis is reachable.
    /// <c>Heartbeat</c> is <c>null</c> when the key does not exist.
    /// Returns <c>(Available: false, Heartbeat: null)</c> when Redis threw an exception.
    /// </summary>
    Task<(bool Available, DateTimeOffset? Heartbeat)> TryGetRedisHeartbeatAsync(string jobName, string agentId);

    /// <summary>
    /// Deletes the heartbeat key for <paramref name="agentId"/> from Redis.
    /// Best-effort — failures are logged as warnings but do not throw.
    /// </summary>
    Task DeleteRedisHeartbeatAsync(string agentId);
}

/// <inheritdoc cref="IChatHeartbeatTracker"/>
internal sealed class ChatHeartbeatTracker : IChatHeartbeatTracker
{
    private readonly IRedisStore _redis;
    private readonly DispatchServiceOptions _options;
    private readonly ILogger _logger;

    public ChatHeartbeatTracker(IRedisStore redis, DispatchServiceOptions options, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(redis);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _redis = redis;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task WriteRedisHeartbeatAsync(string agentId)
    {
        // TODO [WARNING]: The returned Task is the OnlyOnFaulted continuation, which is in the
        // Canceled state (not Completed) when SetAsync succeeds. Any caller that awaits the
        // returned task on the happy path will receive TaskCanceledException. Current callers
        // use `_ =` (fire-and-forget) so there is no immediate throw, but the interface's return
        // type is Task and any future caller that awaits it will be surprised. Fix: either return
        // Task.CompletedTask after calling ContinueWith (keeping the fault-logging side-effect),
        // or use await + try/catch so the returned task is always completed successfully.
        // See review finding: DotNetSpecialist WARNING @ ChatHeartbeatTracker.cs:66.
        //
        // TODO [WARNING]: agentId is not null-checked. A null agentId will produce a
        // NullReferenceException inside HeartbeatKey (string interpolation) rather than a
        // clean ArgumentNullException at the call site. Add ArgumentNullException.ThrowIfNull(agentId).
        // See review finding: DotNetSpecialist WARNING @ ChatHeartbeatTracker.cs:56.
        var ttl = TimeSpan.FromSeconds(_options.ChatIdleTimeoutSeconds * 2);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return _redis.SetAsync(HeartbeatKey(agentId), nowMs.ToString(), ttl)
            .ContinueWith(t => _logger.Warning(t.Exception,
                "ChatHeartbeatTracker: Redis heartbeat write failed for {AgentId}", agentId),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <inheritdoc/>
    public async Task<(bool Available, DateTimeOffset? Heartbeat)> TryGetRedisHeartbeatAsync(
        string jobName, string agentId)
    {
        // TODO [WARNING]: agentId is not null-checked. A null agentId silently produces an incorrect
        // Redis key ("chat:heartbeat:"), returning (true, null) instead of failing fast.
        // Add ArgumentNullException.ThrowIfNull(agentId).
        // See review finding: DotNetSpecialist WARNING @ ChatHeartbeatTracker.cs:75.
        try
        {
            var raw = await _redis.GetAsync(HeartbeatKey(agentId)).ConfigureAwait(false);
            if (raw is not null && long.TryParse(raw, out var ms))
                return (true, DateTimeOffset.FromUnixTimeMilliseconds(ms));
            // Key does not exist — Redis is available but no heartbeat written yet.
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "ChatHeartbeatTracker: Redis heartbeat read failed for {JobName} — skipping idle-kill for this cycle",
                jobName);
            return (false, null);
        }
    }

    /// <inheritdoc/>
    public Task DeleteRedisHeartbeatAsync(string agentId)
    {
        // TODO [WARNING]: Same ContinueWith/TaskCanceledException issue as WriteRedisHeartbeatAsync —
        // the returned Task is in the Canceled state on the happy path. Fix alongside WriteRedisHeartbeatAsync.
        // TODO [WARNING]: agentId is not null-checked. Add ArgumentNullException.ThrowIfNull(agentId).
        // See review findings: DotNetSpecialist WARNING @ ChatHeartbeatTracker.cs:97 and :56.
        return _redis.DeleteAsync(HeartbeatKey(agentId))
            .ContinueWith(t => _logger.Warning(t.Exception,
                "ChatHeartbeatTracker: Redis heartbeat key delete failed for {AgentId}", agentId),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    private static string HeartbeatKey(string agentId) => $"chat:heartbeat:{agentId}";
}
