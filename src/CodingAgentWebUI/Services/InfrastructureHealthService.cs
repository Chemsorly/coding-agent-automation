using StackExchange.Redis;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Aggregates infrastructure health signals (Database + Redis) into a single queryable service.
/// All property reads are lightweight (volatile bool / property) — no network calls.
/// Returns null for services that are not configured (Legacy mode / no Redis).
/// </summary>
public sealed class InfrastructureHealthService
{
    private readonly DatabaseHealthState? _dbHealth;
    private readonly IConnectionMultiplexer? _redis;
    private readonly bool _dbModeActive;
    private readonly bool _redisConfigured;

    public InfrastructureHealthService(IServiceProvider serviceProvider, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(configuration);

        _dbHealth = serviceProvider.GetService<DatabaseHealthState>();
        // TODO: (#997 review) IConnectionMultiplexer is no longer registered as a DI singleton after the
        //       Redis backplane refactoring (ConnectionMultiplexer is now created inside the async ConnectionFactory
        //       delegate in ConfigureSignalRRedisBackplane). This will always resolve to null, causing
        //       RedisConnected to report null ("not configured") even when Redis IS configured and healthy.
        //       Fix: either re-register IConnectionMultiplexer as a singleton, or use an alternative health
        //       signal (e.g., a dedicated Redis health check service).
        _redis = serviceProvider.GetService<IConnectionMultiplexer>();

        // DB mode is active when Database:Host is configured
        _dbModeActive = !string.IsNullOrEmpty(DatabaseConnectionResolver.Resolve(configuration));

        // Redis is configured when SignalR:Redis:ConnectionString is set
        _redisConfigured = !string.IsNullOrEmpty(configuration.GetValue<string>("SignalR:Redis:ConnectionString"));
    }

    /// <summary>
    /// Database connection status. null = not configured (Legacy mode), true = healthy, false = unhealthy.
    /// </summary>
    public bool? DatabaseConnected => _dbModeActive ? _dbHealth?.IsDatabaseHealthy : null;

    /// <summary>
    /// Redis connection status. null = not configured, true = connected, false = disconnected.
    /// </summary>
    public bool? RedisConnected => _redisConfigured ? _redis?.IsConnected : null;
}
