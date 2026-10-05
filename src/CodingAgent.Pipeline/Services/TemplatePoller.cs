using System.Collections.Concurrent;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Polls issues, PRs, and decomposition items from providers for each template.
/// Reports status changes via callbacks; mutates the shared <see cref="ConfigStatusSnapshot"/>
/// dictionary directly (single-threaded access from the loop).
/// </summary>
// TODO: Add direct unit tests for TemplatePoller. Currently tested only indirectly through
// integration-level PipelineLoopServiceTests. Direct tests should cover: auth error eviction,
// rate-limit detection, queue clearing on failure, and page fetching logic.
internal sealed class TemplatePoller
{
    private readonly ProviderCacheManager _cacheManager;
    private readonly Serilog.ILogger _logger;

    internal TemplatePoller(ProviderCacheManager cacheManager, Serilog.ILogger logger)
    {
        _cacheManager = cacheManager;
        _logger = logger;
    }

    /// <summary>
    /// Polls once per pollable template for issues, PRs, decomposition candidates, and agent:done PRs.
    /// </summary>
    internal async Task<(Dictionary<string, List<IssueSummary>> IssueQueues,
                          Dictionary<string, List<PullRequestSummary>> PrQueues,
                          Dictionary<string, List<EpicCandidate>> DecompositionQueues,
                          Dictionary<string, List<PullRequestSummary>> AgentDonePrQueues,
                          Dictionary<string, bool> AgentDonePrTruncated)>
        PollTemplateQueuesAsync(
            IReadOnlyList<PipelineJobTemplate> pollableTemplates,
            int maxPagesToFetch,
            ConcurrentDictionary<string, ConfigStatusSnapshot> templateStatuses,
            Action<int> reportTemplateIndex,
            Action<string> reportStatus,
            Action notifyChange,
            CancellationToken ct)
    {
        var queues = new PollQueues();

        for (int i = 0; i < pollableTemplates.Count; i++)
        {
            if (ct.IsCancellationRequested) break;

            var template = pollableTemplates[i];
            reportTemplateIndex(i);
            reportStatus($"🔄 Polling template '{template.Name}' ({i + 1} of {pollableTemplates.Count})");

            // Mark as currently polling
            templateStatuses[template.Id] = (templateStatuses.TryGetValue(template.Id, out var prev) ? prev : ConfigStatusSnapshot.Empty)
                with { IsCurrentlyPolling = true };
            notifyChange();

            try
            {
                await PollSingleTemplateAsync(template, maxPagesToFetch, templateStatuses, queues, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (RateLimitExceededException ex)
            {
                HandleRateLimitException(template, ex, templateStatuses, queues);
            }
            catch (Exception ex) when (IsAuthError(ex))
            {
                await HandleAuthErrorExceptionAsync(template, ex, templateStatuses, queues);
            }
            catch (Exception ex)
            {
                HandleGenericPollException(template, ex, templateStatuses, queues);
            }
        }

        return (queues.IssueQueues, queues.PrQueues, queues.DecompositionQueues, queues.AgentDonePrQueues, queues.AgentDonePrTruncated);
    }

    /// <summary>
    /// The per-template queues filled by one <see cref="PollTemplateQueuesAsync"/> pass, keyed by template ID.
    /// </summary>
    private sealed class PollQueues
    {
        public Dictionary<string, List<IssueSummary>> IssueQueues { get; } = new();
        public Dictionary<string, List<PullRequestSummary>> PrQueues { get; } = new();
        public Dictionary<string, List<EpicCandidate>> DecompositionQueues { get; } = new();
        public Dictionary<string, List<PullRequestSummary>> AgentDonePrQueues { get; } = new();
        public Dictionary<string, bool> AgentDonePrTruncated { get; } = new();

        /// <summary>Clears every queue entry for <paramref name="templateId"/>.</summary>
        public void ClearForTemplate(string templateId) =>
            ClearQueuesForTemplate(templateId, IssueQueues, PrQueues, DecompositionQueues, AgentDonePrQueues, AgentDonePrTruncated);
    }

    /// <summary>
    /// Polls issues, PRs, decomposition candidates, and agent:done PRs for a single template,
    /// then updates the success status.
    /// </summary>
    private async Task PollSingleTemplateAsync(
        PipelineJobTemplate template,
        int maxPagesToFetch,
        ConcurrentDictionary<string, ConfigStatusSnapshot> templateStatuses,
        PollQueues queues,
        CancellationToken ct)
    {
        await PollIssueQueueAsync(template, maxPagesToFetch, templateStatuses, queues.IssueQueues, ct);
        await PollPrQueueAsync(template, maxPagesToFetch, queues.PrQueues, ct);
        await PollDecompositionQueueAsync(template, maxPagesToFetch, queues.DecompositionQueues, ct);
        await PollAgentDonePrQueueAsync(template, maxPagesToFetch, queues.AgentDonePrQueues, queues.AgentDonePrTruncated, ct);

        // Success — update status (agentDonePrQueues not counted as dispatchable work)
        var issueCount = queues.IssueQueues[template.Id].Count;
        // prQueues[template.Id] may be absent when ReviewEnabled=false or when PR polling failed
        // and intentionally omitted the key (fail-open for BuildPrEligibilityMap). Use 0 in those cases.
        var prCount = queues.PrQueues.TryGetValue(template.Id, out var prList) ? prList.Count : 0;
        var decompCount = queues.DecompositionQueues[template.Id].Count;
        templateStatuses[template.Id] = new ConfigStatusSnapshot
        {
            LastPollTime = DateTimeOffset.UtcNow,
            LastPollIssueCount = issueCount + prCount + decompCount,
            LastError = null,
            ConsecutiveFailures = 0,
            RateLimitResetAt = null,
            IsCurrentlyPolling = false
        };
    }

    /// <summary>
    /// Polls the issue queue for a template (only when ImplementationEnabled).
    /// </summary>
    private async Task PollIssueQueueAsync(
        PipelineJobTemplate template,
        int maxPagesToFetch,
        ConcurrentDictionary<string, ConfigStatusSnapshot> templateStatuses,
        Dictionary<string, List<IssueSummary>> issueQueues,
        CancellationToken ct)
    {
        if (!template.ImplementationEnabled)
        {
            issueQueues[template.Id] = new List<IssueSummary>();
            return;
        }

        if (!_cacheManager.IssueProviders.TryGetValue(template.IssueProviderId, out var provider))
        {
            // Provider not in cache (config issue) — skip issues
            templateStatuses[template.Id] = new ConfigStatusSnapshot
            {
                LastPollTime = DateTimeOffset.UtcNow,
                LastError = $"Issue provider '{template.IssueProviderId}' not found in cache.",
                IsCurrentlyPolling = false
            };
            issueQueues[template.Id] = new List<IssueSummary>();
            return;
        }

        var issues = await FetchAgentNextIssuesForProviderAsync(provider, maxPagesToFetch, ct);
        issueQueues[template.Id] = issues;
    }

    /// <summary>
    /// Polls the PR queue for a template (only when ReviewEnabled).
    /// Wrapped in its own try-catch so that a PR polling failure does not discard the issue queue.
    /// On success, writes the fetched PRs to <paramref name="prQueues"/> (may be an empty list if
    /// no eligible PRs were found).
    /// On failure, the entry is intentionally NOT written to <paramref name="prQueues"/>, so
    /// <c>BuildPrEligibilityMap</c> sees an absent key and fails open (does not cancel Review
    /// WorkItems for this template's provider), rather than treating an empty result from a failed
    /// poll as "zero eligible PRs".
    /// </summary>
    private async Task PollPrQueueAsync(
        PipelineJobTemplate template,
        int maxPagesToFetch,
        Dictionary<string, List<PullRequestSummary>> prQueues,
        CancellationToken ct)
    {
        if (!template.ReviewEnabled) return;

        try
        {
            if (!_cacheManager.RepoProviders.TryGetValue(template.RepoProviderId, out var repoProvider))
            {
                _logger.Warning("Template '{TemplateName}': repo provider '{RepoProviderId}' not found in cache, skipping PR polling",
                    template.Name, template.RepoProviderId);
                return;
            }

            var prs = await FetchAgentNextPullRequestsAsync(repoProvider, maxPagesToFetch, ct);
            // Write result only on success so BuildPrEligibilityMap can distinguish a genuine
            // empty queue (present key, empty list) from a failed poll (absent key → fail open).
            prQueues[template.Id] = prs;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Template '{TemplateName}' PR polling failed, issue polling unaffected: {Error}",
                template.Name, ex.Message);
            // Intentionally omit prQueues[template.Id] on failure so BuildPrEligibilityMap
            // fails open for this template's provider rather than cancelling all Review WorkItems.
        }
    }

    /// <summary>
    /// Polls the PR queue for agent:done PRs (only when HousekeepingEnabled).
    /// Used by the housekeeping service (spec 040). Gated on HousekeepingEnabled only —
    /// NOT on ReviewEnabled, as housekeeping is an independent capability.
    /// Wrapped in its own try-catch to not affect issue/PR/decomposition queues on failure.
    /// </summary>
    private async Task PollAgentDonePrQueueAsync(
        PipelineJobTemplate template,
        int maxPagesToFetch,
        Dictionary<string, List<PullRequestSummary>> agentDonePrQueues,
        Dictionary<string, bool> agentDonePrTruncated,
        CancellationToken ct)
    {
        agentDonePrQueues[template.Id] = new List<PullRequestSummary>();
        agentDonePrTruncated[template.Id] = false;
        if (!template.HousekeepingEnabled) return;

        try
        {
            if (!_cacheManager.RepoProviders.TryGetValue(template.RepoProviderId, out var repoProvider))
            {
                _logger.Warning("Template '{TemplateName}': repo provider not found, skipping agent:done PR polling",
                    template.Name);
                return;
            }

            if (!repoProvider.SupportsServerSideBranchUpdate) return;

            var (prs, wasTruncated) = await FetchAgentDonePullRequestsAsync(repoProvider, maxPagesToFetch, ct);
            agentDonePrQueues[template.Id] = prs;
            agentDonePrTruncated[template.Id] = wasTruncated;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A polling failure means we have an empty PR list but cannot know whether it is
            // complete. Treat it the same as a truncated result: set wasInputTruncated=true so
            // that StaleBranchCleaner skips the cleanup cycle rather than deleting every agent
            // branch whose issue has a terminal label. This matches the safety behaviour of the
            // old FetchAllOpenAgentPrBranchesAsync throw path, which skipped cleanup on failure.
            agentDonePrTruncated[template.Id] = true;
            _logger.Warning(ex, "Template '{TemplateName}' agent:done PR polling failed: {Error}",
                template.Name, ex.Message);
        }
    }

    /// <summary>
    /// Fetches open agent-created PRs from a repository provider by matching the agent branch prefix.
    /// Uses branch name prefix (<c>feature/auto-</c>) rather than label filtering because the agent
    /// applies <c>agent:done</c> to the <em>issue</em>, not the PR — PRs have no agent status labels.
    /// IsDraft and active-run exclusion are applied in HousekeepingService.ExecuteAsync.
    /// Returns the fetched list and a flag indicating whether the result was truncated by
    /// <paramref name="maxPages"/> before all pages were consumed.
    /// </summary>
    private static async Task<(List<PullRequestSummary> Prs, bool WasTruncated)> FetchAgentDonePullRequestsAsync(
        IRepositoryProvider repoProvider, int maxPages, CancellationToken ct)
    {
        // Fetch all open PRs (no label filter — agent:done is on the issue, not the PR).
        var (all, wasTruncated) = await FetchAllPagesWithTruncationAsync<PullRequestSummary>(
            (page, pageSize, token) =>
                repoProvider.ListOpenPullRequestsAsync(page, pageSize, null, token),
            maxPages, ct);

        // Filter to agent-created PRs by branch prefix.
        all.RemoveAll(pr => !pr.BranchName.StartsWith(PipelineConstants.BranchPrefix, StringComparison.Ordinal));
        return (all, wasTruncated);
    }

    /// <summary>
    /// Polls the decomposition queue for a template (only when DecompositionEnabled).
    /// Wrapped in its own try-catch so that a decomposition failure does not discard issue/PR queues.
    /// </summary>
    private async Task PollDecompositionQueueAsync(
        PipelineJobTemplate template,
        int maxPagesToFetch,
        Dictionary<string, List<EpicCandidate>> decompositionQueues,
        CancellationToken ct)
    {
        decompositionQueues[template.Id] = new List<EpicCandidate>();
        if (!template.DecompositionEnabled) return;

        try
        {
            if (!_cacheManager.IssueProviders.TryGetValue(template.IssueProviderId, out var decompProvider))
            {
                _logger.Warning("Template '{TemplateName}': issue provider '{IssueProviderId}' not found in cache, skipping decomposition polling",
                    template.Name, template.IssueProviderId);
                return;
            }

            // Validate that RepoProviderId references an existing provider config (Req 1.3)
            // IssueProviderId is already validated by the provider cache lookup above.
            if (!_cacheManager.RepoProviders.ContainsKey(template.RepoProviderId))
            {
                _logger.Warning("Template '{TemplateName}': decomposition skipped — RepoProviderId '{RepoProviderId}' references non-existent provider config",
                    template.Name, template.RepoProviderId);
                return;
            }

            // Poll for agent:epic issues (Phase 1 candidates)
            var epicIssues = await FetchEpicIssuesAsync(decompProvider, AgentLabels.Epic, maxPagesToFetch, ct);
            foreach (var epic in epicIssues)
                decompositionQueues[template.Id].Add(new EpicCandidate(epic, PipelineRunType.DecompositionAnalysis, template.IssueProviderId));

            // Poll for agent:epic-approved issues (Phase 2 candidates)
            var approvedIssues = await FetchEpicIssuesAsync(decompProvider, AgentLabels.EpicApproved, maxPagesToFetch, ct);
            foreach (var approved in approvedIssues)
                decompositionQueues[template.Id].Add(new EpicCandidate(approved, PipelineRunType.Decomposition, template.IssueProviderId));

            // Every decomposition queue is oldest first, across both phases
            decompositionQueues[template.Id].SortByCreatedAtFifo();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Template '{TemplateName}' decomposition polling failed, issue/PR polling unaffected: {Error}",
                template.Name, ex.Message);
        }
    }

