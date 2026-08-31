using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// <see cref="IAgentRegistryService"/> implementation that reads agent presence from the Pipeline
/// API instead of from an in-process dictionary.
///
/// <para>
/// <b>Why this exists.</b> Spec 044 moved <c>MapHub&lt;AgentHub&gt;</c> into the Pipeline API
/// process, and <c>AgentHub.RegisterAgent</c> is the only writer of the agent registry. The
/// monolith kept binding <see cref="IAgentRegistryService"/> to its own
/// <see cref="AgentRegistryService"/> instance, which nothing writes to — so in a running cluster
/// every UI surface that asks "which agents are connected?" was answered "none", permanently.
/// This adapter answers from the process that actually knows.
/// </para>
///
/// <para>
/// <b>Why a cached snapshot.</b> <see cref="IAgentRegistryService"/> is synchronous —
/// <see cref="GetAllAgents"/> returns a list, not a <see cref="Task{TResult}"/> — and it is called
/// from Blazor render paths. Fetching inline would mean <c>.Result</c> or
/// <c>GetAwaiter().GetResult()</c> on a request thread, the sync-over-async hazard that stalls a
/// Blazor Server circuit. Instead <see cref="AgentRegistrySyncService"/> polls
/// <see cref="RefreshAsync"/> in the background and every read serves an immutable snapshot with no
/// I/O and no locking.
/// </para>
///
/// <para>
/// <b>Staleness.</b> A snapshot older than <see cref="MaxSnapshotAge"/> is discarded and reads
/// return empty. Reporting agents that may have disconnected minutes ago is worse than reporting
/// none: callers such as <c>IssueDrawerService</c> gate dispatch on
/// <c>GetAllAgents().Count == 0</c>, and the monitoring page would otherwise render ghosts forever
/// after the API became unreachable. An unreachable API degrades to the old behaviour, not to a lie.
/// </para>
/// </summary>
public sealed class ApiAgentRegistryService : IAgentRegistryService
{
    private readonly IPipelineApiAgentClient _client;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    private volatile Snapshot _snapshot = Snapshot.Empty;

    /// <summary>
    /// How long a successfully fetched snapshot stays usable. Must comfortably exceed
    /// <see cref="AgentRegistrySyncService.PollInterval"/> so one slow or failed poll does not blank
    /// the agent list, yet stay short enough that a sustained API outage stops showing stale agents.
    /// </summary>
    public TimeSpan MaxSnapshotAge { get; set; } = TimeSpan.FromSeconds(30);

    public ApiAgentRegistryService(IPipelineApiAgentClient client, TimeProvider clock, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _client = client;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Timestamp of the most recent successful fetch, or <c>null</c> if none has completed yet.
    /// Exposed for diagnostics; the read path does not need it.
    /// </summary>
    public DateTimeOffset? LastRefreshedAt => _snapshot.CapturedAt;

    /// <summary>
    /// Fetches the current agent list from the Pipeline API and replaces the snapshot.
    /// Called by <see cref="AgentRegistrySyncService"/>. Exceptions propagate so the poller owns
    /// failure logging and the previous snapshot survives until it ages out.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var agents = await _client.GetAgentsAsync(ct);
        _snapshot = Snapshot.From(agents, _clock.GetUtcNow());
    }

    // ── Reads — served from the snapshot, never blocking ────────────────────

    /// <inheritdoc />
    public IReadOnlyList<AgentEntry> GetAllAgents() => Current.Agents;

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentEntry>> GetAllAgentsAsync(CancellationToken ct = default)
        => Task.FromResult(GetAllAgents());

    /// <inheritdoc />
    public IReadOnlyList<AgentEntry> GetIdleAgents() =>
        Current.Agents.Where(a => a.Status == AgentStatus.Idle).ToList().AsReadOnly();

    /// <inheritdoc />
    public Task<IReadOnlyList<AgentEntry>> GetIdleAgentsAsync(CancellationToken ct = default)
        => Task.FromResult(GetIdleAgents());

    /// <inheritdoc />
    public int GetBusyAgentCount() => Current.Agents.Count(a => a.Status == AgentStatus.Busy);

    /// <inheritdoc />
    public AgentEntry? GetByAgentId(AgentId agentId)
    {
        ArgumentException.ThrowIfNullOrEmpty(agentId.Value);
        return Current.ById.GetValueOrDefault(agentId.Value);
    }

    /// <inheritdoc />
    public Task<AgentEntry?> GetByAgentIdAsync(AgentId agentId, CancellationToken ct = default)
        => Task.FromResult(GetByAgentId(agentId));

