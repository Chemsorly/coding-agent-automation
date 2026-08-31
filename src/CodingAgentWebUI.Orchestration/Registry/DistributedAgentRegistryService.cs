using System.Collections.Concurrent;
using System.Text.Json;
using CodingAgentWebUI.Orchestration.Redis;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using StackExchange.Redis;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Registry;

/// <summary>
/// Redis-backed implementation of <see cref="IAgentRegistryService"/>.
/// Replaces <see cref="AgentRegistryService"/> when <c>IConnectionMultiplexer</c> is available,
/// enabling <c>api.replicas > 1</c>.
///
/// <para>
/// Key schema:
/// <list type="bullet">
///   <item><c>agent:{agentId}</c> — Hash with all agent fields. TTL 600s, refreshed on heartbeat.</item>
///   <item><c>agents:all</c> — Set of all registered agentId strings.</item>
///   <item><c>agents:idle</c> — Set of idle agentId strings (subset of agents:all).</item>
/// </list>
/// </para>
///
/// <para>
/// Per-replica in-memory: <c>_connectionIndex</c> (connectionId → agentId).
/// Only accessed on the replica that owns the connection — never needs cross-replica lookup.
/// </para>
/// </summary>
public sealed class DistributedAgentRegistryService : IAgentRegistryService
{
    private const int AgentTtlSeconds = 600;
    private static readonly TimeSpan AgentTtl = TimeSpan.FromSeconds(AgentTtlSeconds);

    private readonly IRedisStore _store;
    private readonly ILogger _logger;

    // Node-local: connectionId → agentId. Only valid on the replica that owns the connection.
    private readonly ConcurrentDictionary<string, string> _connectionIndex = new();

    // Node-local: agentId → AgentEntry snapshot. Used to recreate the Redis hash if TTL expires
    // while the SignalR connection is still live. Populated in Register, cleared in DeregisterAsync.
    // Same retention semantics as _connectionIndex: entry persists until explicit Deregister is called.
    private readonly ConcurrentDictionary<string, AgentEntry> _localSnapshot = new();

    // Cached snapshot of all agents, refreshed by GetAllAgentsAsync and write paths.
    // Used by GetByAgentId (cross-replica fallback) and async callers that want fresh Redis data
    // without waiting for a full cross-replica read. Write paths (Register, TransitionStatusAsync)
    // keep this cache current so the OTel gauges (GetBusyAgentCount) see timely data between
    // the Redis-backed sync reads that fully refresh it.
    private volatile IReadOnlyList<AgentEntry> _allAgentsCache = [];

    // Serialises mutations to _allAgentsCache from write paths. Read-side uses the volatile field
    // directly (lock-free read, lock-protected write).
    private readonly object _cacheUpdateLock = new();

    // Internal hooks for test determinism: fire-and-forget tasks are stored here so tests can
    // await them instead of using Thread.Sleep. Not used in production code paths.
    // TODO (WARNING): Consider a dedicated FlushAsync() or IAsyncDisposable pattern if more
    // fire-and-forget methods need deterministic test coverage.
    internal Task LastHeartbeatTask { get; private set; } = Task.CompletedTask;
    internal Task LastDeregisterTask { get; private set; } = Task.CompletedTask;

    public DistributedAgentRegistryService(IRedisStore store, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _logger = logger;
    }

    // ── Keys ──────────────────────────────────────────────────────────

    private static string AgentKey(string agentId) => $"agent:{agentId}";
    private const string AgentsAllKey = "agents:all";
    private const string AgentsIdleKey = "agents:idle";

    // ── Register ──────────────────────────────────────────────────────

    /// <inheritdoc />
    public AgentEntry Register(AgentRegistrationMessage message, string connectionId)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(connectionId);

        var agentId = message.AgentId.Value;
        var now = DateTimeOffset.UtcNow;

        // Check if already exists (re-registration / reconnect)
        var existing = GetAgentRaw(agentId);

        // Determine status: Busy if existing activeJobId, else Idle
        AgentStatus status;
        string? activeJobId;
        bool disabled;

        if (existing is not null)
        {
            // Preserve activeJobId, disabled from the existing hash
            activeJobId = existing.ActiveJobId;
            disabled = existing.Disabled;
            status = activeJobId is not null ? AgentStatus.Busy : AgentStatus.Idle;

            // Remove old connectionId from local index
            _connectionIndex.TryRemove(existing.ConnectionId, out _);

            _logger.Information(
                "Agent {AgentId} re-registered after {PreviousStatus} (connection={ConnectionId}, activeJob={JobId})",
                agentId, existing.Status, connectionId, activeJobId ?? "none");
        }
        else
        {
            activeJobId = null;
            disabled = false;
            status = AgentStatus.Idle;

            _logger.Information(
                "Agent {AgentId} registered (labels=[{Labels}], connection={ConnectionId})",
                agentId, string.Join(", ", message.Labels), connectionId);
        }