    /// <summary>Handles a rate-limit exception: updates status, clears queues.</summary>
    private void HandleRateLimitException(
        PipelineJobTemplate template,
        RateLimitExceededException ex,
        ConcurrentDictionary<string, ConfigStatusSnapshot> templateStatuses,
        PollQueues queues)
    {
        _logger.Warning(ex, "Template '{TemplateName}' rate limited until {ResetAt}", template.Name, ex.ResetAt);
        var prevStatus = templateStatuses.TryGetValue(template.Id, out var s) ? s : ConfigStatusSnapshot.Empty;
        templateStatuses[template.Id] = prevStatus with
        {
            LastPollTime = DateTimeOffset.UtcNow,
            RateLimitResetAt = ex.ResetAt,
            IsCurrentlyPolling = false
        };
        queues.ClearForTemplate(template.Id);
    }

    /// <summary>Handles an auth error exception: evicts cached provider, updates status, clears queues.</summary>
    private async Task HandleAuthErrorExceptionAsync(
        PipelineJobTemplate template,
        Exception ex,
        ConcurrentDictionary<string, ConfigStatusSnapshot> templateStatuses,
        PollQueues queues)
    {
        _logger.Warning(ex, "Template '{TemplateName}' auth error, evicting cached provider", template.Name);
        await _cacheManager.EvictOnAuthErrorAsync(template.IssueProviderId);
        var prevStatus = templateStatuses.TryGetValue(template.Id, out var s) ? s : ConfigStatusSnapshot.Empty;
        templateStatuses[template.Id] = prevStatus with
        {
            LastPollTime = DateTimeOffset.UtcNow,
            LastError = ex.Message,
            ConsecutiveFailures = prevStatus.ConsecutiveFailures + 1,
            IsCurrentlyPolling = false
        };
        PipelineTelemetry.LoopBackoffEvents.Add(1);
        queues.ClearForTemplate(template.Id);
    }