    /// <inheritdoc />
    public AgentEntry? GetByConnectionId(string connectionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionId);
        return Current.ByConnectionId.GetValueOrDefault(connectionId);
    }

    // ── Writes — not owned by this process ──────────────────────────────────

    // The registry is written only by AgentHub.RegisterAgent, in the Pipeline API process. The
    // monolith no longer maps that hub, so none of the four writers below should be reachable.
    // They are logged no-ops rather than throws deliberately: a stray call is a wiring bug worth a
    // log line, not a reason to fault a Blazor circuit or a hub filter and take a page down.

    /// <inheritdoc />
    /// <remarks>
    /// No-op. Returns an unstored entry describing what was asked for, because the signature
    /// requires a non-null <see cref="AgentEntry"/> — nothing is retained, and the next
    /// <see cref="RefreshAsync"/> rebuilds the snapshot from the API regardless.
    /// </remarks>
    public AgentEntry Register(AgentRegistrationMessage message, string connectionId)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(connectionId);

        _logger.Warning(
            "Register({AgentId}) called on the API-backed agent registry and was ignored. Agents " +
            "register on the Pipeline API hub; this process holds a read-only replica.",
            message.AgentId);

        var now = _clock.GetUtcNow();
        return new AgentEntry
        {
            AgentId = message.AgentId,
            ConnectionId = connectionId,
            Hostname = message.Hostname,
            Labels = message.Labels,
            Status = AgentStatus.Idle,
            RegisteredAt = now,
            LastHeartbeatAt = now
        };
    }

    /// <inheritdoc />
    /// <remarks>No-op; always returns <c>false</c> because nothing was removed.</remarks>
    public bool Deregister(AgentId agentId)
    {
        _logger.Warning(
            "Deregister({AgentId}) called on the API-backed agent registry and was ignored.", agentId);
        return false;
    }

    /// <inheritdoc />
    /// <remarks>No-op — heartbeats are recorded by the Pipeline API.</remarks>
    public void UpdateHeartbeat(AgentId agentId, DateTimeOffset timestamp)
    {
        _logger.Warning(
            "UpdateHeartbeat({AgentId}) called on the API-backed agent registry and was ignored.", agentId);
    }

    /// <inheritdoc />
    /// <remarks>No-op — status transitions are owned by the Pipeline API.</remarks>
    public void TransitionStatus(AgentId agentId, AgentStatus newStatus)
    {
        _logger.Warning(
            "TransitionStatus({AgentId} to {NewStatus}) called on the API-backed agent registry and was ignored.",
            agentId, newStatus);
    }

    /// <inheritdoc />
    public IReadOnlyList<AgentEntry> GetAgentsByLabel(string labelKey, string labelValue)
    {
        var target = $"{labelKey}={labelValue}";
        return Current.Agents
            .Where(a => a.Labels?.Any(l => string.Equals(l, target, StringComparison.OrdinalIgnoreCase)) == true)
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc />
    /// <remarks>
    /// No-op — field writes are owned by the Pipeline API. The snapshot is rebuilt on the next
    /// <see cref="RefreshAsync"/> call from <see cref="AgentRegistrySyncService"/>.
    /// </remarks>
    public Task UpdateAgentFieldAsync(AgentId agentId, string field, string? value)
    {
        _logger.Debug(
            "UpdateAgentFieldAsync({AgentId}, {Field}={Value}) called on the API-backed registry — ignored (read-only replica).",
            agentId, field, value);
        return Task.CompletedTask;
    }

    // ── Snapshot ────────────────────────────────────────────────────────────

    /// <summary>
    /// The snapshot reads are served from: the last fetch while it is still within
    /// <see cref="MaxSnapshotAge"/>, otherwise empty.
    /// </summary>
    private Snapshot Current
    {
        get
        {
            var snapshot = _snapshot;
            if (snapshot.CapturedAt is not { } capturedAt)
                return Snapshot.Empty;  // no successful fetch yet

            return _clock.GetUtcNow() - capturedAt > MaxSnapshotAge
                ? Snapshot.Empty
                : snapshot;
        }
    }

    /// <summary>
    /// Immutable view of one successful fetch. Built once per refresh and published by a single
    /// reference assignment, so readers never observe a half-updated registry and never take a lock.
    /// </summary>
    private sealed class Snapshot
    {
        public static Snapshot Empty { get; } = new(
            [],
            new Dictionary<string, AgentEntry>(StringComparer.Ordinal),
            new Dictionary<string, AgentEntry>(StringComparer.Ordinal),
            capturedAt: null);

        public IReadOnlyList<AgentEntry> Agents { get; }
        public IReadOnlyDictionary<string, AgentEntry> ById { get; }
        public IReadOnlyDictionary<string, AgentEntry> ByConnectionId { get; }
        public DateTimeOffset? CapturedAt { get; }

        private Snapshot(
            IReadOnlyList<AgentEntry> agents,
            IReadOnlyDictionary<string, AgentEntry> byId,
            IReadOnlyDictionary<string, AgentEntry> byConnectionId,
            DateTimeOffset? capturedAt)
        {
            Agents = agents;
            ById = byId;
            ByConnectionId = byConnectionId;
            CapturedAt = capturedAt;
        }

        public static Snapshot From(IReadOnlyList<AgentEntry> agents, DateTimeOffset capturedAt)
        {
            var byId = new Dictionary<string, AgentEntry>(agents.Count, StringComparer.Ordinal);
            var byConnectionId = new Dictionary<string, AgentEntry>(agents.Count, StringComparer.Ordinal);

            foreach (var agent in agents)
            {
                // Indexer assignment, not Add: the API keys its registry by agent ID so duplicates
                // should not occur, and a malformed payload must not throw and blank an otherwise
                // usable snapshot.
                if (!string.IsNullOrEmpty(agent.AgentId.Value))
                    byId[agent.AgentId.Value] = agent;
                if (!string.IsNullOrEmpty(agent.ConnectionId))
                    byConnectionId[agent.ConnectionId] = agent;
            }

            return new Snapshot(agents, byId, byConnectionId, capturedAt);
        }
    }
}
