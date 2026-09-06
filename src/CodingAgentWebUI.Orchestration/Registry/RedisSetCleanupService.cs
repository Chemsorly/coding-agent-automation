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
    /// (e.g. an idle-agents secondary set).
    /// </summary>
    // TODO [WARNING]: No validation guard ensures RemovalSetKeys contains ScanSetKey. A subclass
    // that omits ScanSetKey from RemovalSetKeys would silently fail to remove stale members from
    // the scanned set while still removing them from secondary sets. No test currently covers this
    // misconfiguration. Consider adding a Debug.Assert or a constructor-time check:
    //   Debug.Assert(RemovalSetKeys.Contains(ScanSetKey), "RemovalSetKeys must include ScanSetKey");
    // and/or add a test with a deliberately misconfigured subclass to document the contract.
    protected abstract IReadOnlyList<string> RemovalSetKeys { get; }

    /// <summary>
    /// How often the sweep runs.
    /// </summary>
    // TODO [WARNING]: SweepInterval is read exactly once when ExecuteAsync constructs the PeriodicTimer.
    // A subclass returning a dynamically-computed value (e.g. from IOptions) would only observe its
    // initial value. Both current subclasses return compile-time constants, so this is not a current
    // defect, but the "read once at startup" contract should be documented or enforced if the
    // abstraction is extended.
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

    // TODO [WARNING]: SweepAsync is `internal` rather than `protected internal`. Tests access it via
    // InternalsVisibleTo; this works for current test assemblies. However, a future test project
    // not listed in InternalsVisibleTo will not be able to call SweepAsync on new subclasses.
    // Consider changing to `protected internal` so subclass test projects can access it without
    // requiring an InternalsVisibleTo entry for the base assembly.
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
            // TODO [WARNING]: ThrowIfCancellationRequested is at the top of the loop body, so once
            // ExistsAsync returns false the full removal block (all SetRemoveAsync calls for this
            // member) executes before the next cancellation check fires. With the current subclasses
            // (max 2 removal keys) this is acceptable, but if RemovalSetKeys grows large, cancellation
            // latency grows proportionally. Moving the check after the removal block would not help;
            // a finer-grained check inside the inner foreach would reduce latency if needed.
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
