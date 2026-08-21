using CodingAgentWebUI.Api.Client;
using StackExchange.Redis;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Aggregates infrastructure health signals into a single queryable service.
/// All property reads are lightweight (volatile bool / property) — no network calls.
/// Returns null for services that are not configured.
/// </summary>
/// <remarks>
/// DB health is proxied via the Pipeline API's /readyz endpoint (which checks Postgres).
/// Redis health is tracked when SignalR:Redis:ConnectionString is configured.
/// </remarks>
public sealed class InfrastructureHealthService
{
    private readonly IPipelineApiHealthClient _apiHealth;
    private readonly IConnectionMultiplexer? _redis;
    private readonly bool _redisConfigured;

    private volatile int _dbConnectedFlag = -1; // -1=unknown, 0=false, 1=true

    public InfrastructureHealthService(
        IServiceProvider serviceProvider,
        IConfiguration configuration,
        IPipelineApiHealthClient apiHealth)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(apiHealth);

        _apiHealth = apiHealth;
        _redis = serviceProvider.GetService<IConnectionMultiplexer>();
        _redisConfigured = !string.IsNullOrEmpty(configuration.GetValue<string>("SignalR:Redis:ConnectionString"));
    }

    /// <summary>
    /// Triggers a non-blocking background refresh of the DB health status via the API.
    /// Called by the sidebar timer — result is available on the next read via DatabaseConnected.
    /// </summary>
    public void RefreshDbHealthBackground()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _apiHealth.IsReadyAsync();
                _dbConnectedFlag = result ? 1 : 0;
            }
            catch
            {
                _dbConnectedFlag = 0;
            }
        });
    }

    /// <summary>
    /// Database connection status proxied from the Pipeline API's /readyz endpoint.
    /// null = not yet polled, true = healthy, false = unhealthy/unreachable.
    /// </summary>
    public bool? DatabaseConnected => _dbConnectedFlag < 0 ? null : _dbConnectedFlag == 1;

    /// <summary>
    /// Redis connection status. null = not configured, true = connected, false = disconnected.
    /// </summary>
    public bool? RedisConnected => _redisConfigured ? _redis?.IsConnected : null;
}
