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
    private readonly IProviderFactory? _providerFactory;
    private readonly IProviderConfigStore? _providerConfigStore;
    private readonly IProjectStore? _projectStore;

    /// <summary>
    /// Per-cycle cache of eligibility check results keyed by (issueProviderConfigId, issueIdentifier, taskType).
    /// TaskType is included in the key because the same numeric identifier can be used as both an
    /// issue number (Implementation) and a PR number (Review) for the same provider — the two
    /// upstream checks are logically different and must not share a cache slot.
    /// Prevents duplicate upstream API calls for multiple pending items targeting the same issue/PR.
    /// Reset at the start of each <see cref="PollAndDispatchAsync"/> call.
    /// </summary>
    // TODO [WARNING]: _eligibilityCache is an instance field reset at PollAndDispatchAsync start.
    // Between cycles it holds stale data. If IsEligibleForDispatchAsync were ever called outside
    // PollAndDispatchAsync (e.g. from a test or future concurrent path), stale results would be
    // returned. Consider using a local variable threaded through instead.
    private Dictionary<(string ProviderConfigId, string Identifier, WorkItemTaskType TaskType), bool>? _eligibilityCache;

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
    }

    /// <inheritdoc/>
    protected override Task OnPollCycleAsync(CancellationToken ct) => PollAndDispatchAsync(ct);

    internal async Task PollAndDispatchAsync(CancellationToken ct)
    {
        // Reset per-cycle eligibility cache. One upstream call per (providerConfigId, identifier, taskType).
        _eligibilityCache = new Dictionary<(string, string, WorkItemTaskType), bool>();

        // Poll all non-consolidation Pending WorkItems, ordered by PriorityWeight DESC, CreatedAt ASC.
        // recordTelemetry:false — only the Job Controller is the authoritative emitter for
        // workdistribution.dispatcher_last_poll_epoch_seconds and credential pool metrics.
        var state = await _stateBuilder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            ct);
        if (state is null)
            return;

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
                    candidate.IsKiroAgent, state.AvailablePvcs, state.ConcurrencyBySelector, ct);
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
        CancellationToken ct)
    {
        await _lifecycle.ExecuteDispatchLifecycleAsync(
            new DispatchLifecycleContext(db, item, template, isKiroAgent, availablePvcs, concurrencyBySelector, "workitem-dispatch ")
            {
                // Default ExpectedInitialStatus = Pending — items were created as Pending by the Scheduler.
            },
            prepareVariant: async workItem =>
            {
                // ── Pre-dispatch eligibility gate ──────────────────────────────────────────
                // Re-check upstream issue/PR eligibility before creating the K8s Job.
                // This restores the #2251 / #2268 dispatch-time gate that was removed with
                // DispatchLoop in #2322 and not restored when the Pending queue was re-introduced
                // in #2493. Fail-open: if the check cannot be performed (provider unavailable,
                // network error, unsupported provider type), dispatch proceeds normally.
                if (!await IsEligibleForDispatchAsync(item, ct))
                {
                    Log.Information(
                        "WorkItemDispatchService: pre-dispatch eligibility check failed for WorkItem {WorkItemId} ({TaskType}) " +
                        "(issue/PR {IssueIdentifier}, provider {IssueProviderConfigId}) — cancelling before K8s Job creation",
                        item.Id, item.TaskType, item.IssueIdentifier, item.IssueProviderConfigId);
                    await CancelWorkItemAsync(item.Id, item.TaskType == WorkItemTaskType.Review
                        ? "PR closed or no longer eligible for dispatch (pre-dispatch gate)"
                        : "Issue closed or no longer eligible for dispatch (pre-dispatch gate)", ct);
                    return (shouldContinue: false, null);
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
    /// Performs a pre-dispatch eligibility re-check for a pending work item.
    /// Returns <c>true</c> if the item should be dispatched, <c>false</c> if it should be cancelled.
    /// Returns <c>true</c> (fail-open) if:
    /// <list type="bullet">
    ///   <item>Provider infrastructure is not available (<see cref="IProviderFactory"/> or <see cref="IProviderConfigStore"/> not injected).</item>
    ///   <item>The provider config cannot be found.</item>
    ///   <item>Any exception occurs during the check (network error, rate limit, unsupported provider).</item>
    ///   <item>The task type is not Implementation or Review (Decomposition, Consolidation — conservative fail-open).</item>
    /// </list>
    /// Uses a per-cycle cache to avoid duplicate upstream API calls for multiple items
    /// targeting the same issue or PR.
    /// </summary>
    private async Task<bool> IsEligibleForDispatchAsync(PendingWorkItemProjection item, CancellationToken ct)
    {
        // Fail-open if provider infrastructure not available.
        if (_providerFactory is null || _providerConfigStore is null)
            return true;

        if (string.IsNullOrEmpty(item.IssueIdentifier) || string.IsNullOrEmpty(item.IssueProviderConfigId))
            return true;

        // Only check Implementation and Review items; Decomposition/Consolidation — fail open.
        if (item.TaskType != WorkItemTaskType.Implementation && item.TaskType != WorkItemTaskType.Review)
            return true;

        var cacheKey = (item.IssueProviderConfigId, item.IssueIdentifier, item.TaskType);
        if (_eligibilityCache is not null && _eligibilityCache.TryGetValue(cacheKey, out var cached))
            return cached;

        bool eligible;
        try
        {
            eligible = await CheckEligibilityUpstreamAsync(item, ct);
        }
        catch (NotSupportedException)
        {
            // Provider does not support the required check — fail open.
            Log.Debug(
                "WorkItemDispatchService: eligibility check not supported for {TaskType} item {WorkItemId} — skipping (fail-open)",
                item.TaskType, item.Id);
            eligible = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Network error, rate limit, or other transient failure — fail open.
            Log.Warning(ex,
                "WorkItemDispatchService: eligibility check failed for WorkItem {WorkItemId} (issue {IssueIdentifier}) — dispatching anyway (fail-open)",
                item.Id, item.IssueIdentifier);
            eligible = true;
        }

        // Cache the result — including fail-open (eligible=true) from exceptions — so a second item
        // for the same (provider, identifier, taskType) in the same cycle does not repeat the call.
        if (_eligibilityCache is not null)
            _eligibilityCache[cacheKey] = eligible;

        return eligible;
    }

    /// <summary>
    /// Performs the actual upstream eligibility check.
    /// For Implementation items: checks that the issue is open and has <c>agent:next</c>.
    /// For Review items: checks that the PR is open using <see cref="IPullRequestProvider.ListOpenPullRequestsAsync"/>.
    /// </summary>
    private async Task<bool> CheckEligibilityUpstreamAsync(PendingWorkItemProjection item, CancellationToken ct)
    {
        // TODO [WARNING]: LoadProviderConfigsAsync(ProviderKind.Issue) runs for every cache-miss,
        // including for Review items where the config object is only used to confirm the provider ID
        // exists (the actual repo provider is resolved separately). This is an unnecessary DB/network
        // call for every Review item. Consider branching before this call: skip the issue-provider
        // lookup entirely for Review items and go straight to the _projectStore template resolution.
        var configs = await _providerConfigStore!.LoadProviderConfigsAsync(ProviderKind.Issue, ct);
        var config = configs.FirstOrDefault(c => c.Id == item.IssueProviderConfigId);
        if (config is null)
        {
            Log.Debug(
                "WorkItemDispatchService: provider config '{ProviderConfigId}' not found — skipping eligibility check (fail-open)",
                item.IssueProviderConfigId);
            return true; // fail-open
        }

        var identifier = new IssueIdentifier(item.IssueIdentifier!);

        if (item.TaskType == WorkItemTaskType.Implementation)
        {
            // Check that the issue is still open and has agent:next.
            // TODO [WARNING]: Two sequential upstream calls (IsIssueClosedAsync + GetIssueAsync) happen per
            // cache-miss. The _eligibilityCache is only written after both calls complete. If PollAndDispatchAsync
            // were ever parallelised, a second item for the same (provider, issue) could enter this block
            // concurrently and make duplicate calls. Currently safe because PollAndDispatchAsync is serial,
            // but fragile. Consider combining into a single GetIssueAsync call and checking closed+label together.
            await using var issueProvider = _providerFactory!.CreateIssueProvider(config);
            var isClosed = await issueProvider.IsIssueClosedAsync(identifier, ct);
            if (isClosed)
            {
                Log.Information(
                    "WorkItemDispatchService: issue {IssueIdentifier} is closed — WorkItem {WorkItemId} is ineligible for dispatch",
                    item.IssueIdentifier, item.Id);
                return false;
            }

            // Also verify agent:next label is still present to catch cases where the label
            // was removed while the item was in the Pending queue.
            // TODO [WARNING]: issue.Labels == null is treated as fail-open (item considered eligible).
            // This is intentional for providers that don't return label data, but creates a silent
            // eligibility bypass for providers that represent "no labels" as null rather than an
            // empty list. If GetIssueAsync returns null Labels on success, agent:next removal is
            // not detected. Document or enforce that providers return an empty list (not null) when
            // the issue has no labels.
            var issue = await issueProvider.GetIssueAsync(identifier, ct);
            if (issue.Labels is not null && !issue.Labels.Contains(AgentLabels.Next, StringComparer.OrdinalIgnoreCase))
            {
                Log.Information(
                    "WorkItemDispatchService: issue {IssueIdentifier} no longer has agent:next label — WorkItem {WorkItemId} is ineligible",
                    item.IssueIdentifier, item.Id);
                return false;
            }
            return true;
        }

        if (item.TaskType == WorkItemTaskType.Review)
        {
            // For Review items, IssueIdentifier stores the PR number.
            // Use IPullRequestProvider (via IRepositoryProvider) rather than IIssueProvider
            // because on GitLab MRs are not accessible as issues.
            //
            // Find the repo provider config that corresponds to this WorkItem's IssueProviderConfigId
            // by looking up the template that has a matching IssueProviderId. In a multi-repository
            // setup there can be multiple repo provider configs; taking the first one without matching
            // would check the wrong repository's PR list and incorrectly cancel or pass items.
            ProviderConfig? repoConfig = null;
            if (_projectStore is not null)
            {
                try
                {
                    var allTemplates = await _projectStore.LoadAllTemplatesAsync(ct);
                    // TODO [WARNING]: FirstOrDefault picks an arbitrary template when multiple templates share
                    // the same IssueProviderId but target different repositories. In a multi-template setup this
                    // can cause the wrong repository's PR list to be consulted, producing false-negatives
                    // (valid PR not found → item incorrectly cancelled) or false-positives (PR found in wrong
                    // repo → stale item not cancelled). A disambiguation strategy (e.g. matching on WorkItem
                    // AgentSelector or a stored RepoProviderId on the WorkItem) is needed for multi-repo safety.
                    var matchingTemplate = allTemplates.FirstOrDefault(t =>
                        string.Equals(t.IssueProviderId, item.IssueProviderConfigId, StringComparison.Ordinal));

                    if (matchingTemplate is not null)
                    {
                        repoConfig = await _providerConfigStore!.GetProviderConfigByIdAsync(
                            matchingTemplate.RepoProviderId, ProviderKind.Repository, ct);
                    }

                    if (repoConfig is null)
                    {
                        Log.Debug(
                            "WorkItemDispatchService: no template with IssueProviderId '{IssueProviderId}' found or repo config missing — skipping Review eligibility check (fail-open)",
                            item.IssueProviderConfigId);
                        return true; // fail-open
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    Log.Warning(ex,
                        "WorkItemDispatchService: failed to resolve repo config for Review item {WorkItemId} — skipping eligibility check (fail-open)",
                        item.Id);
                    return true; // fail-open
                }
            }
            else
            {
                // ProjectStore not injected — fall back to first available repo config.
                // TODO [WARNING]: Without ProjectStore, Review items are checked against the first
                // available repository provider config. In multi-repository setups this may pick the
                // wrong repository, causing incorrect cancellations or false eligibility results.
                // Inject IProjectStore via WorkItemDispatchServiceDependencies.ProjectStore to fix.
                var repoConfigs = await _providerConfigStore!.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
                repoConfig = repoConfigs.FirstOrDefault();
                if (repoConfig is null)
                {
                    Log.Debug(
                        "WorkItemDispatchService: no repo provider config found for Review item {WorkItemId} — skipping eligibility check (fail-open)",
                        item.Id);
                    return true;
                }
            }

            if (!int.TryParse(item.IssueIdentifier, out var prNumber))
            {
                Log.Debug(
                    "WorkItemDispatchService: Review item {WorkItemId} IssueIdentifier '{IssueIdentifier}' is not a PR number — skipping eligibility check (fail-open)",
                    item.Id, item.IssueIdentifier);
                return true;
            }

            await using var repoProvider = _providerFactory!.CreateRepositoryProvider(repoConfig);
            // List open PRs with agent:next to check if this PR is still eligible.
            // TODO [WARNING]: pageSize: 100 is a hard cap with no full pagination. If more than 100 PRs carry
            // agent:next, the target PR may not appear and the WorkItem is incorrectly cancelled.
            // Consider paginating or increasing the page size in a future improvement.
            // Guard: if the result set hits the cap boundary, treat it as inconclusive and fail open
            // (the same conservative approach used for network errors and rate limits).
            const int prPageSize = 100;
            try
            {
                var openPrs = await repoProvider.ListOpenPullRequestsAsync(
                    page: 1, pageSize: prPageSize,
                    labels: [AgentLabels.Next],
                    ct);

                // If the result set is exactly at the cap, we may have missed PRs due to truncation.
                // Treat as inconclusive (fail-open) to avoid cancelling a valid WorkItem whose PR
                // simply didn't appear in the first page.
                if (openPrs.Items.Count >= prPageSize)
                {
                    Log.Warning(
                        "WorkItemDispatchService: PR list for Review item {WorkItemId} returned {Count} results (cap={Cap}) — " +
                        "result may be truncated; treating as inconclusive (fail-open) to avoid incorrect cancellation",
                        item.Id, openPrs.Items.Count, prPageSize);
                    return true;
                }

                var isEligible = openPrs.Items.Any(pr =>
                    pr.Identifier == item.IssueIdentifier);

                if (!isEligible)
                {
                    Log.Information(
                        "WorkItemDispatchService: PR {PrNumber} is no longer open/eligible (not in agent:next PRs) — WorkItem {WorkItemId} is ineligible for dispatch",
                        prNumber, item.Id);
                }
                return isEligible;
            }
            catch (NotSupportedException)
            {
                // Provider doesn't support ListOpenPullRequestsAsync — fail open.
                return true;
            }
        }

        return true; // Other task types — fail open
    }

    private async Task CancelWorkItemAsync(Guid workItemId, string reason, CancellationToken ct)
    {
        try
        {
            await _lifecycle.CancelWorkItemAsync(workItemId, reason, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "WorkItemDispatchService: failed to cancel WorkItem {WorkItemId} after eligibility check failure — will retry next cycle",
                workItemId);
        }
    }

    private async Task FailWorkItemAsync(Guid workItemId, string errorMessage, CancellationToken ct)
    {
        Log.Error("WorkItemDispatchService: failing WorkItem {WorkItemId}: {Error}", workItemId, errorMessage);
        await _lifecycle.FailWorkItemAsync(workItemId, errorMessage, ct);
    }
}
