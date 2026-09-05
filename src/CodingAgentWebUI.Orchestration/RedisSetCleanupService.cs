using CodingAgentWebUI.Orchestration.Redis;
using CodingAgentWebUI.Pipeline.LeaderElection;
using Microsoft.Extensions.Hosting;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration;

/// <summary>
/// Abstract base class for leader-gated Redis set cleanup services.
///
/// <para>
/// Owns the <see cref="PeriodicTimer"/> sweep loop, the leader-gate check, and the shared
/// iteration pattern (SMEMBERS → EXISTS → remove stale). Concrete subclasses supply
/// configuration via abstract properties and implement <see cref="RemoveStaleAsync"/> to
/// perform the set removal(s) specific to their domain (one set or multiple sets).
/// </para>
///
/// <para>
/// When <see cref="ILeaderElectionService"/> is provided, only the current leader runs each
/// sweep. When no leader-election service is injected (local dev / single-replica), every
/// instance sweeps — which is safe because all SREM operations are idempotent.
/// </para>
/// </summary>
public abstract class RedisSetCleanupService : BackgroundService
{
    // TODO: Consider making _store private and exposing store operations only via RemoveStaleAsync
    // (the intended extension point). Subclasses calling _store directly bypass future base-class
    // instrumentation and make the class harder to reason about. (Review: DotNetSpecialist)
    /// <summary>The Redis store used for all set and key operations.</summary>
    protected readonly IRedisStore _store;
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

    /// <summary>How often the sweep runs.</summary>
    protected abstract TimeSpan SweepInterval { get; }

    /// <summary>Service name used in log messages (e.g. "AgentRegistryCleanupService").</summary>
    protected abstract string ServiceName { get; }

    /// <summary>The Redis set whose members are inspected on each sweep (e.g. "agents:all").</summary>
    protected abstract string MembershipSetKey { get; }

    /// <summary>
    /// Key prefix used to check whether a member is still active (e.g. "agent:" → EXISTS "agent:{id}").
    /// </summary>
    protected abstract string HashKeyPrefix { get; }

    /// <summary>
    /// Remove the stale <paramref name="id"/> from whichever set(s) the concrete service manages.
    /// Called once per stale member detected during a sweep.
    /// </summary>
    protected abstract Task RemoveStaleAsync(string id, CancellationToken ct);

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

    // TODO: Consider removing `virtual` from SweepAsync — the only intended extension point is
    // RemoveStaleAsync. Keeping virtual allows a future assembly-internal subclass to accidentally
    // override SweepAsync and silently remove the leader-gate logic. (Review: DotNetSpecialist)
    internal virtual async Task SweepAsync(CancellationToken ct)
    {
        // null = no election service (local dev / single-replica) → always sweep
        if (_leaderElection is not null && !_leaderElection.IsLeader)
        {
            _logger.Debug("{ServiceName}: skipping sweep — not the leader", ServiceName);
            return;
        }

        var members = await _store.SetMembersAsync(MembershipSetKey);
        var removed = 0;

        foreach (var id in members)
        {
            ct.ThrowIfCancellationRequested();
            var exists = await _store.ExistsAsync($"{HashKeyPrefix}{id}");
            if (!exists)
            {
                await RemoveStaleAsync(id, ct);
                removed++;
            }
        }

        if (removed > 0)
            _logger.Information("{ServiceName}: removed {Count} stale members from {MembershipSetKey}",
                ServiceName, removed, MembershipSetKey);
    }
}
