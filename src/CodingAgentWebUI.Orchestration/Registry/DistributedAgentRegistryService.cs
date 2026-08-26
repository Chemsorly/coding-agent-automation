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

        return new AgentEntry
        {
            AgentId = new AgentId(agentId),
            ConnectionId = connectionId,
            Hostname = message.Hostname,
            Labels = message.Labels,
            Status = status,
            RegisteredAt = existing?.RegisteredAt ?? now,
            LastHeartbeatAt = now,
            ActiveJobId = activeJobId,
            Disabled = disabled
        };
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
        _ = DeregisterAsync(agentId.Value)
            .ContinueWith(t => _logger.Warning(t.Exception,
                "DeregisterAsync failed for agent {AgentId}", agentId.Value),
                TaskContinuationOptions.OnlyOnFaulted);
        return true; // Optimistic; actual removal is async
    }

    private async Task DeregisterAsync(string agentId)
    {
        var key = AgentKey(agentId);
        var entry = GetAgentRaw(agentId);
        if (entry is not null)
            _connectionIndex.TryRemove(entry.ConnectionId, out _);

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
        // Fire-and-forget: heartbeat is on the hot path
        _ = UpdateHeartbeatAsync(agentId.Value, timestamp)
            .ContinueWith(t => _logger.Warning(t.Exception,
                "UpdateHeartbeat: Redis write failed for agent {AgentId} — TTL not refreshed, agent may be evicted prematurely",
                agentId.Value), TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task UpdateHeartbeatAsync(string agentId, DateTimeOffset timestamp)
    {
        var key = AgentKey(agentId);

        // Guard: do NOT create a ghost entry if the hash was deleted (deregister or TTL expiry)
        // This matches in-memory AgentRegistryService semantics (logs Warning + returns)
        if (!await _store.ExistsAsync(key))
        {
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

        if (newStatus == AgentStatus.Idle)
            await _store.SetAddAsync(AgentsIdleKey, agentId);
        else
            await _store.SetRemoveAsync(AgentsIdleKey, agentId);

        _logger.Information("Agent {AgentId} status transitioned {Old} → {New}", agentId, oldStatus, newStatus);
    }

    // ── Lookups ───────────────────────────────────────────────────────

    /// <inheritdoc />
    public AgentEntry? GetByAgentId(AgentId agentId)
    {
        ArgumentNullException.ThrowIfNull(agentId.Value);
        return GetAgentRaw(agentId.Value);
    }

    /// <inheritdoc />
    public AgentEntry? GetByConnectionId(string connectionId)
    {
        ArgumentNullException.ThrowIfNull(connectionId);
        // Node-local lookup — always on the correct replica
        return _connectionIndex.TryGetValue(connectionId, out var agentId)
            ? GetAgentRaw(agentId)
            : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<AgentEntry> GetIdleAgents()
    {
        return GetIdleAgentsAsync().GetAwaiter().GetResult(); // Safe: ThreadPool context only
    }

    private async Task<IReadOnlyList<AgentEntry>> GetIdleAgentsAsync()
    {
        var members = await _store.SetMembersAsync(AgentsIdleKey);
        var result = new List<AgentEntry>(members.Length);

        foreach (var agentId in members)
        {
            var hash = await _store.HashGetAllAsync(AgentKey(agentId));
            if (hash.Length == 0) continue; // TTL expired, set not yet cleaned

            var entry = HashToEntry(hash);
            if (entry is not null) result.Add(entry);
        }

        return result.AsReadOnly();
    }

    /// <inheritdoc />
    public IReadOnlyList<AgentEntry> GetAllAgents()
    {
        return GetAllAgentsAsync().GetAwaiter().GetResult(); // Safe: ThreadPool context only
    }

    private async Task<IReadOnlyList<AgentEntry>> GetAllAgentsAsync()
    {
        var members = await _store.SetMembersAsync(AgentsAllKey);
        var result = new List<AgentEntry>(members.Length);

        foreach (var agentId in members)
        {
            var hash = await _store.HashGetAllAsync(AgentKey(agentId));
            if (hash.Length == 0) continue;

            var entry = HashToEntry(hash);
            if (entry is not null) result.Add(entry);
        }

        return result.AsReadOnly();
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
        await _store.HashSetFieldAsync(key, field, value ?? "");
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private AgentEntry? GetAgentRaw(string agentId)
    {
        var hash = _store.HashGetAllAsync(AgentKey(agentId)).GetAwaiter().GetResult(); // Safe: ThreadPool
        return hash.Length == 0 ? null : HashToEntry(hash);
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
