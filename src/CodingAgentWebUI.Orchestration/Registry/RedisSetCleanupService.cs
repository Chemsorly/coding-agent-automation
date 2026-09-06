using CodingAgentWebUI.Orchestration.Redis;
using CodingAgentWebUI.Pipeline.LeaderElection;
using Microsoft.Extensions.Hosting;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Registry;

/// <summary>
/// Abstract base for leader-gated Redis set cleanup services.
///
/// <para>
/// Owns the <see cref="PeriodicTimer"/> sweep loop and the leader-election gate.
/// Subclasses provide configuration only: which set to scan, which hash-key prefix
/// to test for existence, which sets to remove stale members from, and the interval.
/// </para>
///
/// <para>
/// When <see cref="ILeaderElectionService"/> is provided, only the current leader
/// runs the sweep — consistent with other periodic background services.
/// When no leader-election service is injected (local dev / single-replica), every
/// instance sweeps, which is safe because all <c>SREM</c> operations are idempotent.
/// </para>
/// </summary>
public abstract class RedisSetCleanupService : BackgroundService
{
    private readonly IRedisStore _store;
    private readonly ILeaderElectionService? _leaderElection;
    private readonly ILogger _logger;

    protected RedisSetCleanupService(
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

    /// <summary>
    /// The Redis set key whose members are iterated during the sweep.
    /// </summary>
    protected abstract string ScanSetKey { get; }

    /// <summary>
    /// Prefix used to build the per-member existence key: <c>{HashKeyPrefix}:{memberId}</c>.
    /// When this key is absent the member is considered stale and removed.
    /// </summary>
    protected abstract string HashKeyPrefix { get; }

    /// <summary>
    /// All set keys from which stale members are removed.
    /// At minimum contains <see cref="ScanSetKey"/>, but may include additional sets
    /// (e.g. an idle-agents secondary set). Subclasses must include <see cref="ScanSetKey"/>
    /// in this list; omitting it will silently leave stale members in the scanned set.
    /// </summary>
    protected abstract IReadOnlyList<string> RemovalSetKeys { get; }

    /// <summary>
    /// How often the sweep runs.
    /// This value is read exactly once when <see cref="ExecuteAsync"/> constructs the
    /// <see cref="PeriodicTimer"/>. Subclasses should return a compile-time constant or a
    /// value fixed at construction time; dynamically-changing values will not take effect.
    /// </summary>
    protected abstract TimeSpan SweepInterval { get; }

    /// <summary>
    /// Human-readable service name used in log messages.
    /// Defaults to the concrete type name; override for a friendlier label.
    /// </summary>
    protected virtual string ServiceName => GetType().Name;

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
                _logger.Warning(ex, "{ServiceName}: sweep error (will retry at next interval)", ServiceName);
            }
        }
    }

    // Tests access SweepAsync via InternalsVisibleTo declared in the project file.
    internal async Task SweepAsync(CancellationToken ct)
    {
        // null = no election service (local dev / single-replica) → always sweep
        if (_leaderElection is not null && !_leaderElection.IsLeader)
        {
            _logger.Debug("{ServiceName}: skipping sweep — not the leader", ServiceName);
            return;
        }

        var members = await _store.SetMembersAsync(ScanSetKey);
        var removed = 0;

        foreach (var memberId in members)
        {
            ct.ThrowIfCancellationRequested();
            var exists = await _store.ExistsAsync($"{HashKeyPrefix}:{memberId}");
            if (!exists)
            {
                foreach (var setKey in RemovalSetKeys)
                    await _store.SetRemoveAsync(setKey, memberId);
                removed++;
            }
        }

        if (removed > 0)
            _logger.Information("{ServiceName}: removed {Count} stale members from {ScanSetKey}",
                ServiceName, removed, ScanSetKey);
    }
}
