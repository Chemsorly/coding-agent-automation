using CodingAgentWebUI.Orchestration.Redis;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Atomically reserves an idle agent for dispatch, preventing two dispatch paths from
/// double-booking the same agent. Registered as a singleton in DI.
///
/// <para>
/// Renamed from <c>JobDeduplicationGuardService</c> (Spec 046). Issue deduplication is owned by
/// the Postgres partial unique index on <c>WorkItems</c> filtered to non-terminal statuses.
/// This type's sole job is atomic agent reservation.
/// </para>
///
/// <para>
/// When <see cref="IRedisStore"/> is provided (multi-replica mode), per-agent Redis locks
/// (<c>lock:agent:{id}</c>, 5-second TTL) replace the in-process <c>_selectionLock</c>,
/// enabling safe selection across API replicas.
/// </para>
/// </summary>
public sealed class AgentReservationService
{
    private static readonly TimeSpan LockTtl = TimeSpan.FromSeconds(5);

    private readonly IAgentRegistryService _registry;
    private readonly ILogger _logger;
    private readonly IRedisStore? _store;

    /// <summary>In-memory fallback lock — used when Redis is not configured (local dev).</summary>
    private readonly object _selectionLock = new();

    public AgentReservationService(IAgentRegistryService registry, ILogger logger, IRedisStore? store = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _logger = logger;
        _store = store;
    }

    /// <summary>
    /// Selects an idle agent whose labels are a superset of the required labels and
    /// atomically reserves it by transitioning to <see cref="AgentStatus.Busy"/>.
    ///
    /// When Redis is available, uses per-agent distributed locks to prevent double-booking
    /// across multiple API replicas. When Redis is absent, falls back to an in-process lock.
    ///
    /// When multiple agents match, selects the one idle longest (FIFO by
    /// <see cref="AgentEntry.LastJobCompletedAt"/>, falling back to <see cref="AgentEntry.RegisteredAt"/>).
    /// </summary>
    /// <returns>The reserved agent (already transitioned to Busy), or <c>null</c> if none available.</returns>
    public AgentEntry? SelectAgent(IReadOnlyList<string> requiredLabels)
    {
        ArgumentNullException.ThrowIfNull(requiredLabels);

        return _store is not null
            ? SelectAgentDistributed(requiredLabels).GetAwaiter().GetResult() // Safe: ThreadPool context only
            : SelectAgentInMemory(requiredLabels);
    }

    // ── In-memory path (single replica / local dev) ───────────────────

    private AgentEntry? SelectAgentInMemory(IReadOnlyList<string> requiredLabels)
    {
        lock (_selectionLock)
        {
            var compatible = GetCompatibleCandidates(requiredLabels);
            if (compatible is null) return null;

            foreach (var candidate in compatible)
            {
                lock (candidate.SyncRoot)
                {
                    if (candidate.Status != AgentStatus.Idle)
                    {
                        _logger.Debug("SelectAgent: skipping agent {AgentId} — status changed to {Status} before reservation",
                            candidate.AgentId, candidate.Status);
                        continue;
                    }

                    candidate.Status = AgentStatus.Busy;
                    candidate.BusySince = DateTimeOffset.UtcNow;
                }

                _logger.Debug("SelectAgent(in-memory): reserved agent {AgentId} for requiredLabels=[{Labels}]",
                    candidate.AgentId, string.Join(", ", requiredLabels));
                return candidate;
            }

            return null;
        }
    }

    // ── Distributed path (multi-replica with Redis) ───────────────────

