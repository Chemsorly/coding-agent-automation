using System.Runtime.CompilerServices;
using System.Threading.RateLimiting;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgentWebUI.Api.Dispatch;

/// <summary>
/// Encapsulates the shared dispatch state-building prologue and per-item gating logic
/// for the API's dispatch services. Builds the dispatch state (pending items,
/// concurrency map, PVC availability) and yields eligible candidates lazily via
/// <see cref="GetEligibleCandidatesAsync"/>.
/// </summary>
internal sealed class DispatchStateBuilder
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<DispatchStateBuilder>();

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
    /// ConsolidationWorkItemDispatchService: w.TaskType == WorkItemTaskType.Consolidation
    /// </param>
    /// <param name="recordTelemetry">Whether to record poll telemetry.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<DispatchState?> BuildStateAsync(
        System.Linq.Expressions.Expression<Func<WorkItemEntity, bool>> taskTypeFilter,
        bool recordTelemetry,
        CancellationToken ct)
    {
        var db = await _dbFactory.CreateDbContextAsync(ct);
        try
        {
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
            {
                // NOTE: RecordLastPollEpoch and DispatcherPollCount are intentionally NOT called here.
                // WorkDistributionTelemetry uses process-static backing fields — both the API and the
                // Job Controller export the same metric names (workdistribution.dispatcher_last_poll_epoch_seconds,
                // workdistribution.credential_pool_available, etc.). The Helm PrometheusRules for
                // DispatcherStalled / CredentialPoolExhausted designate the Job Controller as the sole
                // authoritative source. Writing from the API's consolidation path produces a second
                // conflicting series that causes spurious alerts and masks real stalls.
                // The Job Controller's DispatchService.cs is the only caller of RecordLastPollEpoch /
                // UpdateCredentialPoolMetrics.
            }

            if (pendingItems.Count == 0)
            {
                await db.DisposeAsync();
                return null;
            }

            // Build concurrency state: count running/dispatched per selector group
            var activeCounts = await db.WorkItems
                .Where(w => w.Status == WorkItemStatus.Dispatched || w.Status == WorkItemStatus.Running)
                .GroupBy(w => w.AgentSelector)
                .Select(g => new { Selector = g.Key, Count = g.Count() })
                .ToListAsync(ct);

            var concurrencyBySelector = activeCounts.ToDictionary(x => x.Selector, x => x.Count);

            // PVC pool: determine available PVCs for kiro agents
            var pvcResult = await DispatchLifecycleService.QueryAvailablePvcsAsync(db, _options.KiroPvcPool, ct);
            var availablePvcs = pvcResult.AvailablePvcs;

            if (recordTelemetry)
            {
                // NOTE: UpdateCredentialPoolMetrics is intentionally NOT called here.
                // See the comment on RecordLastPollEpoch above — same reasoning applies.
                // The Job Controller's DispatchService.cs is the authoritative writer.
            }

            return new DispatchState
            {
                Db = db,
                PendingItems = pendingItems,
                ConcurrencyBySelector = concurrencyBySelector,
                AvailablePvcs = availablePvcs
            };
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Iterates pending items with per-item gating logic (rate limit, concurrency check,
    /// template resolution, PVC gate) and yields eligible candidates lazily.
    /// </summary>
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
            if (!skipItem)
                await onNoTemplate(item, $"No job template for selector: {item.AgentSelector}", ct);
            return null;
        }

        var isKiroAgent = string.Equals(template.ProviderType, "kiro", StringComparison.OrdinalIgnoreCase);

        if (IsKiroAgentWithoutPvc(isKiroAgent, state.AvailablePvcs))
        {
            Log.Information("{CallerName}: no PVC available for kiro agent, skipping WorkItem {WorkItemId}",
                callerName, item.Id);
            return null;
        }

        return new DispatchCandidate(item, template, effectiveSelector, isKiroAgent);
    }

    /// <summary>
    /// Resolves the job template for a pending work item, applying profile-based fallback when needed.
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
        var (fallbackTemplate, resolvedSelector) = await _templateResolver.ResolveTemplateViaProfileAsync(
            item.AgentSelector, callerName, ct);

        if (fallbackTemplate is null)
            return (null, effectiveSelector, skip: false);

        template = fallbackTemplate;
        effectiveSelector = resolvedSelector!;

        // Re-check concurrency limit against the resolved selector
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
}
