using CodingAgentWebUI.Orchestration.Redis;
using Microsoft.Extensions.Hosting;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Registry;

/// <summary>
/// Background service that periodically removes stale members from <c>agents:all</c> and
/// <c>agents:idle</c> whose <c>agent:{id}</c> hash key has expired (TTL elapsed without heartbeat).
///
/// Runs every 2 minutes on all replicas concurrently — all SREM operations are idempotent.
///
/// NOTE: This also closes the cleanup-sweep/register race for <see cref="DistributedAgentRegistryService.UpdateHeartbeat"/>:
/// if a member was pruned from the set during a brief hash expiry / re-register overlap, the next
/// heartbeat restores membership via <c>SADD agents:all</c>.
/// </summary>
public sealed class AgentRegistryCleanupService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(2);

    private readonly IRedisStore _store;
    private readonly ILogger _logger;

    public AgentRegistryCleanupService(IRedisStore store, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "AgentRegistryCleanupService: sweep error (will retry at next interval)");
            }

            await Task.Delay(SweepInterval, stoppingToken);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        var members = await _store.SetMembersAsync("agents:all");
        var removed = 0;

        foreach (var agentId in members)
        {
            ct.ThrowIfCancellationRequested();
            var exists = await _store.ExistsAsync($"agent:{agentId}");
            if (!exists)
            {
                await _store.SetRemoveAsync("agents:all", agentId);
                await _store.SetRemoveAsync("agents:idle", agentId);
                removed++;
            }
        }

        if (removed > 0)
            _logger.Information("AgentRegistryCleanupService: removed {Count} stale members from agent sets", removed);
    }
}
