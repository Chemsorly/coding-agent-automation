using CodingAgentWebUI.Orchestration.Redis;
using CodingAgentWebUI.Pipeline.LeaderElection;
using Microsoft.Extensions.Hosting;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration;

/// <summary>
/// Background service that periodically removes stale members from <c>runs:active</c> whose
/// <c>run:{id}</c> hash key has expired.
///
/// <para>
/// When <see cref="ILeaderElectionService"/> is provided, only the current leader runs the sweep.
/// When no leader-election service is injected (local dev / single-replica), every instance sweeps,
/// which is safe because all <c>SREM</c> operations are idempotent.
/// </para>
///
/// <para>
/// A repair path for orphaned run hashes (Lua script crash between SREM and EXPIREAT) is
/// planned but not yet implemented. See issue for tracking.
/// </para>
/// </summary>
public sealed class RunServiceCleanupService : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);

    private readonly IRedisStore _store;
    private readonly ILeaderElectionService? _leaderElection;
    private readonly ILogger _logger;

    public RunServiceCleanupService(
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
                _logger.Warning(ex, "RunServiceCleanupService: sweep error (will retry at next interval)");
            }
        }
    }

    internal async Task SweepAsync(CancellationToken ct)
    {
        // null = no election service (local dev / single-replica) → always sweep
        if (_leaderElection is not null && !_leaderElection.IsLeader)
        {
            _logger.Debug("RunServiceCleanupService: skipping sweep — not the leader");
            return;
        }

        var members = await _store.SetMembersAsync("runs:active");
        var removedCount = 0;

        foreach (var runId in members)
        {
            ct.ThrowIfCancellationRequested();
            if (!await _store.ExistsAsync($"run:{runId}"))
            {
                await _store.SetRemoveAsync("runs:active", runId);
                removedCount++;
            }
        }

        if (removedCount > 0)
            _logger.Information("RunServiceCleanupService: removed {Count} stale members from runs:active", removedCount);

        // TODO: orphan-hash repair path — scan for run:{id} hashes not in runs:active and apply
        // a short TTL (handles Lua crash between SREM and EXPIREAT). Requires IDatabase.SCAN
        // support on IRedisStore; tracked separately.
    }
}
