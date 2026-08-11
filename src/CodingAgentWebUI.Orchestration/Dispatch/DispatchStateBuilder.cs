using System.Runtime.CompilerServices;
using System.Threading.RateLimiting;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Orchestration.Telemetry;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Lightweight projection of pending work items (no Payload loaded).
/// Shared between <see cref="DispatchService"/>, <see cref="ConsolidationWorkItemDispatchService"/>,
/// and <see cref="DispatchLifecycleService"/>.
/// </summary>
internal sealed record PendingWorkItemProjection
{
    public required Guid Id { get; init; }
    public required string AgentSelector { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required int TimeoutSeconds { get; init; }
    public WorkItemTaskType TaskType { get; init; }
    public string? ProjectId { get; init; }
    public string? IssueIdentifier { get; init; }
    public string? IssueProviderConfigId { get; init; }
}

/// <summary>
/// Result of <see cref="DispatchStateBuilder.BuildStateAsync"/>: the dispatch state
/// needed for per-item gating decisions.
/// </summary>
internal sealed class DispatchState
{
    public required PipelineDbContext Db { get; init; }
    public required List<PendingWorkItemProjection> PendingItems { get; init; }
    public required Dictionary<string, int> ConcurrencyBySelector { get; init; }
    public required List<string> AvailablePvcs { get; init; }
}

/// <summary>
/// A dispatch-ready candidate that has passed all gating checks.
/// </summary>
internal sealed record DispatchCandidate(
    PendingWorkItemProjection Item,
    JobTemplate Template,
    string EffectiveSelector,
    bool IsKiroAgent);

/// <summary>
/// Encapsulates the shared dispatch state-building prologue and per-item gating logic
/// that was previously duplicated between <see cref="DispatchService"/> and
/// <see cref="ConsolidationWorkItemDispatchService"/>. Builds the dispatch state (pending items,
/// concurrency map, PVC availability) and yields eligible candidates lazily via
/// <see cref="GetEligibleCandidatesAsync"/>.
/// </summary>
internal sealed class DispatchStateBuilder
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DispatchStateBuilder>();

    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly JobTemplateStore _templateProvider;
    private readonly DispatchTemplateResolver _templateResolver;
    private readonly DispatchServiceOptions _options;

    public DispatchStateBuilder(
        IDbContextFactory<PipelineDbContext> dbFactory,
        DispatchLifecycleService lifecycle,
        JobTemplateStore templateProvider,
        DispatchTemplateResolver templateResolver,
        DispatchServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(lifecycle); // validated but not stored — used only for DI wiring
        _dbFactory = dbFactory;
        _templateProvider = templateProvider;
        _templateResolver = templateResolver;
        _options = options;
    }

