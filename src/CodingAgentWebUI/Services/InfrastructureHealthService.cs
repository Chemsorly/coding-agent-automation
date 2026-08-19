using StackExchange.Redis;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Aggregates infrastructure health signals into a single queryable service.
/// All property reads are lightweight (volatile bool / property) — no network calls.
/// Returns null for services that are not configured.
/// </summary>
/// <remarks>
/// DB health monitoring removed in Spec 045 Task 10 (Req 1.5): the monolith no longer
/// has a direct Postgres connection. Only Redis health is tracked.
/// </remarks>
public sealed class InfrastructureHealthService
{
    private readonly IConnectionMultiplexer? _redis;
    private readonly bool _redisConfigured;

    public InfrastructureHealthService(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(configuration);

        _redis = serviceProvider.GetService<IConnectionMultiplexer>();

        // Redis is configured when SignalR:Redis:ConnectionString is set
        _redisConfigured = !string.IsNullOrEmpty(configuration.GetValue<string>("SignalR:Redis:ConnectionString"));
    }

    /// <summary>
    /// Database connection status. Always null — the monolith no longer has a direct DB connection.
    /// </summary>
    public bool? DatabaseConnected => null;

    /// <summary>
    /// Redis connection status. null = not configured, true = connected, false = disconnected.
    /// </summary>
    public bool? RedisConnected => _redisConfigured ? _redis?.IsConnected : null;
}