        // Build hash — do NOT overwrite disabled on re-registration
        var fields = AgentEntryToHashEntries(
            agentId: agentId,
            connectionId: connectionId,
            hostname: message.Hostname,
            labels: message.Labels,
            status: status,
            registeredAt: existing?.RegisteredAt ?? now,
            lastHeartbeatAt: now,
            lastJobCompletedAt: existing?.LastJobCompletedAt,
            disconnectedAt: null,
            busySince: status == AgentStatus.Busy ? (existing?.BusySince ?? now) : null,
            activeJobId: activeJobId,
            activeChatSessionId: existing?.ActiveChatSessionId,
            disabled: disabled,
            orphanRestoredAt: null,
            batchAsync: false);

        // Fire-and-forget write to Redis; return a snapshot immediately so the hub method
        // completes without blocking on Redis I/O. The brief window between return and Redis
        // write means the dispatcher on another replica may not see the agent for one cycle.
        // Log any Redis write failures so they surface rather than being swallowed silently.
        _ = WriteRegistrationAsync(agentId, connectionId, status, fields)
            .ContinueWith(t => _logger.Warning(t.Exception,
                "WriteRegistrationAsync failed for agent {AgentId}", agentId),
                TaskContinuationOptions.OnlyOnFaulted);

        _connectionIndex[connectionId] = agentId;

        var entry = new AgentEntry
        {
            AgentId = new AgentId(agentId),
            ConnectionId = connectionId,
            Hostname = message.Hostname,
            // TODO (WARNING): Labels is assigned directly from message.Labels whose runtime type is unknown.
            // If the caller passes a mutable List<string> and later mutates it externally, the snapshot's
            // Labels reference reflects those mutations. Consider ToList() here to break the aliasing.
            Labels = message.Labels,
            Status = status,
            RegisteredAt = existing?.RegisteredAt ?? now,
            LastHeartbeatAt = now,
            ActiveJobId = activeJobId,
            Disabled = disabled
        };

        // Keep a local snapshot so UpdateHeartbeatAsync can recreate the Redis hash if it
        // expires due to TTL while the SignalR connection is still live (issue #2110).
        // The snapshot is kept up-to-date by TransitionStatusAsync and UpdateAgentFieldAsync
        // so that re-registration from snapshot does not overwrite live state with stale values.
        // Fields NOT currently reflected in the snapshot:
        //   - lastJobCompletedAt (updated via direct field writes, not tracked here)
        //   - busySince exact timestamp (approximated from existing snap.BusySince in TransitionStatusAsync)
        // TODO (WARNING): TOCTOU risk if external code writes directly to the Redis hash without going
        // through TransitionStatus / UpdateAgentFieldAsync (e.g. ReconciliationService direct writes).
        _localSnapshot[agentId] = entry;

        // Update the all-agents cache so GetAllAgents()/GetIdleAgents() sync overloads see the new entry.
        UpdateAllAgentsCache(entry);