    /// <summary>
    /// Queries pending work items matching <paramref name="taskTypeFilter"/>, builds the concurrency map,
    /// and determines available PVCs. Returns the full dispatch state for per-item gating.
    /// </summary>
    /// <param name="taskTypeFilter">
    /// Filter expression for the TaskType column.
    /// DispatchService: w.TaskType != WorkItemTaskType.Consolidation
    /// ConsolidationWorkItemDispatchService: w.TaskType == WorkItemTaskType.Consolidation
    /// </param>
    /// <param name="recordTelemetry">Whether to record poll telemetry (only DispatchService does this).</param>
    /// <param name="ct">Cancellation token.</param>
    // TODO: DbContext leak on exception path — if an exception (e.g., OperationCanceledException)
    // propagates from the activeCounts query, QueryAvailablePvcsAsync, or telemetry call after
    // the pendingItems.Count == 0 guard, the DbContext is never disposed. Wrap in try/catch that
    // disposes db on exception, or use a pattern where db is always in a using scope and transferred
    // to the caller only on success.
    public async Task<DispatchState?> BuildStateAsync(
        System.Linq.Expressions.Expression<Func<WorkItemEntity, bool>> taskTypeFilter,
        bool recordTelemetry,
        CancellationToken ct)
    {
        var db = await _dbFactory.CreateDbContextAsync(ct);

        // Column projection — no Payload loading.
        var pendingItems = await db.WorkItems
            .Where(w => w.Status == WorkItemStatus.Pending)
            .Where(taskTypeFilter)
            .OrderBy(w => w.CreatedAt)
            .Select(w => new PendingWorkItemProjection
            {
                Id = w.Id,
                AgentSelector = w.AgentSelector,
                CreatedAt = w.CreatedAt,
                TimeoutSeconds = w.TimeoutSeconds,
                ProjectId = w.ProjectId,
                IssueIdentifier = w.IssueIdentifier,
                IssueProviderConfigId = w.IssueProviderConfigId,
                TaskType = w.TaskType
            })
            .ToListAsync(ct);

        if (recordTelemetry)
            WorkDistributionTelemetry.RecordLastPollEpoch();

        if (pendingItems.Count == 0)
        {
            if (recordTelemetry)
                WorkDistributionTelemetry.DispatcherPollCount.Add(1);
            await db.DisposeAsync();
            return null;
        }

        // Build concurrency state: count running/dispatched per selector group
        var activeCounts = await db.WorkItems
            .WhereActive()
            .GroupBy(w => w.AgentSelector)
            .Select(g => new { Selector = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var concurrencyBySelector = activeCounts.ToDictionary(x => x.Selector, x => x.Count);

        // PVC pool: determine available PVCs for kiro agents
        var pvcResult = await DispatchLifecycleService.QueryAvailablePvcsAsync(db, _options.KiroPvcPool, ct);
        var availablePvcs = pvcResult.AvailablePvcs;

        if (recordTelemetry)
            WorkDistributionTelemetry.UpdateCredentialPoolMetrics(availablePvcs.Count, pvcResult.ClaimedCount);

        return new DispatchState
        {
            Db = db,
            PendingItems = pendingItems,
            ConcurrencyBySelector = concurrencyBySelector,
            AvailablePvcs = availablePvcs
        };
    }

    /// <summary>
    /// Iterates pending items with per-item gating logic (rate limit, concurrency check,
    /// template resolution, PVC gate) and yields eligible candidates lazily.
    /// The caller can break enumeration at any time (e.g., on leadership loss).
    /// </summary>
    /// <param name="state">Dispatch state from <see cref="BuildStateAsync"/>.</param>
    /// <param name="leaderElection">Leader election service for mid-iteration bailout.</param>
    /// <param name="rateLimiter">Rate limiter owned by the calling service.</param>
    /// <param name="callerName">Name of the calling service (for log messages).</param>
    /// <param name="onNoTemplate">
    /// Callback invoked when no template can be resolved for an item.
    /// Receives the full item projection and error message for context-specific failure handling.
    /// </param>
    /// <param name="ct">Cancellation token (linked token from base class).</param>
    public async IAsyncEnumerable<DispatchCandidate> GetEligibleCandidatesAsync(
        DispatchState state,
        ILeaderElectionService leaderElection,
        TokenBucketRateLimiter rateLimiter,
        string callerName,
        Func<PendingWorkItemProjection, string, CancellationToken, Task> onNoTemplate,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var item in state.PendingItems)
        {
            if (ct.IsCancellationRequested || !leaderElection.IsLeader)
                yield break;

            // Rate limit
            using var lease = await rateLimiter.AcquireAsync(1, ct);
            if (!lease.IsAcquired)
            {
                Log.Warning("{CallerName}: rate limit hit, stopping dispatch cycle", callerName);
                yield break;
            }

            // Check concurrency limit from template
            var maxConcurrent = _templateProvider.GetMaxConcurrent(item.AgentSelector);
            if (IsAtConcurrencyLimit(item.AgentSelector, state.ConcurrencyBySelector, maxConcurrent))
            {
                // TODO: Restore {Current}/{Max} structured log properties stripped during complexity refactoring.
                // These are useful for diagnosing why items are stuck at a concurrency limit.
                // Restore: Log.Debug("{CallerName}: selector {Selector} at concurrency limit ({Current}/{Max}), skipping {WorkItemId}",
                //     callerName, item.AgentSelector, state.ConcurrencyBySelector.GetValueOrDefault(item.AgentSelector, 0), maxConcurrent, item.Id);
                Log.Debug("{CallerName}: selector {Selector} at concurrency limit, skipping {WorkItemId}",
                    callerName, item.AgentSelector, item.Id);
                continue;
            }

            var candidate = await TryResolveCandidateAsync(item, state, callerName, onNoTemplate, ct);
            if (candidate is null) continue;

            yield return candidate;
        }
    }

    /// <summary>
    /// Resolves the job template and PVC gate for a single item.
    /// Returns a <see cref="DispatchCandidate"/> if the item is eligible, or <c>null</c> to skip it.
    /// </summary>
    private async Task<DispatchCandidate?> TryResolveCandidateAsync(
        PendingWorkItemProjection item,
        DispatchState state,
        string callerName,
        Func<PendingWorkItemProjection, string, CancellationToken, Task> onNoTemplate,
        CancellationToken ct)
    {
        var (template, effectiveSelector, skipItem) = await ResolveTemplateAsync(
            item, state.ConcurrencyBySelector, callerName, ct);

        if (skipItem || template is null)
        {
            // TODO: When skipItem=true and template is also null simultaneously, the onNoTemplate
            // callback is correctly suppressed (the !skipItem guard below prevents it). However this
            // reduces observability: there is no log/metric indicating that onNoTemplate was suppressed
            // rather than invoked, which could mask misconfigured selectors. Consider adding a
            // diagnostic log here for the (skipItem && template is null) case.
            // See review finding: DotNetSpecialist WARNING DispatchStateBuilder.cs:206
            if (!skipItem)
                await onNoTemplate(item, $"No job template for selector: {item.AgentSelector}", ct);
            return null;
        }

        var isKiroAgent = string.Equals(template.ProviderType, "kiro", StringComparison.OrdinalIgnoreCase);

        if (IsKiroAgentWithoutPvc(isKiroAgent, state.AvailablePvcs))
        {
            // No PVC available — skip, leave Pending for next poll cycle (NOT failed)
            Log.Information("{CallerName}: no PVC available for kiro agent, skipping WorkItem {WorkItemId}",
                callerName, item.Id);
            return null;
        }

        return new DispatchCandidate(item, template, effectiveSelector, isKiroAgent);
    }

    /// <summary>
    /// Returns true if the given selector group is at or above its concurrency limit.
    /// A limit of 0 means no limit is configured — always returns false.
    /// </summary>
    internal static bool IsAtConcurrencyLimit(
        string? agentSelector,
        Dictionary<string, int> concurrencyBySelector,
        int maxConcurrent)
    {
        if (maxConcurrent <= 0)
            return false;
        var current = concurrencyBySelector.GetValueOrDefault(agentSelector ?? "", 0);
        return current >= maxConcurrent;
    }

    /// <summary>
    /// Returns true if the template targets a kiro agent but no PVCs are available.
    /// Non-kiro agents always return false (they do not require PVCs).
    /// </summary>
    internal static bool IsKiroAgentWithoutPvc(bool isKiroAgent, List<string> availablePvcs)
        => isKiroAgent && availablePvcs.Count == 0;

    /// <summary>
    /// Resolves the job template for a pending work item, applying profile-based fallback when needed.
    /// Returns (template, effectiveSelector, skip=true) when the item should be skipped due to
    /// concurrency limit on the resolved selector. Returns (null, selector, skip=false) when no
    /// template is found and the caller should invoke the onNoTemplate callback.
    /// </summary>
    private async Task<(JobTemplate? template, string effectiveSelector, bool skip)> ResolveTemplateAsync(
        PendingWorkItemProjection item,
        Dictionary<string, int> concurrencyBySelector,
        string callerName,
        CancellationToken ct)
    {
        var template = _templateProvider.Resolve(item.AgentSelector);
        var effectiveSelector = item.AgentSelector;

        if (template is not null)
            return (template, effectiveSelector, skip: false);

        // Fallback: AgentSelector might be a subset of the template's label set.
        // Resolve profile to get the full label set, then retry template lookup.
        var (fallbackTemplate, resolvedSelector) = await _templateResolver.ResolveTemplateViaProfileAsync(
            item.AgentSelector, callerName, ct);

        if (fallbackTemplate is null)
            return (null, effectiveSelector, skip: false);

        template = fallbackTemplate;
        effectiveSelector = resolvedSelector!;

        // Re-check concurrency limit against the resolved selector (the actual template key)
        var resolvedMaxConcurrent = template.MaxConcurrent;
        if (resolvedMaxConcurrent > 0)
        {
            var current = concurrencyBySelector.GetValueOrDefault(effectiveSelector, 0);
            if (current >= resolvedMaxConcurrent)
            {
                Log.Debug("{CallerName}: resolved selector {Selector} at concurrency limit ({Current}/{Max}), skipping {WorkItemId}",
                    callerName, effectiveSelector, current, resolvedMaxConcurrent, item.Id);
                return (null, effectiveSelector, skip: true);
            }
        }

        return (template, effectiveSelector, skip: false);
    }
}
