using CodingAgentWebUI.Orchestration.Redis;
using CodingAgentWebUI.Pipeline.LeaderElection;
using Microsoft.Extensions.Hosting;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Registry;

/// <summary>
/// Background service that periodically removes stale members from <c>agents:all</c> and
/// <c>agents:idle</c> whose <c>agent:{id}</c> hash key has expired (TTL elapsed without heartbeat).
///
/// <para>
/// When <see cref="ILeaderElectionService"/> is provided, only the current leader runs the sweep —
/// consistent with <c>DatabaseMaintenanceService</c> and other periodic background services.
/// When no leader-election service is injected (local dev / single-replica), every instance sweeps,
/// which is safe because all <c>SREM</c> operations are idempotent.
/// </para>
///
/// NOTE: Even when the sweep is leader-gated, <see cref="DistributedAgentRegistryService.GetIdleAgents"/>
/// gracefully skips stale set members (<c>HGETALL</c> returns empty → continue), so stale entries
/// are invisible to the dispatcher regardless of whether a cleanup sweep has run.
/// </summary>
public sealed class AgentRegistryCleanupService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(2);

    private readonly IRedisStore _store;
    private readonly ILeaderElectionService? _leaderElection;
    private readonly ILogger _logger;

    public AgentRegistryCleanupService(
        IRedisStore store,
        ILogger logger,
        ILeaderElectionService? leaderElection = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
        _leaderElection = leaderElection;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "AgentRegistryCleanupService: sweep error (will retry at next interval)");
            }
        }
    }

    internal async Task SweepAsync(CancellationToken ct)
    {
        // null = no election service (local dev / single-replica) → always sweep
        if (_leaderElection is not null && !_leaderElection.IsLeader)
        {
            _logger.Debug("AgentRegistryCleanupService: skipping sweep — not the leader");
            return;
        }

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