    /// <summary>Handles a generic poll exception: updates failure status, clears queues.</summary>
    private void HandleGenericPollException(
        PipelineJobTemplate template,
        Exception ex,
        ConcurrentDictionary<string, ConfigStatusSnapshot> templateStatuses,
        PollQueues queues)
    {
        _logger.Warning(ex, "Template '{TemplateName}' poll failed: {Error}", template.Name, ex.Message);
        var prevStatus = templateStatuses.TryGetValue(template.Id, out var s) ? s : ConfigStatusSnapshot.Empty;
        templateStatuses[template.Id] = prevStatus with
        {
            LastPollTime = DateTimeOffset.UtcNow,
            LastError = ex.Message,
            ConsecutiveFailures = prevStatus.ConsecutiveFailures + 1,
            IsCurrentlyPolling = false
        };
        PipelineTelemetry.LoopBackoffEvents.Add(1);
        queues.ClearForTemplate(template.Id);
    }

    /// <summary>
    /// Adds project epics to the decomposition queues. For each enabled project with an
    /// <see cref="PipelineProject.EpicIssueProviderId"/>, polls that tracker and appends its epics to the
    /// queue of the project's executor template (the first decomposition-enabled template), so project
    /// epics and repo epics share one round-robin. Each candidate carries the epic tracker, which the run
    /// is bound to. Must run after <see cref="PollTemplateQueuesAsync"/>, which creates the queues.
    /// Projects are visited in name order; when two projects share an epic tracker, the first one owns it.
    /// </summary>
    internal async Task AddProjectEpicsAsync(
        IReadOnlyList<PipelineProject> projects,
        IReadOnlyDictionary<string, PipelineJobTemplate> templateLookup,
        int maxPagesToFetch,
        Dictionary<string, List<EpicCandidate>> decompositionQueues,
        CancellationToken ct)
    {
        var claimedEpicTrackers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in projects
                     .Where(p => p.Enabled && !string.IsNullOrEmpty(p.EpicIssueProviderId))
                     .OrderBy(p => p.Name, StringComparer.Ordinal)
                     .ThenBy(p => p.Id, StringComparer.Ordinal))
        {
            if (ct.IsCancellationRequested) break;

            if (!claimedEpicTrackers.Add(project.EpicIssueProviderId!))
            {
                _logger.Warning("Project '{ProjectName}': epic tracker '{EpicProviderId}' is already the epic tracker of another project, skipping project epic polling",
                    project.Name, project.EpicIssueProviderId);
                continue;
            }

            await AddSingleProjectEpicsAsync(project, templateLookup, maxPagesToFetch, decompositionQueues, ct);
        }
    }

    /// <summary>Polls one project's epic tracker and appends its epics to the executor template's queue.</summary>
    private async Task AddSingleProjectEpicsAsync(
        PipelineProject project,
        IReadOnlyDictionary<string, PipelineJobTemplate> templateLookup,
        int maxPagesToFetch,
        Dictionary<string, List<EpicCandidate>> decompositionQueues,
        CancellationToken ct)
    {
        var epicProviderId = project.EpicIssueProviderId!;

        // Validate that EpicIssueProviderId references an existing provider config in the cache
        if (!_cacheManager.IssueProviders.TryGetValue(epicProviderId, out var epicProvider))
        {
            _logger.Warning("Project '{ProjectName}': EpicIssueProviderId '{EpicProviderId}' not found in provider cache, skipping project epic polling",
                project.Name, epicProviderId);
            return;
        }

        // The first decomposition-enabled template in the project executes the project's epics
        var executor = SelectDecompositionTemplate(project, templateLookup);
        if (executor is null)
        {
            _logger.Warning("Project '{ProjectName}': no decomposition-enabled template found, skipping project epic polling",
                project.Name);
            return;
        }

        // The epic tracker may also be a template's own tracker. Its epics are this project's epics, so
        // drop the copies that template's own poll queued before anything below can fail: if the poll
        // fails, or the executor is not polled, the epics wait a cycle instead of running under that template.
        foreach (var (templateId, queue) in decompositionQueues)
        {
            if (queue.RemoveAll(c => c.IssueProviderId == epicProviderId) > 0 && !project.TemplateIds.Contains(templateId))
            {
                _logger.Warning("Project '{ProjectName}': template '{TemplateId}' of another project uses the epic tracker '{EpicProviderId}' as its own tracker; its epics are handled as this project's epics",
                    project.Name, templateId, epicProviderId);
            }
        }

        // The scheduler only dispatches the queues of templates polled this cycle; while the executor is
        // not polled (for example rate-limited), the project's epics wait with it.
        if (!decompositionQueues.TryGetValue(executor.Id, out var executorQueue))
        {
            _logger.Information("Project '{ProjectName}': executor template '{TemplateName}' was not polled this cycle, project epics wait",
                project.Name, executor.Name);
            return;
        }

        try
        {
            var projectEpics = new List<EpicCandidate>();

            // Poll for agent:epic issues (Phase 1 candidates)
            var epicIssues = await FetchEpicIssuesAsync(epicProvider, AgentLabels.Epic, maxPagesToFetch, ct);
            foreach (var epic in epicIssues)
                projectEpics.Add(new EpicCandidate(epic, PipelineRunType.DecompositionAnalysis, epicProviderId));

            // Poll for agent:epic-approved issues (Phase 2 candidates)
            var approvedIssues = await FetchEpicIssuesAsync(epicProvider, AgentLabels.EpicApproved, maxPagesToFetch, ct);
            foreach (var approved in approvedIssues)
                projectEpics.Add(new EpicCandidate(approved, PipelineRunType.Decomposition, epicProviderId));

            executorQueue.AddRange(projectEpics);
            executorQueue.SortByCreatedAtFifo();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Project '{ProjectName}' project epic polling failed: {Error}",
                project.Name, ex.Message);
        }
    }

    /// <summary>Fetches agent:next issues from a specific provider (used in multi-template mode).</summary>
    private static async Task<List<IssueSummary>> FetchAgentNextIssuesForProviderAsync(
        IIssueProvider provider, int maxPages, CancellationToken ct)
    {
        var result = await FetchAllPagesAsync<IssueSummary>(
            (page, pageSize, token) => provider.ListOpenIssuesAsync(page, pageSize, new[] { AgentLabels.Next }, token),
            maxPages, ct);

        // FIFO: oldest first
        result.SortByCreatedAtFifo();
        return result;
    }

    /// <summary>
    /// Fetches agent:next pull requests from a repository provider, filters out ineligible PRs,
    /// and orders by CreatedAt ascending (FIFO). PRs without CreatedAt sort last.
    /// </summary>
    private static async Task<List<PullRequestSummary>> FetchAgentNextPullRequestsAsync(
        IRepositoryProvider repoProvider, int maxPages, CancellationToken ct)
    {
        var result = await FetchAllPagesAsync<PullRequestSummary>(
            (page, pageSize, token) => repoProvider.ListOpenPullRequestsAsync(page, pageSize, new[] { AgentLabels.Next }, token),
            maxPages, ct);

        // Filter: skip PRs with terminal/in-progress status labels
        result.RemoveAll(pr =>
            pr.Labels.Contains(AgentLabels.Error) ||
            pr.Labels.Contains(AgentLabels.InProgress) ||
            pr.Labels.Contains(AgentLabels.Done) ||
            pr.Labels.Contains(AgentLabels.Cancelled));

        // FIFO: oldest first, PRs without CreatedAt go last
        result.SortByCreatedAtFifo();
        return result;
    }

    /// <summary>
    /// Fetches epic issues with a specific label from a provider, applies eligibility filters,
    /// and orders by CreatedAt ascending (FIFO). Used for decomposition polling.
    /// </summary>
    private static async Task<List<IssueSummary>> FetchEpicIssuesAsync(
        IIssueProvider provider, string label, int maxPages, CancellationToken ct)
    {
        var result = await FetchAllPagesAsync<IssueSummary>(
            (page, pageSize, token) => provider.ListOpenIssuesAsync(page, pageSize, new[] { label }, token),
            maxPages, ct);

        // Apply eligibility filters based on the label type:
        if (label == AgentLabels.Epic)
        {
            // Phase 1: skip if also has agent:epic-review, agent:in-progress, agent:error, or agent:done
            result.RemoveAll(issue =>
                issue.Labels.Contains(AgentLabels.EpicReview) ||
                issue.Labels.Contains(AgentLabels.InProgress) ||
                issue.Labels.Contains(AgentLabels.Error) ||
                issue.Labels.Contains(AgentLabels.Done));
        }
        else if (label == AgentLabels.EpicApproved)
        {
            // Phase 2: skip if also has agent:in-progress, agent:error, or agent:done
            result.RemoveAll(issue =>
                issue.Labels.Contains(AgentLabels.InProgress) ||
                issue.Labels.Contains(AgentLabels.Error) ||
                issue.Labels.Contains(AgentLabels.Done));
        }

        // FIFO: oldest first
        result.SortByCreatedAtFifo();
        return result;
    }

    /// <summary>Fetches all pages from a paginated API up to maxPages.</summary>
    internal static async Task<List<T>> FetchAllPagesAsync<T>(
        Func<int, int, CancellationToken, Task<PagedResult<T>>> fetchPage,
        int maxPages,
        CancellationToken ct)
    {
        var (result, _) = await FetchAllPagesWithTruncationAsync(fetchPage, maxPages, ct);
        return result;
    }

    /// <summary>
    /// Fetches all pages from a paginated API up to maxPages, and indicates whether the result
    /// was truncated (i.e., stopped due to the page cap while <c>HasMore</c> was still true).
    /// </summary>
    /// <remarks>
    /// Note: when the page cap is hit, items from all fetched pages — including the page that
    /// triggered the cap — are included in the returned list. The <c>WasTruncated=true</c> flag
    /// signals "at least one more page exists that was not fetched", not "the last page is absent".
    /// Callers that use WasTruncated as a safety gate (e.g. StaleBranchCleaner) will correctly
    /// skip processing even though partial data is present.
    /// TODO [WARNING]: This semantic asymmetry (truncated ≠ all items absent) is noted here for
    /// clarity. No action required unless the contract is tightened in future.
    /// </remarks>
    internal static async Task<(List<T> Items, bool WasTruncated)> FetchAllPagesWithTruncationAsync<T>(
        Func<int, int, CancellationToken, Task<PagedResult<T>>> fetchPage,
        int maxPages,
        CancellationToken ct)
    {
        var result = new List<T>();
        int page = 1;
        const int pageSize = PipelineConstants.DefaultPageSize;

        while (true)
        {
            var pagedResult = await fetchPage(page, pageSize, ct);
            result.AddRange(pagedResult.Items);
            if (!pagedResult.HasMore) break;
            if (page >= maxPages) return (result, true); // truncated by cap
            page++;
        }

        return (result, false);
    }

    /// <summary>Determines if an exception is an auth-related error (401/403/credential).</summary>
    internal static bool IsAuthError(Exception ex)
    {
        if (ex is HttpRequestException httpEx)
        {
            var statusCode = httpEx.StatusCode;
            return statusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;
        }
        // Check for common auth-related exception messages
        var msg = ex.Message.ToLowerInvariant();
        return msg.Contains("unauthorized") || msg.Contains("forbidden") || msg.Contains("credential");
    }

    /// <summary>
    /// Clears all four queue dictionaries for a given template. Used in error catch blocks
    /// to ensure a failed template doesn't leave stale partial data in queues.
    /// </summary>
    internal static void ClearQueuesForTemplate(
        TemplateId templateId,
        Dictionary<string, List<IssueSummary>> issueQueues,
        Dictionary<string, List<PullRequestSummary>> prQueues,
        Dictionary<string, List<EpicCandidate>> decompositionQueues,
        Dictionary<string, List<PullRequestSummary>> agentDonePrQueues,
        Dictionary<string, bool> agentDonePrTruncated)
    {
        issueQueues[templateId.Value] = new List<IssueSummary>();
        prQueues[templateId.Value] = new List<PullRequestSummary>();
        decompositionQueues[templateId.Value] = new List<EpicCandidate>();
        agentDonePrQueues[templateId.Value] = new List<PullRequestSummary>();
        agentDonePrTruncated[templateId.Value] = false;
    }

    /// <summary>
    /// Selects the executor of a project's epics (the epics in its epic tracker).
    /// Returns the first decomposition-enabled template in the project (TemplateIds are in TemplateOrder, by name).
    /// Returns null if no decomposition-enabled template exists.
    /// </summary>
    internal static PipelineJobTemplate? SelectDecompositionTemplate(
        PipelineProject project,
        IReadOnlyDictionary<string, PipelineJobTemplate> templateLookup)
    {
        foreach (var templateId in project.TemplateIds)
        {
            if (templateLookup.TryGetValue(templateId, out var template)
                && template.Enabled
                && template.DecompositionEnabled)
            {
                return template;
            }
        }

        return null;
    }
}
