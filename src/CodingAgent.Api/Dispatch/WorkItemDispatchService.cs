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
    private readonly IProviderFactory? _providerFactory;
    private readonly IProviderConfigStore? _providerConfigStore;
    private readonly IProjectStore? _projectStore;
    private readonly WorkItemTransitionService _transitionService;

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
        _providerFactory = deps.ProviderFactory;
        _providerConfigStore = deps.ProviderConfigStore;
        _projectStore = deps.ProjectStore;
        // TODO [WARNING]: _transitionService is assigned here without a null guard, unlike deps.StateBuilder
        // above which uses ArgumentNullException.ThrowIfNull. If a caller constructs
        // WorkItemDispatchServiceDependencies with a null TransitionService (e.g. via default/reflection),
        // the NullReferenceException surfaces only at the point of use (TransitionAsync call) rather than
        // at construction time. Add: ArgumentNullException.ThrowIfNull(deps.TransitionService, nameof(deps.TransitionService));
        _transitionService = deps.TransitionService;
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

        // Per-cycle eligibility cache: (providerConfigId, issueIdentifier) → eligible?
        // null means the check failed (fail-open). Allocated fresh each cycle so stale
        // results from a previous cycle never carry over.
        var eligibilityCache = new Dictionary<(string, string), bool?>();

        // Per-cycle cache: issue provider config ID → repo provider config ID (from templates).
        // Populated lazily on the first Review item encountered this cycle.
        var issueToRepoProviderIdCache = new Dictionary<string, string?>();

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
                    eligibilityCache, issueToRepoProviderIdCache, ct);
            }
        }
    }

    private async Task DispatchItemAsync(
        PipelineDbContext db,
        PendingWorkItemProjection item,
        JobTemplate template,
        bool isKiroAgent,
        List<string> availablePvcs,
        Dictionary<string, int> concurrencyBySelector,
        Dictionary<(string providerConfigId, string issueIdentifier), bool?> eligibilityCache,
        Dictionary<string, string?> issueToRepoProviderIdCache,
        CancellationToken ct)
    {
        await _lifecycle.ExecuteDispatchLifecycleAsync(
            new DispatchLifecycleContext(db, item, template, isKiroAgent, availablePvcs, concurrencyBySelector, "workitem-dispatch ")
            {
                // Default ExpectedInitialStatus = Pending — items were created as Pending by the Scheduler.
            },
            prepareVariant: async workItem =>
            {
                // Pre-dispatch eligibility gate: re-check upstream state before creating a K8s Job.
                // This is a defence-in-depth check — the queue sweep (PipelineLoopService) should
                // have already cancelled stale items, but items can still reach dispatch before the
                // sweep runs (e.g. enqueued between sweep cycles, or sweep disabled).
                //
                // On any exception or inconclusive result: fail-open (shouldContinue: true).
                // RetryCount is NOT incremented — we cancel via TransitionAsync to Cancelled,
                // not via FailWorkItemAsync. The WorkItemMutationFactory.Cancelled() mutation only
                // sets CompletedAt, not RetryCount.
                // TODO [WARNING]: When _providerFactory or _providerConfigStore is null (optional DI
                // dependencies absent), the gate is silently skipped with no log line. A misconfigured
                // deployment or test that wires the service without these dependencies will invisibly
                // bypass the eligibility gate. Consider adding a Log.Debug/Warning here to make the
                // skipped gate observable (e.g. "pre-dispatch eligibility gate skipped — provider
                // dependencies not configured").
                if (workItem.IssueIdentifier is not null && workItem.IssueProviderConfigId is not null
                    && _providerFactory is not null && _providerConfigStore is not null)
                {
                    var isEligible = await CheckEligibilityAsync(
                        workItem.TaskType,
                        workItem.IssueIdentifier,
                        workItem.IssueProviderConfigId,
                        eligibilityCache,
                        issueToRepoProviderIdCache,
                        ct);

                    if (isEligible == false)
                    {
                        var reason = workItem.TaskType == WorkItemTaskType.Review
                            ? "PR closed or no longer eligible for dispatch"
                            : "Issue closed or no longer eligible for dispatch";

                        Log.Information(
                            "WorkItemDispatchService: cancelling ineligible {TaskType} WorkItem {WorkItemId} " +
                            "({IssueIdentifier}, provider {ProviderConfigId}) before K8s Job creation — {Reason}",
                            workItem.TaskType, workItem.Id,
                            workItem.IssueIdentifier, workItem.IssueProviderConfigId, reason);

                        // Cancel without incrementing RetryCount (use TransitionAsync with Cancelled mutation,
                        // not FailWorkItemAsync which goes through the failure path).
                        await _transitionService.TransitionAsync(workItem.Id, WorkItemStatus.Cancelled,
                            mutate: WorkItemMutationFactory.Cancelled(), ct: ct);

                        return (shouldContinue: false, projectSecrets: null);
                    }
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

    /// <summary>
    /// Checks whether the issue/PR for the given work item is still eligible for dispatch.
    /// Uses a per-cycle cache to avoid redundant upstream calls for multiple items targeting the same issue/PR.
    /// <para>
    /// Return values: <c>true</c> = eligible, <c>false</c> = ineligible (cancel), <c>null</c> = check failed
    /// (fail-open, do not cancel).
    /// </para>
    /// <para>
    /// For <c>Review</c> items: looks up the repo provider config ID via <paramref name="issueToRepoProviderIdCache"/>
    /// (populated lazily from <see cref="IProjectStore.LoadAllTemplatesAsync"/>), then calls
    /// <see cref="IPullRequestProvider.ListOpenPullRequestsAsync"/> and checks whether the PR number appears
    /// in the result. This uses <c>IPullRequestProvider</c> — not <c>IIssueProvider</c> — so GitLab MRs
    /// are checked via the MR API, not the issue API. On <see cref="NotSupportedException"/> or any exception,
    /// fails open.
    /// </para>
    /// </summary>
    private async Task<bool?> CheckEligibilityAsync(
        WorkItemTaskType taskType,
        string issueIdentifier,
        string issueProviderConfigId,
        Dictionary<(string, string), bool?> cache,
        Dictionary<string, string?> issueToRepoProviderIdCache,
        CancellationToken ct)
    {
        var cacheKey = (issueProviderConfigId, issueIdentifier);
        if (cache.TryGetValue(cacheKey, out var cached))
            return cached;
        // TODO [WARNING]: Cache key does not include taskType. On GitLab, issue IIDs and MR IIDs are in
        // separate namespaces — Issue #5 and MR !5 can coexist in the same project with the same numeric
        // identifier. If an Implementation item (IssueIdentifier="5") and a Review item (IssueIdentifier="5")
        // share the same IssueProviderConfigId, they share this cache key. If Implementation runs first and
        // caches false (issue closed), the cached false is returned for Review — spuriously cancelling a live
        // MR. Fix: change the key to (issueProviderConfigId, issueIdentifier, taskType).
        if (_providerConfigStore is null || _providerFactory is null)
        {
            cache[cacheKey] = null;
            return null;
        }

        bool? result = null; // null = fail-open
        try
        {
            if (taskType == WorkItemTaskType.Review)
            {
                // Review WorkItems: IssueIdentifier is the PR number (as string).
                // Use IPullRequestProvider — do NOT use IIssueProvider (PR != issue on GitLab).
                // Find the repo provider config ID by scanning templates for one with ReviewEnabled
                // and this issue provider config ID.
                var repoConfigId = await GetRepoProviderConfigIdAsync(
                    issueProviderConfigId, issueToRepoProviderIdCache, ct);
                if (repoConfigId is null)
                {
                    // No ReviewEnabled template found for this issue provider — fail open
                    cache[cacheKey] = null;
                    return null;
                }

                var repoConfig = await _providerConfigStore.GetProviderConfigByIdAsync(
                    repoConfigId, ProviderKind.Repository, ct);
                if (repoConfig is null)
                {
                    cache[cacheKey] = null;
                    return null;
                }

                // IRepositoryProvider extends IPullRequestProvider — safe direct cast
                await using var repoProvider = _providerFactory.CreateRepositoryProvider(repoConfig);
                // TODO [WARNING]: This direct cast to IPullRequestProvider assumes all IRepositoryProvider
                // implementations also implement IPullRequestProvider (the interface hierarchy enforces this
                // today). If a future provider implements IRepositoryProvider without IPullRequestProvider,
                // this throws InvalidCastException, which is caught by the outer catch block and treated as
                // a transient failure (fail-open). This is the correct observable behaviour but the failure
                // mode is non-obvious. Consider using 'as' with a null check and explicit fail-open return
                // to make the unsupported-provider case visible in logs.
                var prProvider = (IPullRequestProvider)repoProvider;

                PagedResult<PullRequestSummary> prs;
                try
                {
                    // TODO [WARNING]: Only page 1 (up to 100 PRs) is fetched. If a repository has more
                    // than 100 open agent:next PRs, the target PR may be on a later page and will be
                    // reported absent — triggering a spurious cancellation of a live Review WorkItem.
                    // The sweep path (TemplatePoller.FetchAgentNextPullRequestsAsync) uses FetchAllPagesAsync
                    // with maxPagesToFetch to avoid this. Fix: either paginate until the identifier is found
                    // (or last page exhausted), or use a single-PR lookup (GetPullRequestAsync) if supported.
                    // Fetch open agent:next PRs and check whether our PR number appears.
                    // The per-cycle cache ensures at most one upstream call per unique
                    // (issueProviderConfigId, prNumber) combination per dispatch cycle.
                    prs = await prProvider.ListOpenPullRequestsAsync(
                        page: 1, pageSize: 100,
                        labels: [AgentLabels.Next],
                        ct);
                }
                catch (NotSupportedException)
                {
                    // Provider does not support ListOpenPullRequestsAsync — fail open
                    // TODO [WARNING]: This early-return inside the outer try block bypasses the
                    // consolidated 'cache[cacheKey] = result;' write at the bottom of CheckEligibilityAsync.
                    // The null written here is consistent with other fail-open paths, but the pattern is
                    // asymmetric: most fail-open paths return early with a direct cache write, while the
                    // happy path writes via the consolidated statement. If the outer catch or final write
                    // line is ever refactored, this asymmetry risks a missed or double write.
                    // Note: caching null here means no re-check is attempted for this (providerConfigId,
                    // identifier) pair for the rest of the cycle — intentional fail-open, but not documented.
                    cache[cacheKey] = null;
                    return null;
                }

                result = prs.Items.Any(pr => pr.Identifier == issueIdentifier);
            }
            else
            {
                // Implementation / Decomposition: check whether the issue is still open.
                // TODO [WARNING]: IsIssueClosedAsync only checks whether the issue is closed — it does not
                // verify that the issue still carries agent:next or lacks blocking labels (e.g. agent:done,
                // agent:in-progress). The original #2251 gate checked "open + label state". An item whose
                // issue is open but has lost agent:next will still be dispatched. The queue sweep (using
                // issueQueues, which only contains label-eligible candidates) catches this case; the dispatch
                // gate is weaker than the acceptance criterion "Issue no longer eligible: agent:next removed".
                var issueConfig = await _providerConfigStore.GetProviderConfigByIdAsync(
                    issueProviderConfigId, ProviderKind.Issue, ct);
                if (issueConfig is null)
                {
                    cache[cacheKey] = null;
                    return null;
                }

                await using var issueProvider = _providerFactory.CreateIssueProvider(issueConfig);
                var isClosed = await issueProvider.IsIssueClosedAsync(
                    new IssueIdentifier(issueIdentifier), ct);
                result = !isClosed;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Any exception (network, rate limit, unsupported) → fail open
            Log.Debug(ex,
                "WorkItemDispatchService: eligibility check failed for {TaskType} item {IssueIdentifier} " +
                "(provider {ProviderConfigId}) — failing open",
                taskType, issueIdentifier, issueProviderConfigId);
            result = null; // fail open
        }

        cache[cacheKey] = result;
        return result;
    }

    /// <summary>
    /// Returns the repo provider config ID for a template that has <c>ReviewEnabled = true</c>
    /// and uses <paramref name="issueProviderConfigId"/>. Result is cached in
    /// <paramref name="issueToRepoCache"/> so subsequent calls for the same issue provider
    /// do not re-query the project store. Returns null if no matching template is found (fail-open).
    /// </summary>
    private async Task<string?> GetRepoProviderConfigIdAsync(
        string issueProviderConfigId,
        Dictionary<string, string?> issueToRepoCache,
        CancellationToken ct)
    {
        if (issueToRepoCache.TryGetValue(issueProviderConfigId, out var cachedId))
            return cachedId;

        if (_projectStore is null)
            return null;

        try
        {
            var templates = await _projectStore.LoadAllTemplatesAsync(ct);
            // TODO [WARNING]: FirstOrDefault picks the first ReviewEnabled template with this IssueProviderId.
            // When multiple templates share the same IssueProviderId but use different RepoProviderIds
            // (e.g. two repositories under the same issue tracker), this always uses the first template's
            // repo provider. A Review WorkItem originating from the second repository will be checked against
            // the wrong repo provider, potentially triggering a spurious cancellation. To fix definitively,
            // WorkItems would need to carry a RepoProviderConfigId field. As a minimum, log a warning when
            // multiple eligible templates are found so operators can detect the ambiguity.
            var match = templates.FirstOrDefault(t =>
                t.IssueProviderId == issueProviderConfigId && t.ReviewEnabled);
            var repoId = match?.RepoProviderId;
            issueToRepoCache[issueProviderConfigId] = repoId;
            return repoId;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Fail open — do not write to cache so next cycle retries
            // TODO [WARNING]: Not writing to issueToRepoCache on failure means that if LoadAllTemplatesAsync
            // throws on the first Review item this cycle, every subsequent Review item for the same
            // issueProviderConfigId will also call LoadAllTemplatesAsync (cache miss each time). Under
            // sustained IProjectStore failures this results in one LoadAllTemplatesAsync call per Review item
            // per cycle rather than one per provider per cycle — contradicting the per-cycle-single-call
            // intent. The intent is correct (allow a retry next cycle), but consider writing a sentinel null
            // to the cache to suppress repeated calls within the same cycle while still allowing retries
            // on the next cycle (null would already suppress only within the cycle if the cache is cleared
            // each cycle — which it is, since issueToRepoProviderIdCache is allocated fresh in PollAndDispatchAsync).
            return null;
        }
    }

    private async Task FailWorkItemAsync(Guid workItemId, string errorMessage, CancellationToken ct)
    {
        Log.Error("WorkItemDispatchService: failing WorkItem {WorkItemId}: {Error}", workItemId, errorMessage);
        await _lifecycle.FailWorkItemAsync(workItemId, errorMessage, ct);
    }
}
