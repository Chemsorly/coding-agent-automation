using CodingAgent.Orchestration.Redis;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Models;
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
    Task WriteRedisHeartbeatAsync(AgentId agentId);

    /// <summary>
    /// Reads the cross-replica heartbeat timestamp from Redis.
    /// Returns <c>(Available: true, Heartbeat: value)</c> when Redis is reachable.
    /// <c>Heartbeat</c> is <c>null</c> when the key does not exist.
    /// Returns <c>(Available: false, Heartbeat: null)</c> when Redis threw an exception.
    /// </summary>
    Task<(bool Available, DateTimeOffset? Heartbeat)> TryGetRedisHeartbeatAsync(string jobName, AgentId agentId);

    /// <summary>
    /// Deletes the heartbeat key for <paramref name="agentId"/> from Redis.
    /// Best-effort — failures are logged as warnings but do not throw.
    /// </summary>
    Task DeleteRedisHeartbeatAsync(AgentId agentId);
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
    public async Task WriteRedisHeartbeatAsync(AgentId agentId)
    {
        var ttl = TimeSpan.FromSeconds(_options.ChatIdleTimeoutSeconds * 2);
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            await _redis.SetAsync(HeartbeatKey(agentId.Value), nowMs.ToString(), ttl).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "ChatHeartbeatTracker: Redis heartbeat write failed for {AgentId}", agentId);
        }
    }

    /// <inheritdoc/>
    public async Task<(bool Available, DateTimeOffset? Heartbeat)> TryGetRedisHeartbeatAsync(
        string jobName, AgentId agentId)
    {
        try
        {
            var raw = await _redis.GetAsync(HeartbeatKey(agentId.Value)).ConfigureAwait(false);
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
    public async Task DeleteRedisHeartbeatAsync(AgentId agentId)
    {
        try
        {
            await _redis.DeleteAsync(HeartbeatKey(agentId.Value)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "ChatHeartbeatTracker: Redis heartbeat key delete failed for {AgentId}", agentId);
        }
    }

    private static string HeartbeatKey(string agentId) => $"chat:heartbeat:{agentId}";
}
