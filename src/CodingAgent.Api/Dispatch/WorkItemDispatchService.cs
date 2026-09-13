using CodingAgent.Api.Client;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline;
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
    private readonly IProviderConfigStore? _providerConfigStore;
    private readonly IProviderFactory? _providerFactory;

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
        _providerConfigStore = deps.ProviderConfigStore;
        _providerFactory = deps.ProviderFactory;
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

        // Per-cycle cache: (IssueProviderConfigId, IssueIdentifier, TaskType) → bool (true = eligible, false = ineligible)
        // TaskType is included in the key because Review items key on PR numbers and Implementation items
        // key on issue numbers: on GitLab these are independent sequences, so issue #5 and MR !5 can
        // coexist under the same provider. Without TaskType, the first eligibility result cached for
        // identifier "5" (e.g. issue open → true) would be incorrectly reused for the other type
        // (MR "5" which may be closed), or vice versa.
        // Ensures N Pending items for the same issue/PR cost at most one upstream call per cycle.
        var eligibilityCache = new Dictionary<(string, string, WorkItemTaskType), bool>(capacity: 16);

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

    private async Task DispatchItemAsync(
        PipelineDbContext db,
        PendingWorkItemProjection item,
        JobTemplate template,
        bool isKiroAgent,
        List<string> availablePvcs,
        Dictionary<string, int> concurrencyBySelector,
        Dictionary<(string, string, WorkItemTaskType), bool> eligibilityCache,
        CancellationToken ct)
    {
        await _lifecycle.ExecuteDispatchLifecycleAsync(
            new DispatchLifecycleContext(db, item, template, isKiroAgent, availablePvcs, concurrencyBySelector, "workitem-dispatch ")
            {
                // Default ExpectedInitialStatus = Pending — items were created as Pending by the Scheduler.
            },
            prepareVariant: async workItem =>
            {
                // ── Pre-dispatch eligibility gate (restores #2251 / #2268 behaviour) ──────────────
                // Re-check the upstream issue/PR state before creating a K8s Job. This prevents
                // wasting a pod on a closed issue or a PR that was merged/closed while the item
                // was sitting in the Pending queue. Fail-open: any exception or inconclusive
                // result leaves the item Pending for the next cycle (shouldContinue = true, no cancel).
                var providerConfigId = item.IssueProviderConfigId;
                var issueIdentifier = item.IssueIdentifier;
                if (!string.IsNullOrEmpty(providerConfigId) && !string.IsNullOrEmpty(issueIdentifier)
                    && _providerConfigStore is not null && _providerFactory is not null)
                {
                    var cacheKey = (providerConfigId, issueIdentifier, item.TaskType);
                    if (!eligibilityCache.TryGetValue(cacheKey, out var isEligible))
                    {
                        isEligible = await CheckEligibilityAsync(item, providerConfigId, issueIdentifier, ct);
                        eligibilityCache[cacheKey] = isEligible;
                    }

                    if (!isEligible)
                    {
                        // Cancel without incrementing RetryCount.
                        var reason = item.TaskType == WorkItemTaskType.Review
                            ? "PR closed or no longer eligible (pre-dispatch eligibility gate)"
                            : "Issue closed or no longer eligible (pre-dispatch eligibility gate)";
                        Log.Information(
                            "WorkItemDispatchService: cancelling ineligible WorkItem {WorkItemId} ({TaskType}) for {IssueIdentifier} — {Reason}",
                            workItem.Id, item.TaskType, issueIdentifier, reason);
                        await _transitionService.TransitionAsync(
                            workItem.Id,
                            WorkItemStatus.Cancelled,
                            e => { e.ErrorMessage = reason; },
                            ct: ct);
                        // TODO [WARNING]: TransitionAsync returns a bool indicating whether the transition
                        // succeeded. The return value is discarded here. If the transition is rejected
                        // (e.g. the item was concurrently dispatched by another replica between the
                        // eligibility check and this call — Pending→Dispatched), TransitionAsync still
                        // returns false/true depending on implementation, but this code always returns
                        // (shouldContinue: false, null), preventing K8s Job creation. In a concurrent
                        // scenario, a Dispatched→Cancelled transition may also occur for a live job
                        // because IsValidTransition(Dispatched, Cancelled) = true. Consider using
                        // TransitionIfAsync (CAS) or checking the returned bool and falling through to
                        // dispatch when the transition fails.
                        return (shouldContinue: false, null);
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
    /// Checks upstream eligibility for the given work item before dispatching.
    /// Returns <c>true</c> if the item is eligible (should proceed to K8s Job creation).
    /// Returns <c>false</c> only when the item is definitively ineligible (issue closed / PR closed).
    /// Returns <c>true</c> (fail-open) on any exception, inconclusive result, or unsupported provider.
    /// Never cancels or modifies the work item — the caller is responsible for any state transition.
    /// </summary>
    private async Task<bool> CheckEligibilityAsync(
        PendingWorkItemProjection item,
        string providerConfigId,
        string issueIdentifier,
        CancellationToken ct)
    {
        try
        {
            var providerConfig = await _providerConfigStore!.GetProviderConfigByIdAsync(
                providerConfigId, ProviderKind.Issue, ct);
            if (providerConfig is null)
                return true; // provider not found — fail open

            if (item.TaskType == WorkItemTaskType.Review)
            {
                // Review items: check via IPullRequestProvider (repo provider), not IIssueProvider.
                // On GitHub, PRs are accessible as issues, but on GitLab MRs are not.
                // Use the repo provider to check PR state correctly on both platforms.
                // The repo provider ID is not stored on the WorkItem; resolve by loading the
                // provider configs and finding a repo provider that matches.
                // If no repo provider is available or the call fails, fail open.
                return await CheckPrEligibilityAsync(providerConfig, issueIdentifier, ct);
            }
            else
            {
                // Implementation / Decomposition: check via IIssueProvider.
                // TODO [WARNING]: CreateIssueProvider may return null if the factory does not support
                // the provider type but returns null instead of throwing NotSupportedException. In that
                // case `await using (issueProvider)` is safe (null IAsyncDisposable is a no-op), but
                // issueProvider.IsIssueClosedAsync will throw NullReferenceException, which is caught
                // by the outer catch(Exception) and treated as fail-open. The Debug log would say
                // "eligibility check failed" — misleading for a null-return from the factory rather than
                // a network error. The same applies to CreateRepositoryProvider in CheckPrEligibilityAsync.
                var issueProvider = _providerFactory!.CreateIssueProvider(providerConfig);
                await using (issueProvider)
                {
                    var isClosed = await issueProvider.IsIssueClosedAsync(
                        new IssueIdentifier(issueIdentifier), ct);
                    // true = closed → not eligible
                    // false = open → eligible
                    return !isClosed;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Network error, rate limit, unsupported provider, etc. — fail open.
            Log.Debug(ex,
                "WorkItemDispatchService: eligibility check failed for WorkItem {WorkItemId} ({TaskType} {IssueIdentifier}) — leaving Pending (fail-open)",
                item.Id, item.TaskType, issueIdentifier);
            return true;
        }
    }

    /// <summary>
    /// Checks whether a Review WorkItem's PR is still open via <see cref="IPullRequestProvider"/>.
    /// Returns <c>true</c> (eligible) if the PR is open; <c>false</c> if it is confirmed closed.
    /// Fails open on any exception, unsupported provider, or missing configuration.
    /// </summary>
    private async Task<bool> CheckPrEligibilityAsync(
        ProviderConfig issueProviderConfig,
        string prIdentifier,
        CancellationToken ct)
    {
        // Attempt to load the corresponding repo provider config (same provider type, same owner/repo).
        // We match by provider type since the WorkItem only stores IssueProviderConfigId.
        // TODO [WARNING]: LoadProviderConfigsAsync is called on every CheckPrEligibilityAsync invocation.
        // The per-cycle eligibilityCache deduplicates by (providerConfigId, identifier, taskType), so for
        // N distinct PR numbers on the same provider this DB call is made N times per cycle even though
        // the repo provider config list does not change within a cycle. Consider caching the result at
        // the cycle level (keyed by issueProviderConfigId) or loading it once in PollAndDispatchAsync.
        var repoProviderConfigs = await _providerConfigStore!.LoadProviderConfigsAsync(ProviderKind.Repository, ct);

        // Find a repo provider of the same type that shares the same owner/repo settings.
        // This is a best-effort match; if none found, fail open.
        ProviderConfig? repoConfig = null;
        foreach (var config in repoProviderConfigs)
        {
            if (!string.Equals(config.ProviderType, issueProviderConfig.ProviderType, StringComparison.OrdinalIgnoreCase))
                continue;
            // Check if owner/repo settings match the issue provider
            if (SettingsMatch(issueProviderConfig, config))
            {
                repoConfig = config;
                break;
            }
        }

        if (repoConfig is null)
            return true; // no matching repo provider found — fail open

        try
        {
            var repoProvider = _providerFactory!.CreateRepositoryProvider(repoConfig);
            await using (repoProvider)
            {
                // Check if the PR number is still in the open PRs list (page 1 only, modest page size).
                // We check the first page assuming recently enqueued PRs are recent/visible.
                // If the PR is not on the first page, we fail open to avoid false cancellation.
                if (!int.TryParse(prIdentifier, out var prNumber))
                    return true; // can't parse PR number — fail open

                var openPrs = await repoProvider.ListOpenPullRequestsAsync(
                    page: 1, pageSize: 100, labels: null, ct);

                // If any open PR matches the number, the PR is still eligible.
                foreach (var pr in openPrs.Items)
                {
                    if (pr.Number == prNumber)
                        return true;
                }

                // PR not found in first page of open PRs.
                // Only return false (ineligible) when the page is non-empty and not paginated
                // (HasMore = false), meaning we fetched all open PRs and the PR is absent.
                // If HasMore = true there may be more open PRs — fail open to avoid false cancellation.
                if (!openPrs.HasMore && openPrs.Items.Count > 0)
                    return false; // all open PRs fetched, PR not present → closed/merged

                // Empty result or HasMore = true — inconclusive, fail open
                // TODO [WARNING]: When HasMore = true (repo has >100 open PRs), this method silently
                // fails open and the pre-dispatch gate becomes a no-op for this provider. The sweep
                // still works (it uses the already-polled full prQueues), but the dispatch-time gate
                // degrades completely with no log/counter. Add a Debug or Information log here when
                // failing open due to HasMore=true to make the degradation observable in production.
                //
                // TODO [WARNING]: When openPrs.Items.Count == 0 && !openPrs.HasMore (the provider
                // returned an empty list of open PRs with no more pages), this method returns true
                // (fail-open) rather than false. An empty page with HasMore=false unambiguously means
                // there are zero open PRs; the PR being checked is therefore definitively closed.
                // The current fail-open is the safe direction (avoids false cancellation), but it means
                // a Review item won't be caught by the dispatch-time gate when all PRs in the repo are
                // closed/merged. The sweep correctly handles this case (empty prQueues entry → item is
                // absent from prEligibleByProvider → cancelled on next cycle). Consider adding a log
                // line here similar to the HasMore=true case for observability.
                return true;
            }
        }
        catch (NotSupportedException)
        {
            // Provider does not support ListOpenPullRequestsAsync — fail open
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "WorkItemDispatchService: PR eligibility check threw — failing open");
            return true;
        }
    }

    /// <summary>
    /// Returns true if two provider configs have matching owner and repo settings
    /// (for mapping an issue provider to its corresponding repo provider).
    /// </summary>
    /// <remarks>
    /// TODO [WARNING]: When both configs lack an 'Owner' and/or 'Repo' key entirely,
    /// string.Equals(null, null, OrdinalIgnoreCase) returns true, so two configs with no
    /// owner/repo settings will match each other. In a deployment with multiple provider
    /// configs where none carry explicit owner/repo settings (e.g. self-hosted GitLab with
    /// path-based routing), the first repo provider config of the same type always matches
    /// regardless of which repository it targets. The consequence is fail-open (the wrong
    /// repo provider is used → PR appears open → returns true → no false cancellation), but
    /// the pre-dispatch gate silently becomes a no-op for those configs. Consider requiring
    /// at least one non-null key to match, or using a more specific provider-level identifier.
    /// </remarks>
    private static bool SettingsMatch(ProviderConfig issueConfig, ProviderConfig repoConfig)
    {
        const string ownerKey = ProviderSettingKeys.Owner;
        const string repoKey = ProviderSettingKeys.Repo;

        issueConfig.Settings.TryGetValue(ownerKey, out var issueOwner);
        repoConfig.Settings.TryGetValue(ownerKey, out var repoOwner);
        issueConfig.Settings.TryGetValue(repoKey, out var issueRepo);
        repoConfig.Settings.TryGetValue(repoKey, out var repoRepo);

        return string.Equals(issueOwner, repoOwner, StringComparison.OrdinalIgnoreCase)
            && string.Equals(issueRepo, repoRepo, StringComparison.OrdinalIgnoreCase);
    }

    private async Task FailWorkItemAsync(Guid workItemId, string errorMessage, CancellationToken ct)
    {
        Log.Error("WorkItemDispatchService: failing WorkItem {WorkItemId}: {Error}", workItemId, errorMessage);
        await _lifecycle.FailWorkItemAsync(workItemId, errorMessage, ct);
    }
}
