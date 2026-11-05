using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using ILogger = Serilog.ILogger;
using System.Diagnostics.Metrics;

namespace CodingAgent.Scheduler.Services;

/// <summary>
/// Replaces WorkItemMetricsBackgroundService (which had a direct EF dependency).
/// Polls GET /api/work-items/counts-by-status, GET /api/agents and GET /api/agents/credential-pool
/// every 10 seconds and feeds the observable gauges:
/// <list type="bullet">
///   <item><description><c>workdistribution.workitems_by_status</c> via <see cref="WorkDistributionTelemetry"/></description></item>
///   <item><description><c>agent.jobs.active</c> and <c>agent.connections.total</c> via <see cref="PipelineTelemetry.Meter"/></description></item>
///   <item><description><c>workdistribution.credential_pool_available</c> and <c>workdistribution.credential_pool_claimed</c> via <see cref="WorkDistributionTelemetry"/></description></item>
/// </list>
/// Leader-gated — only one Scheduler replica registers measurements at a time.
/// </summary>
public sealed class WorkItemCountsService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILeaderGate? _leaderGate;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;

    // NOTE (issue #3555): The initial assignment `= []` is a non-volatile write. Subsequent reads in
    // the registered gauge callback use Volatile.Read(ref _cachedMeasurements), which is correct, but
    // the initial store happens before any Volatile.Write is called. This is safe in practice because
    // the constructor completes before any OTel collection can invoke the callback (no `this` escape
    // before construction ends), but it is a latent inconsistency. If a subclass or test invokes a
    // gauge callback before the constructor completes, it could observe a non-volatile stale value.
    // Fix: replace `= []` with an explicit Volatile.Write in the constructor body after the initial
    // value is set, matching the Volatile.Write pattern used in UpdateMeasurementsAsync.
    private IEnumerable<Measurement<long>> _cachedMeasurements = [];

    // Backing fields for the four leader-only gauges.
    // null = no successful poll yet (or last poll failed) — gauges emit nothing.
    // Non-null = last successful poll result.
    private AgentCounts? _cachedAgentCounts;
    private CredentialPoolCounts? _cachedCredentialPoolCounts;

    // Spec 057: running agents and the limits that apply (GET /api/agents/capacity). Polled in its own try/catch, so
    // its failure resets only the two agent gauges, and a failure of the other polls does not blank them.
    private AgentCapacityStatus? _cachedCapacity;

    public WorkItemCountsService(
        IServiceScopeFactory scopeFactory,
        ILeaderGate? leaderGate,
        ILogger logger,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _scopeFactory = scopeFactory;
        _leaderGate = leaderGate;
        _logger = logger.ForContext<WorkItemCountsService>();
        _interval = interval ?? TimeSpan.FromSeconds(10);

        // Register the gauge callback once at construction — same pattern as WorkItemMetricsBackgroundService.
        // The closure checks IsLeader at read time so that a non-leader replica emits no measurements
        // even if _cachedMeasurements still holds data from a previous leadership term.
        // When _leaderGate is null (single-replica / no leader election), the replica always emits.
        // Only the first-registered callback wins (Interlocked.CompareExchange guard in
        // RegisterWorkItemsByStatusCallback), which is correct: regardless of which replica registered
        // first, its callback will return empty measurements when that replica is not the leader.
        WorkDistributionTelemetry.RegisterWorkItemsByStatusCallback(
            () => _leaderGate is null || _leaderGate.IsLeader
                ? Volatile.Read(ref _cachedMeasurements)
                : Enumerable.Empty<Measurement<long>>());

        // Register agent gauge callbacks.
        // Only the first-registered callback wins (Interlocked.CompareExchange guard in
        // RegisterAgentGaugeCallbacks), which is correct: regardless of which replica registered
        // first, its callback will return empty measurements when that replica is not the leader.
        // The sentinel-field pattern: emit nothing before the first successful poll (null cached
        // value) and nothing when not the leader.
        PipelineTelemetry.RegisterAgentGaugeCallbacks(
            activeJobsCallback: () =>
            {
                if (!(_leaderGate is null || _leaderGate.IsLeader)) return [];
                var counts = Volatile.Read(ref _cachedAgentCounts);
                return counts is null ? [] : [new Measurement<long>(counts.Busy)];
            },
            totalConnectionsCallback: () =>
            {
                if (!(_leaderGate is null || _leaderGate.IsLeader)) return [];
                var counts = Volatile.Read(ref _cachedAgentCounts);
                return counts is null ? [] : [new Measurement<long>(counts.Total)];
            });

        WorkDistributionTelemetry.RegisterCredentialPoolGaugeCallbacks(
            availableCallback: () =>
            {
                if (!(_leaderGate is null || _leaderGate.IsLeader)) return [];
                var counts = Volatile.Read(ref _cachedCredentialPoolCounts);
                return counts is null ? [] : [new Measurement<int>(counts.Available,
                    new KeyValuePair<string, object?>("pool", "kiro"))];
            },
            claimedCallback: () =>
            {
                if (!(_leaderGate is null || _leaderGate.IsLeader)) return [];
                var counts = Volatile.Read(ref _cachedCredentialPoolCounts);
                return counts is null ? [] : [new Measurement<int>(counts.Claimed,
                    new KeyValuePair<string, object?>("pool", "kiro"))];
            });

        // Spec 057: the agent capacity gauges (leader-gated inside the callbacks).
        WorkDistributionTelemetry.RegisterAgentCapacityCallbacks(RunningMeasurements, LimitMeasurements);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information("WorkItemCountsService started — polling every {Interval}", _interval);

        // Immediate first poll
        await UpdateMeasurementsAsync(stoppingToken);

        using var timer = new PeriodicTimer(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await UpdateMeasurementsAsync(stoppingToken);
        }
    }

    private async Task UpdateMeasurementsAsync(CancellationToken ct)
    {
        // Only the leader polls — one source of metrics truth
        // NOTE (issue #3555): This early return does not clear _cachedAgentCounts / _cachedCredentialPoolCounts
        // (nor _cachedMeasurements). The gauge callbacks check IsLeader only at read time, so a replica that
        // polled as leader, lost the lease and later regains it exports its old cached values until the first
        // successful poll after regaining leadership (up to one interval plus API latency). That contradicts
        // "no stale values / nothing before the first successful poll". workitems_by_status has the same gap.
        // Fix: clear the caches in this non-leader branch.
        if (_leaderGate is { IsLeader: false }) return;

        // Resolve ISchedulerApiClient from a short-lived scope for this tick so each tick gets a
        // fresh HttpClient. The scope is disposed after the tick completes, returning the client
        // to the IHttpClientFactory pool for connection recycling (90s PooledConnectionLifetime).
        await using var scope = _scopeFactory.CreateAsyncScope();
        var apiClient = scope.ServiceProvider.GetRequiredService<ISchedulerApiClient>();

        try
        {
            var countsTask = apiClient.GetWorkItemCountsAsync(ct);
            var agentsTask = apiClient.GetAgentsAsync(ct);
            var poolTask = apiClient.GetCredentialPoolAsync(ct);

            // NOTE (issue #3555): The three calls share one Task.WhenAll and one catch block, so a failure of
            // GET /api/agents or GET /api/agents/credential-pool also discards the successfully fetched
            // work-item counts: workitems_by_status and the oldest-pending-age gauge reset to empty although
            // counts-by-status succeeded (and a credential-pool failure blanks the agent gauges). Before
            // #3555 those two gauges depended only on counts-by-status. No test covers a partial failure.
            // Fix: await and cache each source in its own try/catch so a failure resets only its own gauges.
            await Task.WhenAll(countsTask, agentsTask, poolTask);

            var response = await countsTask;
            // NOTE (issue #3555): A null response body falls back to an empty agent list / a zeroed pool here
            // (and in HttpSchedulerApiClient), so it is cached as a successful poll and exported as 0
            // instead of emitting nothing. No test pins null or empty API responses. Fix: treat a null body
            // as a failed poll, and add boundary tests for empty agent list, empty pool and null body.
            var agents = await agentsTask ?? [];
            var pool = await poolTask ?? new CredentialPoolStatus(0, 0, 0);

            Volatile.Write(ref _cachedMeasurements,
                response.Counts.Select(c => new Measurement<long>(c.Count,
                    new KeyValuePair<string, object?>("status", c.Status),
                    new KeyValuePair<string, object?>("agent_selector", c.AgentSelector)))
                               .ToList());
            WorkDistributionTelemetry.UpdateOldestPendingAge(response.OldestPendingCreatedAt);

            var busyCount = agents.Count(a => a.Status == AgentStatus.Busy);
            Volatile.Write(ref _cachedAgentCounts, new AgentCounts(agents.Count, busyCount));

            Volatile.Write(ref _cachedCredentialPoolCounts, new CredentialPoolCounts(pool.Available, pool.Claimed));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* Expected on cancellation. */ }
        catch (Exception ex)
        {
            _logger.Warning(ex, "WorkItemCountsService: failed to fetch counts — resetting to empty");
            Volatile.Write(ref _cachedMeasurements, []);
            WorkDistributionTelemetry.UpdateOldestPendingAge(null);
            // Reset agent and pool caches so gauges stop emitting after a failed poll.
            Volatile.Write(ref _cachedAgentCounts, null);
            Volatile.Write(ref _cachedCredentialPoolCounts, null);
        }

        try
        {
            Volatile.Write(ref _cachedCapacity, await apiClient.GetAgentCapacityAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* Expected on cancellation. */ }
        catch (Exception ex)
        {
            _logger.Warning(ex, "WorkItemCountsService: failed to fetch agent capacity — agent gauges reset");
            Volatile.Write(ref _cachedCapacity, null);
        }
    }

    /// <summary>
    /// Spec 057 req 7.2: <c>workdistribution.agents.running</c> — every deployed template and both kinds, zeros included
    /// (so a series does not vanish when its last agent ends); <c>none</c> only when above 0; nothing when the counts are
    /// unavailable, before the first poll, or on a non-leader.
    /// </summary>
    internal IEnumerable<Measurement<int>> RunningMeasurements()
    {
        if (!(_leaderGate is null || _leaderGate.IsLeader)) return [];
        var capacity = Volatile.Read(ref _cachedCapacity);
        if (capacity is null || !capacity.CountsAvailable) return [];

        var measurements = new List<Measurement<int>>();
        foreach (var row in capacity.Templates)
        {
            measurements.Add(Running(row.RunningRuns, row.TemplateKey ?? "", "run"));
            measurements.Add(Running(row.RunningChats, row.TemplateKey ?? "", "chat"));
        }
        if (capacity.UnknownTemplateRuns > 0) measurements.Add(Running(capacity.UnknownTemplateRuns, "none", "run"));
        if (capacity.UnknownTemplateChats > 0) measurements.Add(Running(capacity.UnknownTemplateChats, "none", "chat"));
        return measurements;
    }

    /// <summary>Spec 057 req 7.2: <c>workdistribution.agents.limit</c> — one measurement per row where a limit applies.</summary>
    internal IEnumerable<Measurement<int>> LimitMeasurements()
    {
        if (!(_leaderGate is null || _leaderGate.IsLeader)) return [];
        var capacity = Volatile.Read(ref _cachedCapacity);
        if (capacity is null) return [];

        return capacity.Templates.Prepend(capacity.All)
            .Where(r => r.EffectiveLimit is not null)
            .Select(r => new Measurement<int>(r.EffectiveLimit!.Value,
                new KeyValuePair<string, object?>("agent_selector", r.TemplateKey ?? "all"),
                new KeyValuePair<string, object?>("source", r.EffectiveSource)))
            .ToList();
    }

    private static Measurement<int> Running(int value, string selector, string kind) =>
        new(value,
            new KeyValuePair<string, object?>("agent_selector", selector),
            new KeyValuePair<string, object?>("kind", kind));

    /// <summary>Immutable snapshot of agent counts from a single poll.</summary>
    private sealed record AgentCounts(int Total, int Busy);

    /// <summary>Immutable snapshot of credential pool counts from a single poll.</summary>
    private sealed record CredentialPoolCounts(int Available, int Claimed);
}