    private async Task<AgentEntry?> SelectAgentDistributed(IReadOnlyList<string> requiredLabels)
    {
        var compatible = await GetCompatibleCandidatesAsync(requiredLabels);
        if (compatible is null) return null;

        foreach (var candidate in compatible)
        {
            var agentId = candidate.AgentId.Value;
            var lockKey = $"lock:agent:{agentId}";
            var lockValue = Environment.MachineName; // Replica identity — aids debugging

            // Try to acquire per-agent lock (NX PX 5000)
            var acquired = await _store!.SetIfNotExistsAsync(lockKey, lockValue, LockTtl);
            if (!acquired)
            {
                _logger.Debug("SelectAgent: could not acquire lock for agent {AgentId} — another replica is selecting it",
                    agentId);
                continue;
            }

            try
            {
                // Double-check: re-read status after acquiring lock using async method for fresh Redis data.
                // NOTE: TransitionStatus does NOT acquire this lock, so there is a tiny race window
                // where an agent disconnects between HGETALL and HSET Busy. This is accepted:
                // ReconciliationService recovers the wasted dispatch within its interval.
                var fresh = await _registry.GetByAgentIdAsync(candidate.AgentId);
                if (fresh is null || fresh.Status != AgentStatus.Idle || fresh.Disabled)
                {
                    _logger.Debug("SelectAgent: agent {AgentId} status changed to {Status} between lock and double-check — skipping",
                        agentId, fresh?.Status ?? AgentStatus.Disconnected);
                    continue;
                }

                // Transition to Busy atomically under the lock
                _registry.TransitionStatus(candidate.AgentId, AgentStatus.Busy);
                // busySince is set by TransitionStatus → TransitionStatusAsync for DistributedAgentRegistryService

                _logger.Debug("SelectAgent(distributed): reserved agent {AgentId} for requiredLabels=[{Labels}]",
                    agentId, string.Join(", ", requiredLabels));
                return fresh;
            }
            finally
            {
                // Always release the lock — even on exception
                await _store!.DeleteAsync(lockKey);
            }
        }

        _logger.Debug("SelectAgent: no compatible idle agent found for requiredLabels=[{Labels}]",
            string.Join(", ", requiredLabels));
        return null;
    }

    private List<AgentEntry>? GetCompatibleCandidates(IReadOnlyList<string> requiredLabels)
        => FilterCompatibleCandidates(_registry.GetIdleAgents(), requiredLabels);

    private async Task<List<AgentEntry>?> GetCompatibleCandidatesAsync(IReadOnlyList<string> requiredLabels)
        => FilterCompatibleCandidates(await _registry.GetIdleAgentsAsync(), requiredLabels);

    private List<AgentEntry>? FilterCompatibleCandidates(
        IReadOnlyList<AgentEntry> idleAgents,
        IReadOnlyList<string> requiredLabels)
    {
        if (idleAgents.Count == 0)
        {
            _logger.Debug("SelectAgent: no idle agents available (requiredLabels=[{Labels}])",
                string.Join(", ", requiredLabels));
            return null;
        }

        var compatible = idleAgents
            .Where(agent => !agent.Disabled)
            .Where(agent => LabelMatchHelper.IsLabelMatch(agent.Labels, requiredLabels))
            .OrderBy(agent => agent.LastJobCompletedAt ?? agent.RegisteredAt)
            .ToList();

        if (compatible.Count == 0)
        {
            _logger.Debug("SelectAgent: {IdleCount} idle agent(s) but none match requiredLabels=[{Labels}]",
                idleAgents.Count, string.Join(", ", requiredLabels));
            return null;
        }

        return compatible;
    }

    /// <summary>
    /// Resolves the required agent labels for a repository provider config.
    /// Delegates to <see cref="Pipeline.Services.LabelResolver.ResolveRequiredLabels"/> for the actual logic.
    /// </summary>
    public static IReadOnlyList<string> ResolveRequiredLabels(
        ProviderConfig? repoConfig,
        PipelineConfiguration pipelineConfig)
        => Pipeline.Services.LabelResolver.ResolveRequiredLabels(repoConfig, pipelineConfig);
}

/// <summary>
/// Backward-compatibility alias for <see cref="AgentReservationService"/>.
/// All code should migrate to <see cref="AgentReservationService"/> directly (Spec 046 Task 3.1).
/// </summary>
[Obsolete("Use AgentReservationService instead. Renamed in Spec 046.")]
public sealed class JobDeduplicationGuardService
{
    private readonly AgentReservationService _inner;

    public JobDeduplicationGuardService(IAgentRegistryService registry, ILogger logger)
        => _inner = new AgentReservationService(registry, logger);

    public AgentEntry? SelectAgent(IReadOnlyList<string> requiredLabels) => _inner.SelectAgent(requiredLabels);

    public static IReadOnlyList<string> ResolveRequiredLabels(ProviderConfig? repoConfig, PipelineConfiguration pipelineConfig)
        => AgentReservationService.ResolveRequiredLabels(repoConfig, pipelineConfig);
}
