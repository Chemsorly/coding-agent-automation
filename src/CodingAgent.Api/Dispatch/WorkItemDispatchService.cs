using CodingAgent.Api.Client;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.LeaderElection;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CodingAgent.Api.Dispatch;

/// <summary>
/// BackgroundService that polls for non-consolidation Pending WorkItems
/// (Implementation, Review, Decomposition) ordered by <c>PriorityWeight DESC, CreatedAt ASC</c>
/// and dispatches them as K8s Jobs when capacity is available.
///
/// <para>
/// Flow: Scheduler enqueues an issue as a <c>Pending</c> WorkItem (visible in the UI queue) via
/// <c>KubernetesWorkDistributor.DistributeAsync → POST /api/work-items</c>. This service picks up
/// the item, checks concurrency and PVC availability, and calls
/// <c>DispatchLifecycleService.ExecuteDispatchLifecycleAsync</c> to create the K8s Job and
/// transition the WorkItem to <c>Dispatched</c>. Because <c>PriorityWeight</c> is applied at
/// poll time, operators can re-order the queue by updating it via
/// <c>POST /api/work-items/{id}/priority</c> before the item is claimed.
/// </para>
/// <para>
/// This service does NOT swap the issue label. While an item is Pending (queued) the issue stays
/// <c>agent:next</c> (see the <c>DistributionResult.Queued</c> contract); it moves to
/// <c>agent:in-progress</c> only when an agent actually picks up the run — in K8s dispatch mode that
/// is <c>AgentHub.RegisterAgent</c>, when the agent connects reporting the run as its active job.
/// </para>
/// <para>
/// <b>Multi-replica note:</b> all API replicas run this service simultaneously (via
/// <see cref="AlwaysLeaderService"/>). The CAS <c>TransitionIfAsync(Pending → Dispatched)</c>
/// in <see cref="DispatchLifecycleService"/> prevents the same item from being double-claimed.
/// However, <c>maxConcurrent</c> per selector is not atomically enforced across replicas:
/// two replicas can both read a concurrency snapshot showing capacity available and each
/// dispatch a different item for the same selector. This is an accepted limitation for
/// single-replica deployments; see <see cref="AlwaysLeaderService"/> for details.
/// </para>
/// </summary>
internal sealed class WorkItemDispatchService : LeaderElectedPollingService
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<WorkItemDispatchService>();

    private readonly DispatchLifecycleService _lifecycle;
    private readonly DispatchServiceOptions _options;
    private readonly DispatchStateBuilder _stateBuilder;
    private readonly WorkItemTransitionService _transitionService;
    private readonly IProviderFactory? _providerFactory;
    private readonly IProviderConfigStore? _providerConfigStore;

    protected override string ServiceName => "WorkItemDispatchService";
    protected override int PollIntervalSeconds => _options.PollIntervalSeconds;

    public WorkItemDispatchService(WorkItemDispatchServiceDependencies deps)
        : this(deps ?? throw new ArgumentNullException(nameof(deps)),
               DispatchServiceOptionsFactory.Create(deps.Configuration))
    { }

    /// <summary>
    /// Internal constructor accepting a pre-built <see cref="DispatchServiceOptions"/>.
    /// Used directly by tests to avoid requiring <see cref="IConfiguration"/>.
    /// </summary>
    internal WorkItemDispatchService(WorkItemDispatchServiceDependencies deps, DispatchServiceOptions options)
        : base((deps ?? throw new ArgumentNullException(nameof(deps))).LeaderElection,
               (options ?? throw new ArgumentNullException(nameof(options))).RateLimitPerSecond)
    {
        _lifecycle = deps.Lifecycle;
        _options = options;
        ArgumentNullException.ThrowIfNull(deps.StateBuilder, nameof(deps.StateBuilder));
        _stateBuilder = deps.StateBuilder;
        _transitionService = deps.TransitionService;
        _providerFactory = deps.ProviderFactory;
        _providerConfigStore = deps.ProviderConfigStore;
    }

    /// <inheritdoc/>
    protected override Task OnPollCycleAsync(CancellationToken ct) => PollAndDispatchAsync(ct);

    internal async Task PollAndDispatchAsync(CancellationToken ct)
    {
        // Poll all non-consolidation Pending WorkItems, ordered by PriorityWeight DESC, CreatedAt ASC.
        // recordTelemetry:false — only the Job Controller is the authoritative emitter for
        // workdistribution.dispatcher_last_poll_epoch_seconds and credential pool metrics.
        var state = await _stateBuilder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            ct);
        if (state is null)
            return;

        // Per-cycle eligibility cache: keyed by (issueProviderConfigId, issueIdentifier, taskType).
        // Ensures N items for the same issue/PR cost at most one upstream call per dispatch cycle.
        // Null value = eligibility not yet checked; true = eligible (keep); false = ineligible (cancel).
        // TaskType is included in the key to prevent collisions between a Review item (PR identifier)
        // and an Implementation item (issue identifier) that happen to share the same numeric string
        // under the same provider — each must use its own eligibility method independently.
        var eligibilityCache = new Dictionary<(string, string, WorkItemTaskType), bool?>(capacity: state.PendingItems.Count);

        await using (state.Db)
        {
            var rateLimiter = RateLimiter ?? throw new InvalidOperationException(
                $"{ServiceName} requires a rate limiter but RateLimiter is null. " +
                "Ensure the constructor passes rateLimitPerSecond to the base class.");
            await foreach (var candidate in _stateBuilder.GetEligibleCandidatesAsync(
                state, LeaderElection, rateLimiter,
                ServiceName,
                async (item, msg, token) =>
                    await FailWorkItemAsync(item.Id, msg, token),
                ct))
            {
                await DispatchItemAsync(
                    state.Db, candidate.Item, candidate.Template,
                    candidate.IsKiroAgent, state.AvailablePvcs, state.ConcurrencyBySelector,
                    eligibilityCache, ct);
            }
        }
    }

    /// <summary>
    /// Checks upstream eligibility for a pending work item before creating the K8s Job.
    /// Returns the cancellation reason string if the item is ineligible, or null if eligible / check not supported.
    /// On any exception or inconclusive result returns null (fail-open: leave item Pending for next cycle).
    /// Uses <paramref name="eligibilityCache"/> to avoid redundant upstream calls within one poll cycle.
    /// </summary>
    /// <param name="repoProviderConfigIdFromPayload">
    /// For Review items only: the repo provider config ID read from the WorkItem payload.
    /// Pass null to fall back to template lookup (usually also null — fails open).
    /// </param>
    internal async Task<string?> CheckEligibilityAsync(
        PendingWorkItemProjection item,
        Dictionary<(string, string, WorkItemTaskType), bool?> eligibilityCache,
        string? repoProviderConfigIdFromPayload,
        CancellationToken ct)
    {
        if (_providerFactory is null || _providerConfigStore is null)
            return null; // provider infrastructure not wired — fail open

        if (string.IsNullOrEmpty(item.IssueIdentifier) || string.IsNullOrEmpty(item.IssueProviderConfigId))
            return null; // missing identifiers — fail open

        var cacheKey = (item.IssueProviderConfigId, item.IssueIdentifier!, item.TaskType);
        if (eligibilityCache.TryGetValue(cacheKey, out var cached))
        {
            // Already checked this cycle; use cached result
            return cached == false ? BuildCancellationReason(item) : null;
        }

        try
        {
            bool eligible;
            if (item.TaskType == WorkItemTaskType.Review)
            {
                eligible = await CheckReviewItemEligibilityAsync(item, repoProviderConfigIdFromPayload, ct);
            }
            else
            {
                // Implementation, Decomposition, and other non-Consolidation types: check issue state.
                // TODO [WARNING]: Decomposition items are routed here (the else branch) and will be
                // cancelled by the gate if their epic issue is closed — even though the queue sweep
                // explicitly skips Decomposition items (fail-open) to avoid incorrect cancellation.
                // This inconsistency means a Decomposition WorkItem that survives the sweep can still
                // be cancelled by the pre-dispatch gate. To align with the sweep's fail-open contract,
                // Decomposition items should be excluded here (return true = eligible, fail open),
                // matching the switch-statement logic in SweepPendingWorkItemsAsync. Fix:
                //   if (item.TaskType == WorkItemTaskType.Decomposition ||
                //       item.TaskType == WorkItemTaskType.Consolidation)
                //       return true; // fail-open, not yet covered
                eligible = await CheckIssueItemEligibilityAsync(item, ct);
            }
            eligibilityCache[cacheKey] = eligible;
            return eligible ? null : BuildCancellationReason(item);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Network failure, rate limit, unsupported provider, etc. — fail open.
            Log.Warning(ex,
                "WorkItemDispatchService: eligibility check failed for WorkItem {WorkItemId} " +
                "(provider {IssueProviderConfigId}, identifier {IssueIdentifier}) — leaving Pending for next cycle",
                item.Id, item.IssueProviderConfigId, item.IssueIdentifier);
            eligibilityCache[cacheKey] = null; // inconclusive — don't cache as eligible or ineligible
            return null;
        }
    }

    private static string BuildCancellationReason(PendingWorkItemProjection item) =>
        item.TaskType == WorkItemTaskType.Review
            ? "PR closed or no longer eligible for dispatch"
            : "Issue closed or no longer eligible for dispatch";

    private async Task<bool> CheckIssueItemEligibilityAsync(PendingWorkItemProjection item, CancellationToken ct)
    {
        var issueProviderConfig = await _providerConfigStore!.GetProviderConfigByIdAsync(
            item.IssueProviderConfigId!, ProviderKind.Issue, ct);
        if (issueProviderConfig is null)
            return true; // provider config not found — fail open

        await using var issueProvider = _providerFactory!.CreateIssueProvider(issueProviderConfig);
        var isClosed = await issueProvider.IsIssueClosedAsync(
            new IssueIdentifier(item.IssueIdentifier!), ct);
        return !isClosed;
    }

    private async Task<bool> CheckReviewItemEligibilityAsync(
        PendingWorkItemProjection item,
        string? repoProviderConfigIdFromPayload,
        CancellationToken ct)
    {
        // For Review items: the WorkItem's IssueIdentifier is the PR number, and
        // the associated repo provider is found via the payload's repoProviderConfigId.
        var repoProviderConfigId = repoProviderConfigIdFromPayload
            ?? _stateBuilder.FindRepoProviderIdForIssueProvider(item.IssueProviderConfigId!);

        if (repoProviderConfigId is null)
            return true; // no repo provider available — fail open

        var repoProviderConfig = await _providerConfigStore!.GetProviderConfigByIdAsync(
            repoProviderConfigId, ProviderKind.Repository, ct);
        if (repoProviderConfig is null)
            return true; // repo provider config not found — fail open

        if (!int.TryParse(item.IssueIdentifier, out var prNumber))
            return true; // PR identifier is not a number — fail open (unexpected)

        await using var repoProvider = _providerFactory!.CreateRepositoryProvider(repoProviderConfig);
        // Use IsPullRequestClosedAsync — defaults to false (fail-open) on unsupported providers
        var isClosed = await repoProvider.IsPullRequestClosedAsync(prNumber, ct);
        return !isClosed;
    }

    private async Task DispatchItemAsync(
        PipelineDbContext db,
        PendingWorkItemProjection item,
        JobTemplate template,
        bool isKiroAgent,
        List<string> availablePvcs,
        Dictionary<string, int> concurrencyBySelector,
        Dictionary<(string, string, WorkItemTaskType), bool?> eligibilityCache,
        CancellationToken ct)
    {
        await _lifecycle.ExecuteDispatchLifecycleAsync(
            new DispatchLifecycleContext(db, item, template, isKiroAgent, availablePvcs, concurrencyBySelector, "workitem-dispatch ")
            {
                // Default ExpectedInitialStatus = Pending — items were created as Pending by the Scheduler.
            },
            prepareVariant: async workItem =>
            {
                // Pre-dispatch eligibility gate: re-check upstream issue/PR state before creating
                // the K8s Job. Restores the correctness guard from #2251/#2268 that was removed in #2322.
                // Fail-open on any exception (leaves item Pending for next cycle).
                //
                // For Review items, extract the repo provider config ID from the payload at this point
                // since we now have the full WorkItemEntity (avoiding a separate DB query).
                string? repoProviderConfigIdFromPayload = null;
                if (item.TaskType == WorkItemTaskType.Review && !string.IsNullOrEmpty(workItem.Payload))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(workItem.Payload);
                        if (doc.RootElement.TryGetProperty("repoProviderConfigId", out var prop))
                        {
                            // TODO [WARNING]: prop.GetString() returns "" when the JSON field is present
                            // but empty (e.g. test helpers that set RepoProviderConfigId = ""). An empty
                            // string bypasses the FindRepoProviderIdForIssueProvider fallback and is passed
                            // verbatim to GetProviderConfigByIdAsync("", ...), which returns null and causes
                            // the gate to fail open silently. Fix: treat empty as null:
                            //   repoProviderConfigIdFromPayload = prop.GetString() is { Length: > 0 } s ? s : null;
                            repoProviderConfigIdFromPayload = prop.GetString();
                        }
                    }
                    catch { /* malformed JSON — fail open */ }
                }

                var cancellationReason = await CheckEligibilityAsync(
                    item, eligibilityCache, repoProviderConfigIdFromPayload, ct);
                if (cancellationReason is not null)
                {
                    Log.Information(
                        "WorkItemDispatchService: pre-dispatch gate: cancelling WorkItem {WorkItemId} " +
                        "(type {TaskType}, identifier {IssueIdentifier}, provider {IssueProviderConfigId}): {Reason}",
                        item.Id, item.TaskType, item.IssueIdentifier, item.IssueProviderConfigId, cancellationReason);
                    await _transitionService.TransitionAsync(
                        item.Id,
                        WorkItemStatus.Cancelled,
                        WorkItemMutationFactory.Cancelled(cancellationReason),
                        ct: ct);
                    return (shouldContinue: false, projectSecrets: null);
                }

                // Load project secrets if a project is configured.
                Dictionary<string, string>? projectSecrets = null;
                if (workItem.ProjectId.HasValue)
                    projectSecrets = await DispatchLifecycleService.LoadProjectSecretsAsync(
                        db, workItem.ProjectId.Value.ToString(), ct);
                return (shouldContinue: true, projectSecrets);
            },
            onDispatchSuccess: _ =>
            {
                // No label swap here. The issue stays agent:next until an agent actually picks up the
                // run and registers (AgentHub.RegisterAgent swaps it to agent:in-progress). Creating
                // the K8s Job does not by itself mean an agent has started working.
                Log.Information(
                    "WorkItemDispatchService: K8s Job created for WorkItem {WorkItemId} (issue {IssueIdentifier})",
                    item.Id, item.IssueIdentifier);
                return Task.CompletedTask;
            },
            ct,
            onFailure: async (workItemId, errorMessage) =>
            {
                Log.Warning(
                    "WorkItemDispatchService: dispatch failed for WorkItem {WorkItemId} (issue {IssueIdentifier}): {Error}",
                    workItemId, item.IssueIdentifier, errorMessage);
            });
    }

    private async Task FailWorkItemAsync(Guid workItemId, string errorMessage, CancellationToken ct)
    {
        Log.Error("WorkItemDispatchService: failing WorkItem {WorkItemId}: {Error}", workItemId, errorMessage);
        await _lifecycle.FailWorkItemAsync(workItemId, errorMessage, ct);
    }
}