        return entry;
    }

    private async Task WriteRegistrationAsync(string agentId, string connectionId, AgentStatus status, HashEntry[] fields)
    {
        var key = AgentKey(agentId);
        await _store.HashSetAsync(key, fields);
        await _store.ExpireAsync(key, AgentTtl);
        await _store.SetAddAsync(AgentsAllKey, agentId);
        if (status == AgentStatus.Idle)
            await _store.SetAddAsync(AgentsIdleKey, agentId);
        else
            await _store.SetRemoveAsync(AgentsIdleKey, agentId);
    }

    // ── Deregister ────────────────────────────────────────────────────

    /// <inheritdoc />
    public bool Deregister(AgentId agentId)
    {
        ArgumentNullException.ThrowIfNull(agentId.Value);
        // Fire-and-forget write to Redis.
        // LastDeregisterTask stores the antecedent task for test determinism (allows tests to await
        // DeregisterAsync completing without Thread.Sleep). The error-log ContinueWith is kept as a
        // separate fire-and-forget — same pattern as UpdateHeartbeat — so awaiting LastDeregisterTask
        // does not throw TaskCanceledException on the success path.
        var deregisterTask = DeregisterAsync(agentId.Value);
        _ = deregisterTask.ContinueWith(t => _logger.Warning(t.Exception,
                "DeregisterAsync failed for agent {AgentId}", agentId.Value),
                TaskContinuationOptions.OnlyOnFaulted);
        LastDeregisterTask = deregisterTask;
        _ = deregisterTask;
        return true; // Optimistic; actual removal is async
    }

    private async Task DeregisterAsync(string agentId)
    {
        var key = AgentKey(agentId);
        // Use _localSnapshot to retrieve the ConnectionId for cleanup. If the snapshot is absent
        // (e.g. agent on another replica), fall back to GetAgentRaw.
        // TODO (WARNING): The comment "avoids a Redis HGETALL on the deregister path" is now
        // inaccurate: the else-branch still calls GetAgentRaw, which performs a full Redis HGETALL
        // and may additionally fall back to _localSnapshot on a miss. In the cross-replica
        // scenario GetAgentRaw will hit Redis first; the snapshot fallback inside GetAgentRaw is
        // only reached if the hash is absent. This creates a benign read loop
        // (DeregisterAsync → GetAgentRaw → _localSnapshot.TryGetValue) that terminates correctly
        // because the snapshot is absent in the cross-replica case. Update this comment if the
        // deregister path is refactored to make the layering explicit again.
        // TODO (WARNING): If the Redis hash has already TTL-expired when Deregister is called,
        // GetAgentRaw returns null and _connectionIndex is not cleaned up — the stale
        // connectionId → agentId mapping persists indefinitely. A subsequent GetByConnectionId
        // call using the old connection ID would hit the stale _connectionIndex entry, call
        // GetAgentRaw (which returns null — hash gone), and return null. However, if
        // UpdateHeartbeatAsync later re-registers from _localSnapshot using the same agentId,
        // the stale _connectionIndex entry for the old connectionId would still map to the
        // (now re-registered) agentId, potentially causing GetByConnectionId to surface the
        // re-registered entry under the stale connection ID. Fix: check _localSnapshot as
        // fallback for ConnectionId when GetAgentRaw returns null.
        string? connectionId = null;
        if (_localSnapshot.TryGetValue(agentId, out var snap))
            connectionId = snap.ConnectionId;
        else
        {
            var raw = GetAgentRaw(agentId);
            connectionId = raw?.ConnectionId;
        }

        if (connectionId is not null)
            _connectionIndex.TryRemove(connectionId, out _);

        // Unconditionally clear the local snapshot so that a subsequent heartbeat does NOT
        // recreate the entry for an intentionally deregistered agent (issue #2110 AC4).
        // This must NOT be inside the `if (entry is not null)` block: if the Redis hash has
        // already expired when Deregister is called, GetAgentRaw returns null and the snapshot
        // would be left in place, causing the next heartbeat to ghost-resurrect the entry.
        // TODO (WARNING): Fire-and-forget race — a Register call arriving between _localSnapshot.TryRemove
        // and _store.DeleteAsync will repopulate _localSnapshot; the pending DeleteAsync will then remove
        // the newly-registered hash while _localSnapshot still holds the entry, causing a resurrection
        // on the next heartbeat. Pre-existing architectural characteristic of fire-and-forget; narrowed
        // (but not eliminated) by _localSnapshot.TryRemove occurring before any await.
        _localSnapshot.TryRemove(agentId, out _);

        // Remove from the all-agents cache so sync read overloads don't return the deregistered agent.
        RemoveFromAllAgentsCache(agentId);

        await _store.DeleteAsync(key);
        await _store.SetRemoveAsync(AgentsAllKey, agentId);
        await _store.SetRemoveAsync(AgentsIdleKey, agentId);

        _logger.Information("Agent {AgentId} deregistered", agentId);
    }

    // ── Heartbeat ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public void UpdateHeartbeat(AgentId agentId, DateTimeOffset timestamp)
    {
        ArgumentNullException.ThrowIfNull(agentId.Value);
        // Fire-and-forget: heartbeat is on the hot path.
        // LastHeartbeatTask stores the antecedent task for test determinism (allows tests to await
        // UpdateHeartbeatAsync completing without Thread.Sleep). The error-log ContinueWith is kept
        // as a separate fire-and-forget so awaiting LastHeartbeatTask does not throw TaskCanceledException
        // on the success path (OnlyOnFaulted causes the continuation to transition to Canceled when the
        // antecedent succeeds, not RanToCompletion).
        var heartbeatTask = UpdateHeartbeatAsync(agentId.Value, timestamp);
        _ = heartbeatTask.ContinueWith(t => _logger.Warning(t.Exception,
                "UpdateHeartbeat: Redis write failed for agent {AgentId} — TTL not refreshed, agent may be evicted prematurely",
                agentId.Value), TaskContinuationOptions.OnlyOnFaulted);
        LastHeartbeatTask = heartbeatTask;
        _ = heartbeatTask;
    }

    private async Task UpdateHeartbeatAsync(string agentId, DateTimeOffset timestamp)
    {
        var key = AgentKey(agentId);

        // Guard: if the hash is absent, distinguish between TTL expiry and explicit deregister.
        // Both conditions produce ExistsAsync == false, so we use _localSnapshot as the discriminator:
        // - Snapshot present  → TTL expired while the SignalR connection is still live; re-register.
        // - Snapshot absent   → Agent was explicitly deregistered (or never registered on this replica);
        //                       log a Warning and return, preserving existing "no ghost entry" semantics.
        // TODO (WARNING): TOCTOU race — two concurrent heartbeat tasks for the same agentId can both
        // observe ExistsAsync == false and both call WriteRegistrationAsync. The final Redis state is
        // consistent (last write wins), but the lastHeartbeatAt timestamp may be stale if the earlier
        // task's write arrives last. Staleness checkers relying on lastHeartbeatAt could misclassify a
        // live agent. Fix: use a per-agentId in-memory lock or a conditional Redis SET (NX/XX) to
        // serialise re-registration.
        if (!await _store.ExistsAsync(key))
        {
            if (_localSnapshot.TryGetValue(agentId, out var snapshot))
            {
                _logger.Warning(
                    "Heartbeat for TTL-expired agent {AgentId} — re-registering from local snapshot to restore registry",
                    agentId);

                var fields = AgentEntryToHashEntries(
                    agentId: agentId,
                    connectionId: snapshot.ConnectionId,
                    hostname: snapshot.Hostname,
                    labels: snapshot.Labels,
                    status: snapshot.Status,
                    registeredAt: snapshot.RegisteredAt,
                    lastHeartbeatAt: timestamp,
                    lastJobCompletedAt: snapshot.LastJobCompletedAt,
                    disconnectedAt: snapshot.DisconnectedAt,
                    busySince: snapshot.BusySince,
                    activeJobId: snapshot.ActiveJobId,
                    activeChatSessionId: snapshot.ActiveChatSessionId,
                    disabled: snapshot.Disabled,
                    orphanRestoredAt: snapshot.OrphanRestoredAt,
                    batchAsync: false);

                await WriteRegistrationAsync(agentId, snapshot.ConnectionId, snapshot.Status, fields);
                return;
            }

            _logger.Warning("Heartbeat for unknown/deregistered agent {AgentId} — ignoring", agentId);
            return;
        }

        await _store.HashSetFieldAsync(key, "lastHeartbeatAt", timestamp.ToString("O"));
        await _store.ExpireAsync(key, AgentTtl);

        // Self-healing: restore set membership in case the cleanup sweep removed this member
        // during a brief hash expiry / re-register race. Only fires when hash is confirmed alive.
        await _store.SetAddAsync(AgentsAllKey, agentId);
    }

    // ── TransitionStatus ──────────────────────────────────────────────

    /// <inheritdoc />
    public void TransitionStatus(AgentId agentId, AgentStatus newStatus)
    {
        ArgumentNullException.ThrowIfNull(agentId.Value);
        _ = TransitionStatusAsync(agentId.Value, newStatus)
            .ContinueWith(t => _logger.Warning(t.Exception,
                "TransitionStatus: Redis write failed for agent {AgentId} → {Status} — status may be stale in registry",
                agentId.Value, newStatus), TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task TransitionStatusAsync(string agentId, AgentStatus newStatus)
    {
        // NOTE: This does NOT acquire lock:agent:{id}. SelectAgent uses the per-agent lock to prevent
        // double-booking, but TransitionStatus writes status without that lock — creating a microsecond
        // race window where an agent could be dispatched to after disconnecting. This is accepted:
        // the race window is tiny and ReconciliationService recovers within its reconciliation interval.
        // Acquiring the lock here would add a Redis round-trip to every heartbeat-related status change.

        var key = AgentKey(agentId);
        var existing = await _store.HashGetAllAsync(key);
        if (existing.Length == 0)
        {
            _logger.Warning("Cannot transition status for unknown agent {AgentId}", agentId);
            return;
        }

        var current = HashToEntry(existing);
        var oldStatus = current?.Status ?? AgentStatus.Idle;

        // Reject Disconnected → Busy: must re-register first
        if (oldStatus == AgentStatus.Disconnected && newStatus == AgentStatus.Busy)
        {
            _logger.Warning(
                "Agent {AgentId} invalid transition {Old} → {New} rejected (must re-register first)",
                agentId, oldStatus, newStatus);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var fields = new List<HashEntry> { new("status", newStatus.ToString()) };

        if (newStatus == AgentStatus.Busy)
            fields.Add(new HashEntry("busySince", now.ToString("O")));
        else
            fields.Add(new HashEntry("busySince", ""));

        if (newStatus == AgentStatus.Disconnected)
            fields.Add(new HashEntry("disconnectedAt", now.ToString("O")));
        else if (newStatus == AgentStatus.Idle)
            fields.Add(new HashEntry("disconnectedAt", ""));

        await _store.HashSetAsync(key, fields.ToArray());
        // Refresh the TTL whenever status is written — ensures any write path that calls
        // TransitionStatus (job completion, orphan recovery, etc.) keeps the hash alive
        // (issue #2110: every write to the hash should reset the TTL).
        await _store.ExpireAsync(key, AgentTtl);

        if (newStatus == AgentStatus.Idle)
            await _store.SetAddAsync(AgentsIdleKey, agentId);
        else
            await _store.SetRemoveAsync(AgentsIdleKey, agentId);

        // Keep the local snapshot in sync so that if TTL fires after this transition, the
        // re-registration in UpdateHeartbeatAsync uses live status rather than the stale
        // Idle/null-activeJobId snapshot captured at Register() time (issue #2110 CRITICAL-2).
        // Without this, a Busy agent whose hash TTL-expires would be re-registered as Idle
        // with no activeJobId, making it eligible for double-booking by the dispatcher.
        if (_localSnapshot.TryGetValue(agentId, out var snap))
        {
            var busySinceValue = newStatus == AgentStatus.Busy ? (snap.BusySince ?? now) : (DateTimeOffset?)null;
            var disconnectedAtValue = newStatus == AgentStatus.Disconnected ? now : (DateTimeOffset?)null;
            var updated = snap with
            {
                Status = newStatus,
                BusySince = busySinceValue,
                DisconnectedAt = disconnectedAtValue
            };
            _localSnapshot[agentId] = updated;
            // Keep all-agents cache in sync so GetIdleAgents()/GetAllAgents() sync overloads
            // return up-to-date status without hitting Redis.
            UpdateAllAgentsCache(updated);
        }

        _logger.Information("Agent {AgentId} status transitioned {Old} → {New}", agentId, oldStatus, newStatus);
    }

    // ── Lookups ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public AgentEntry? GetByAgentId(AgentId agentId)
    {
        ArgumentNullException.ThrowIfNull(agentId.Value);
        // Primary read is Redis (cross-replica visibility, deregistrations, TTL expirations).
        // _localSnapshot is consulted as a fallback *only* when Redis returns an empty hash AND
        // this replica owns the agent's connection — covering the fire-and-forget write window
        // on the owning replica without creating ghost entries on non-owning replicas.
        // Use GetByAgentIdAsync for async callers that can avoid the sync-over-async pattern.
        return GetAgentRaw(agentId.Value);
    }

    /// <inheritdoc />
    public async Task<AgentEntry?> GetByAgentIdAsync(AgentId agentId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(agentId.Value);
        var hash = await _store.HashGetAllAsync(AgentKey(agentId.Value));
        if (hash.Length > 0)
            return HashToEntry(hash);

        // Redis hash absent: fire-and-forget write may not have completed yet.
        // Fall back to in-process snapshot ONLY if this replica owns the agent's connection,
        // to avoid cross-replica ghost reads (same guard as GetAgentRaw).
        // Safety: DeregisterAsync clears _localSnapshot before the Redis delete, so a
        // deregistered agent cannot be resurrected through the snapshot fallback.
        if (_localSnapshot.TryGetValue(agentId.Value, out var snap) &&
            _connectionIndex.ContainsKey(snap.ConnectionId))
            return snap;

        return null;
    }

    /// <inheritdoc />
    public AgentEntry? GetByConnectionId(string connectionId)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        // Node-local lookup — always on the correct replica.
        // Falls back to _localSnapshot (same node) to reconstruct the entry.
        if (!_connectionIndex.TryGetValue(connectionId, out var agentId))
            return null;

        // Try local snapshot first (avoids Redis call — node-local is sufficient here).
        if (_localSnapshot.TryGetValue(agentId, out var local))
            return local;

        // Snapshot absent (e.g. deregistered but _connectionIndex not yet cleaned):
        // fall back to Redis to surface a fresh entry if it exists.
        return GetAgentRaw(agentId);
    }

    /// <inheritdoc />
    public IReadOnlyList<AgentEntry> GetIdleAgents()
    {
        // Read from Redis — ensures cross-replica visibility (agents registered on other replicas
        // are not in this replica's _allAgentsCache until a write path populates it).
        // GetIdleAgentsAsync also refreshes the cache as a side-effect.
        return GetIdleAgentsAsync().GetAwaiter().GetResult(); // Safe: ThreadPool context only
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentEntry>> GetIdleAgentsAsync(CancellationToken ct = default)
    {
        var members = await _store.SetMembersAsync(AgentsIdleKey);
        if (members.Length == 0) return Array.Empty<AgentEntry>();

        // Fire all HGETALL before awaiting any — StackExchange.Redis queues them into a single
        // pipeline flush, reducing N sequential round-trips to approximately 1.
        var tasks = members.Select(id => _store.HashGetAllAsync(AgentKey(id))).ToArray();
        var results = await Task.WhenAll(tasks);

        var list = new List<AgentEntry>(results.Length);
        foreach (var hash in results)
        {
            // TODO (WARNING): GetIdleAgentsAsync does not apply the snapshot fallback. A
            // just-registered agent whose Redis hash is absent (fire-and-forget write window)
            // is silently dropped here, making GetIdleAgentsAsync inconsistent with
            // GetByAgentId / GetByAgentIdAsync which now fall back to _localSnapshot.
            // The issue description listed GetIdleAgents as an affected read path. Fix: when
            // hash.Length == 0, attempt _localSnapshot.TryGetValue(id, out var snap) and add
            // snap to the list if found (mirroring the GetAgentRaw fallback).
            if (hash.Length == 0) continue; // TTL expired, set not yet cleaned
            var entry = HashToEntry(hash);
            if (entry is not null) list.Add(entry);
        }

        var idleList = list.AsReadOnly();

        // Merge idle results into the all-agents cache so GetAllAgents() stays reasonably fresh.
        // This is a best-effort update; GetAllAgentsAsync provides a complete refresh.
        var idleIds = new HashSet<string>(list.Select(e => e.AgentId.Value));
        var existing = _allAgentsCache;
        var merged = existing
            .Where(e => !idleIds.Contains(e.AgentId.Value))
            .Concat(list)
            .ToList()
            .AsReadOnly();
        // TODO: Race condition — this read-modify-write on _allAgentsCache is not protected by
        // _cacheUpdateLock. A concurrent Register/DeregisterAsync that runs between reading
        // `existing` and assigning `merged` will have its cache update silently overwritten.
        // For example: DeregisterAsync for agent-A calls RemoveFromAllAgentsCache (sets cache to
        // [B]), then this line restores [A, B] — resurrecting the deregistered agent.
        // Fix: wrap the merge-and-assign in lock(_cacheUpdateLock), or accept and document staleness.
        _allAgentsCache = merged;

        return idleList;
    }

    /// <inheritdoc />
    public IReadOnlyList<AgentEntry> GetAllAgents()
    {
        // Read from Redis — ensures cross-replica visibility (agents registered on other replicas
        // are not in this replica's _allAgentsCache until a write path populates it).
        // GetAllAgentsAsync also refreshes the cache as a side-effect.
        return GetAllAgentsAsync().GetAwaiter().GetResult(); // Safe: ThreadPool context only
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AgentEntry>> GetAllAgentsAsync(CancellationToken ct = default)
    {
        var members = await _store.SetMembersAsync(AgentsAllKey);
        if (members.Length == 0)
        {
            _allAgentsCache = [];
            return Array.Empty<AgentEntry>();
        }

        // Fire all HGETALL before awaiting any — single pipeline flush.
        var tasks = members.Select(id => _store.HashGetAllAsync(AgentKey(id))).ToArray();
        var results = await Task.WhenAll(tasks);

        var list = new List<AgentEntry>(results.Length);
        foreach (var hash in results)
        {
            if (hash.Length == 0) continue;
            var entry = HashToEntry(hash);
            if (entry is not null) list.Add(entry);
        }

        var readOnly = list.AsReadOnly();
        // TODO: Race condition — assigning _allAgentsCache here without holding _cacheUpdateLock
        // means a concurrent Register (which acquires the lock and updates the cache) that runs
        // between the Task.WhenAll above and this assignment will have its update overwritten.
        // Less dangerous than GetIdleAgentsAsync's partial merge (this is a full replacement), but
        // a newly registered agent can still disappear from sync reads until the next write-path update.
        // Fix: wrap in lock(_cacheUpdateLock).
        _allAgentsCache = readOnly;
        return readOnly;
    }

    /// <inheritdoc />
    public int GetBusyAgentCount()
        => GetAllAgents().Count(a => a.Status == AgentStatus.Busy);

    /// <inheritdoc />
    public IReadOnlyList<AgentEntry> GetAgentsByLabel(string labelKey, string labelValue)
    {
        var target = $"{labelKey}={labelValue}";
        return GetAllAgents()
            .Where(a => a.Labels?.Any(l => string.Equals(l, target, StringComparison.OrdinalIgnoreCase)) == true)
            .ToList()
            .AsReadOnly();
    }

    // ── UpdateAgentFieldAsync ─────────────────────────────────────────

    /// <inheritdoc />
    public async Task UpdateAgentFieldAsync(AgentId agentId, string field, string? value)
    {
        ArgumentNullException.ThrowIfNull(agentId.Value);
        var key = AgentKey(agentId.Value);

        // Existence guard: if the hash has already expired (e.g. TTL fired before
        // ReportChatCompleted cleared activeChatSessionId), writing a single field via
        // HashSetFieldAsync would create a partial hash that satisfies ExistsAsync == true
        // but is missing required fields (agentId, connectionId, registeredAt).
        // HashToEntry would return null for that partial hash, making the agent invisible
        // to GetByAgentId / GetIdleAgents / GetAllAgents until the 600s TTL expires again.
        // Instead, skip the write if the hash is absent — the next heartbeat will
        // re-register from _localSnapshot with all fields present (issue #2110).
        if (!await _store.ExistsAsync(key))
        {
            _logger.Warning(
                "UpdateAgentFieldAsync: hash for agent {AgentId} does not exist (TTL may have expired); skipping field write for '{Field}'",
                agentId.Value, field);
            return;
        }

        await _store.HashSetFieldAsync(key, field, value ?? "");
        // Refresh the TTL on every field write so that transient updates (e.g. clearing
        // activeChatSessionId via ReportChatCompleted) do not leave the hash near-expiry
        // without resetting the window (issue #2110 AC3).
        await _store.ExpireAsync(key, AgentTtl);

        // Keep the local snapshot in sync so that if TTL fires after this field update,
        // UpdateHeartbeatAsync re-registers with the most recent known field value rather
        // than the stale value captured at Register() time (issue #2110 CRITICAL-2 partial fix:
        // snapshot is updated for fields managed through this method).
        if (_localSnapshot.TryGetValue(agentId.Value, out var snapshot))
        {
            _localSnapshot[agentId.Value] = field switch
            {
                "activeJobId" => snapshot with { ActiveJobId = string.IsNullOrEmpty(value) ? null : value },
                "activeChatSessionId" => snapshot with { ActiveChatSessionId = string.IsNullOrEmpty(value) ? null : value },
                "disabled" => bool.TryParse(value, out var d) ? snapshot with { Disabled = d } : snapshot,
                "orphanRestoredAt" => DateTimeOffset.TryParse(value, out var ora) ? snapshot with { OrphanRestoredAt = ora } : snapshot,
                _ => snapshot
            };
        }
        // TODO: _allAgentsCache is NOT updated here. Fields written via this method (e.g. disabled,
        // activeJobId) will not be reflected in GetAllAgents() / GetBusyAgentCount() sync reads until
        // the next Register or TransitionStatusAsync call refreshes the entry via UpdateAllAgentsCache.
        // For example, setting disabled=true will not be visible to GetAgentsByLabel or GetIdleAgents
        // (sync overloads) until a write-path update occurs. Consider calling UpdateAllAgentsCache with
        // the updated snapshot entry, consistent with TransitionStatusAsync which does both.
    }

    // ── Helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Atomically replaces or adds <paramref name="updated"/> in <see cref="_allAgentsCache"/>.
    /// Called from write paths (Register, TransitionStatusAsync, UpdateAgentFieldAsync) so the
    /// sync read overloads stay current without hitting Redis.
    /// </summary>
    private void UpdateAllAgentsCache(AgentEntry updated)
    {
        var id = updated.AgentId.Value;
        lock (_cacheUpdateLock)
        {
            var list = _allAgentsCache.Where(e => e.AgentId.Value != id).Append(updated).ToList().AsReadOnly();
            _allAgentsCache = list;
        }
    }

    /// <summary>
    /// Removes <paramref name="agentId"/> from <see cref="_allAgentsCache"/>.
    /// Called from <see cref="DeregisterAsync"/>.
    /// </summary>
    private void RemoveFromAllAgentsCache(string agentId)
    {
        lock (_cacheUpdateLock)
        {
            var list = _allAgentsCache.Where(e => e.AgentId.Value != agentId).ToList().AsReadOnly();
            _allAgentsCache = list;
        }
    }

    private AgentEntry? GetAgentRaw(string agentId)
    {
        var hash = _store.HashGetAllAsync(AgentKey(agentId)).GetAwaiter().GetResult(); // Safe: ThreadPool
        if (hash.Length > 0)
            return HashToEntry(hash);

        // Redis hash absent: either the fire-and-forget write hasn't completed yet, or the TTL
        // has expired. Fall back to the in-process snapshot ONLY if this replica owns the agent's
        // connection (i.e., the snapshot's connectionId appears in _connectionIndex).
        // This guards against cross-replica ghost reads: if the agent is registered on another
        // replica, _connectionIndex has no entry for that connection and we return null, matching
        // the expected "agent is gone" semantics for Deregister/ForceExpire on non-owning replicas.
        // Safety: DeregisterAsync calls _localSnapshot.TryRemove *before* the Redis delete, so a
        // deregistered agent's snapshot entry is gone before the hash could appear absent here —
        // the fallback cannot resurrect an intentionally deregistered agent on the owning replica.
        // TODO (WARNING): There is no bounded lifetime on how long a TTL-expired agent can remain
        // visible via the snapshot. A replica that loses Redis connectivity entirely will serve
        // stale snapshot data indefinitely until an explicit Deregister is called or the process
        // restarts. The issue description explicitly acknowledges this as intentional for the
        // TTL-expiry recovery path. If unbounded staleness becomes a problem, consider adding a
        // snapshot entry timestamp and evicting entries older than a configured max-staleness
        // threshold (e.g. 2× the Redis TTL) so callers that rely on GetAgentRaw returning null
        // to mean "agent is gone" are not misled indefinitely.
        if (_localSnapshot.TryGetValue(agentId, out var snap) &&
            _connectionIndex.ContainsKey(snap.ConnectionId))
            return snap;

        return null;
    }

    private static AgentEntry? HashToEntry(HashEntry[] hash)
    {
        var dict = hash.ToDictionary(e => (string)e.Name!, e => (string?)e.Value);

        if (!dict.TryGetValue("agentId", out var agentId) || string.IsNullOrEmpty(agentId))
            return null;
        if (!dict.TryGetValue("connectionId", out var connectionId) || string.IsNullOrEmpty(connectionId))
            return null;
        if (!dict.TryGetValue("hostname", out var hostname)) hostname = "";
        var hostnameNonNull = hostname ?? "";
        if (!dict.TryGetValue("registeredAt", out var registeredAtStr) || string.IsNullOrEmpty(registeredAtStr))
            return null;

        List<string> labels;
        if (dict.TryGetValue("labels", out var labelsJson) && !string.IsNullOrEmpty(labelsJson))
            labels = JsonSerializer.Deserialize<List<string>>(labelsJson) ?? new List<string>();
        else
            labels = new List<string>();

        _ = Enum.TryParse<AgentStatus>(dict.GetValueOrDefault("status") ?? "Idle", out var status);
        _ = DateTimeOffset.TryParse(registeredAtStr, out var registeredAt);
        _ = DateTimeOffset.TryParse(dict.GetValueOrDefault("lastHeartbeatAt"), out var lastHeartbeat);

        DateTimeOffset? lastJobCompleted = DateTimeOffset.TryParse(dict.GetValueOrDefault("lastJobCompletedAt"), out var ljc) ? ljc : null;
        DateTimeOffset? disconnectedAt = DateTimeOffset.TryParse(dict.GetValueOrDefault("disconnectedAt"), out var da) ? da : null;
        DateTimeOffset? busySince = DateTimeOffset.TryParse(dict.GetValueOrDefault("busySince"), out var bs) ? bs : null;
        DateTimeOffset? orphanRestoredAt = DateTimeOffset.TryParse(dict.GetValueOrDefault("orphanRestoredAt"), out var ora) ? ora : null;

        _ = bool.TryParse(dict.GetValueOrDefault("disabled") ?? "false", out var disabled);

        return new AgentEntry
        {
            AgentId = new AgentId(agentId),
            ConnectionId = connectionId,
            Hostname = hostnameNonNull,
            Labels = labels!,
            Status = status,
            RegisteredAt = registeredAt,
            LastHeartbeatAt = lastHeartbeat,
            LastJobCompletedAt = lastJobCompleted,
            DisconnectedAt = disconnectedAt,
            BusySince = busySince,
            OrphanRestoredAt = orphanRestoredAt,
            ActiveJobId = dict.GetValueOrDefault("activeJobId") is { Length: > 0 } aj ? aj : null,
            ActiveChatSessionId = dict.GetValueOrDefault("activeChatSessionId") is { Length: > 0 } acs ? acs : null,
            Disabled = disabled
        };
    }

    private static HashEntry[] AgentEntryToHashEntries(
        string agentId, string connectionId, string hostname,
        IReadOnlyList<string> labels, AgentStatus status,
        DateTimeOffset registeredAt, DateTimeOffset lastHeartbeatAt,
        DateTimeOffset? lastJobCompletedAt, DateTimeOffset? disconnectedAt,
        DateTimeOffset? busySince, string? activeJobId, string? activeChatSessionId,
        bool disabled, DateTimeOffset? orphanRestoredAt, bool batchAsync)
    {
        return
        [
            new HashEntry("agentId", agentId),
            new HashEntry("connectionId", connectionId),
            new HashEntry("hostname", hostname),
            new HashEntry("labels", JsonSerializer.Serialize(labels)),
            new HashEntry("status", status.ToString()),
            new HashEntry("registeredAt", registeredAt.ToString("O")),
            new HashEntry("lastHeartbeatAt", lastHeartbeatAt.ToString("O")),
            new HashEntry("lastJobCompletedAt", lastJobCompletedAt?.ToString("O") ?? ""),
            new HashEntry("disconnectedAt", disconnectedAt?.ToString("O") ?? ""),
            new HashEntry("busySince", busySince?.ToString("O") ?? ""),
            new HashEntry("activeJobId", activeJobId ?? ""),
            new HashEntry("activeChatSessionId", activeChatSessionId ?? ""),
            new HashEntry("disabled", disabled.ToString()),
            new HashEntry("orphanRestoredAt", orphanRestoredAt?.ToString("O") ?? "")
        ];
    }
}
