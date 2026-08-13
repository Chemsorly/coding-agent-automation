using System.Collections.Concurrent;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services;

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
    private readonly IAutoUpdatePrBranchService? _autoUpdateService;

    internal TemplatePoller(ProviderCacheManager cacheManager, Serilog.ILogger logger,
        IAutoUpdatePrBranchService? autoUpdateService = null)
    {
        _cacheManager = cacheManager;
        _logger = logger;
        _autoUpdateService = autoUpdateService;
    }

    /// <summary>
    /// Polls once per pollable template for issues, PRs, decomposition candidates, and agent:done PRs.
    /// </summary>
    internal async Task<(Dictionary<string, List<IssueSummary>> IssueQueues,
                          Dictionary<string, List<PullRequestSummary>> PrQueues,
                          Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>> DecompositionQueues,
                          Dictionary<string, List<PullRequestSummary>> AgentDonePrQueues)>
        PollTemplateQueuesAsync(
            IReadOnlyList<PipelineJobTemplate> pollableTemplates,
            int maxPagesToFetch,
            ConcurrentDictionary<string, ConfigStatusSnapshot> templateStatuses,
            Action<int> reportTemplateIndex,
            Action<string> reportStatus,
            Action notifyChange,
            CancellationToken ct)
    {
        var issueQueues = new Dictionary<string, List<IssueSummary>>();
        var prQueues = new Dictionary<string, List<PullRequestSummary>>();
        var decompositionQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>();
        var agentDonePrQueues = new Dictionary<string, List<PullRequestSummary>>();

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
                await PollSingleTemplateAsync(template, maxPagesToFetch, templateStatuses, issueQueues, prQueues, decompositionQueues, agentDonePrQueues, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (RateLimitExceededException ex)
            {
                HandleRateLimitException(template, ex, templateStatuses, issueQueues, prQueues, decompositionQueues, agentDonePrQueues);
            }
            catch (Exception ex) when (IsAuthError(ex))
            {
                await HandleAuthErrorExceptionAsync(template, ex, templateStatuses, issueQueues, prQueues, decompositionQueues, agentDonePrQueues);
            }
            catch (Exception ex)
            {
                HandleGenericPollException(template, ex, templateStatuses, issueQueues, prQueues, decompositionQueues, agentDonePrQueues);
            }
        }

        return (issueQueues, prQueues, decompositionQueues, agentDonePrQueues);
    }

    /// <summary>
    /// Polls issues, PRs, decomposition candidates, and agent:done PRs for a single template,
    /// then updates the success status.
    /// </summary>
    private async Task PollSingleTemplateAsync(
        PipelineJobTemplate template,
        int maxPagesToFetch,
        ConcurrentDictionary<string, ConfigStatusSnapshot> templateStatuses,
        Dictionary<string, List<IssueSummary>> issueQueues,
        Dictionary<string, List<PullRequestSummary>> prQueues,
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>> decompositionQueues,
        Dictionary<string, List<PullRequestSummary>> agentDonePrQueues,
        CancellationToken ct)
    {
        await PollIssueQueueAsync(template, maxPagesToFetch, templateStatuses, issueQueues, ct);
        await PollPrQueueAsync(template, maxPagesToFetch, prQueues, ct);
        await PollDecompositionQueueAsync(template, maxPagesToFetch, decompositionQueues, ct);
        await PollAgentDonePrQueueAsync(template, maxPagesToFetch, agentDonePrQueues, ct);

        // Success — update status (agentDonePrQueues not counted as dispatchable work)
        var issueCount = issueQueues[template.Id].Count;
        var prCount = prQueues[template.Id].Count;
        var decompCount = decompositionQueues[template.Id].Count;
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
    /// </summary>
    private async Task PollPrQueueAsync(
        PipelineJobTemplate template,
        int maxPagesToFetch,
        Dictionary<string, List<PullRequestSummary>> prQueues,
        CancellationToken ct)
    {
        prQueues[template.Id] = new List<PullRequestSummary>();
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
            prQueues[template.Id] = prs;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Template '{TemplateName}' PR polling failed, issue polling unaffected: {Error}",
                template.Name, ex.Message);
        }
    }

    /// <summary>
    /// Polls the PR queue for agent:done PRs (only when AutoUpdatePrBranches is enabled).
    /// Used by the auto-branch-updater (spec 040). Gated on AutoUpdatePrBranches only —
    /// NOT on ReviewEnabled, as auto-update is an independent capability.
    /// Wrapped in its own try-catch to not affect issue/PR/decomposition queues on failure.
    /// </summary>
    private async Task PollAgentDonePrQueueAsync(
        PipelineJobTemplate template,
        int maxPagesToFetch,
        Dictionary<string, List<PullRequestSummary>> agentDonePrQueues,
        CancellationToken ct)
    {
        agentDonePrQueues[template.Id] = new List<PullRequestSummary>();
        if (!template.AutoUpdatePrBranches) return;

        try
        {
            if (!_cacheManager.RepoProviders.TryGetValue(template.RepoProviderId, out var repoProvider))
            {
                _logger.Warning("Template '{TemplateName}': repo provider not found, skipping agent:done PR polling",
                    template.Name);
                return;
            }

            if (!repoProvider.SupportsServerSideBranchUpdate) return;

            agentDonePrQueues[template.Id] = await FetchAgentDonePullRequestsAsync(repoProvider, maxPagesToFetch, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Template '{TemplateName}' agent:done PR polling failed: {Error}",
                template.Name, ex.Message);
        }
    }

    /// <summary>
    /// Fetches open agent:done-labelled PRs from a repository provider.
    /// No RemoveAll filter needed — API label filter returns only agent:done PRs.
    /// IsDraft and active-run exclusion are applied in AutoUpdatePrBranchService.ExecuteAsync.
    /// </summary>
    private static async Task<List<PullRequestSummary>> FetchAgentDonePullRequestsAsync(
        IRepositoryProvider repoProvider, int maxPages, CancellationToken ct)
    {
        return await FetchAllPagesAsync<PullRequestSummary>(
            (page, pageSize, token) =>
                repoProvider.ListOpenPullRequestsAsync(page, pageSize, new[] { AgentLabels.Done }, token),
            maxPages, ct);
    }

    /// <summary>
    /// Polls the decomposition queue for a template (only when DecompositionEnabled).
    /// Wrapped in its own try-catch so that a decomposition failure does not discard issue/PR queues.
    /// </summary>
    private async Task PollDecompositionQueueAsync(
        PipelineJobTemplate template,
        int maxPagesToFetch,
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>> decompositionQueues,
        CancellationToken ct)
    {
        decompositionQueues[template.Id] = new List<(IssueSummary, PipelineRunType)>();
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
                decompositionQueues[template.Id].Add((epic, PipelineRunType.DecompositionAnalysis));

            // Poll for agent:epic-approved issues (Phase 2 candidates)
            var approvedIssues = await FetchEpicIssuesAsync(decompProvider, AgentLabels.EpicApproved, maxPagesToFetch, ct);
            foreach (var approved in approvedIssues)
                decompositionQueues[template.Id].Add((approved, PipelineRunType.Decomposition));
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
        Dictionary<string, List<IssueSummary>> issueQueues,
        Dictionary<string, List<PullRequestSummary>> prQueues,
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>> decompositionQueues,
        Dictionary<string, List<PullRequestSummary>> agentDonePrQueues)
    {
        _logger.Warning(ex, "Template '{TemplateName}' rate limited until {ResetAt}", template.Name, ex.ResetAt);
        var prevStatus = templateStatuses.TryGetValue(template.Id, out var s) ? s : ConfigStatusSnapshot.Empty;
        templateStatuses[template.Id] = prevStatus with
        {
            LastPollTime = DateTimeOffset.UtcNow,
            RateLimitResetAt = ex.ResetAt,
            IsCurrentlyPolling = false
        };
        ClearQueuesForTemplate(template.Id, issueQueues, prQueues, decompositionQueues, agentDonePrQueues);
    }

    /// <summary>Handles an auth error exception: evicts cached provider, updates status, clears queues.</summary>
    private async Task HandleAuthErrorExceptionAsync(
        PipelineJobTemplate template,
        Exception ex,
        ConcurrentDictionary<string, ConfigStatusSnapshot> templateStatuses,
        Dictionary<string, List<IssueSummary>> issueQueues,
        Dictionary<string, List<PullRequestSummary>> prQueues,
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>> decompositionQueues,
        Dictionary<string, List<PullRequestSummary>> agentDonePrQueues)
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
        ClearQueuesForTemplate(template.Id, issueQueues, prQueues, decompositionQueues, agentDonePrQueues);
    }

    /// <summary>Handles a generic poll exception: updates failure status, clears queues.</summary>
    private void HandleGenericPollException(
        PipelineJobTemplate template,
        Exception ex,
        ConcurrentDictionary<string, ConfigStatusSnapshot> templateStatuses,
        Dictionary<string, List<IssueSummary>> issueQueues,
        Dictionary<string, List<PullRequestSummary>> prQueues,
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>> decompositionQueues,
        Dictionary<string, List<PullRequestSummary>> agentDonePrQueues)
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
        ClearQueuesForTemplate(template.Id, issueQueues, prQueues, decompositionQueues, agentDonePrQueues);
    }

    /// <summary>
    /// Project-level epic polling — polls EpicIssueProviderId for each enabled project
    /// that has the field set and at least one decomposition-enabled template.
    /// </summary>
    internal async Task<Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>>>
        PollProjectLevelEpicsAsync(
            IReadOnlyList<PipelineProject> projects,
            IReadOnlyDictionary<string, PipelineJobTemplate> templateLookup,
            int maxPagesToFetch,
            CancellationToken ct)
    {
        var projectLevelDecompositionQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>>();

        foreach (var project in projects.Where(p => p.Enabled && !string.IsNullOrEmpty(p.EpicIssueProviderId)))
        {
            if (ct.IsCancellationRequested) break;

            await PollSingleProjectEpicsAsync(project, templateLookup, maxPagesToFetch, projectLevelDecompositionQueues, ct);
        }

        return projectLevelDecompositionQueues;
    }

    /// <summary>Polls epic issues for a single project and adds results to the queue dictionary.</summary>
    private async Task PollSingleProjectEpicsAsync(
        PipelineProject project,
        IReadOnlyDictionary<string, PipelineJobTemplate> templateLookup,
        int maxPagesToFetch,
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>> projectLevelDecompositionQueues,
        CancellationToken ct)
    {
        var epicProviderId = project.EpicIssueProviderId!;

        // Validate that EpicIssueProviderId references an existing provider config in the cache
        if (!_cacheManager.IssueProviders.TryGetValue(epicProviderId, out var epicProvider))
        {
            _logger.Warning("Project '{ProjectName}': EpicIssueProviderId '{EpicProviderId}' not found in provider cache, skipping project-level epic polling",
                project.Name, epicProviderId);
            return;
        }

        // Select the first decomposition-enabled template in the project
        var decompositionTemplate = SelectDecompositionTemplate(project, templateLookup);
        if (decompositionTemplate is null)
        {
            _logger.Warning("Project '{ProjectName}': no decomposition-enabled template found, skipping project-level epic polling",
                project.Name);
            return;
        }

        try
        {
            var projectQueue = new List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>();

            // Poll for agent:epic issues (Phase 1 candidates)
            var epicIssues = await FetchEpicIssuesAsync(epicProvider, AgentLabels.Epic, maxPagesToFetch, ct);
            foreach (var epic in epicIssues)
                projectQueue.Add((epic, PipelineRunType.DecompositionAnalysis, decompositionTemplate));

            // Poll for agent:epic-approved issues (Phase 2 candidates)
            var approvedIssues = await FetchEpicIssuesAsync(epicProvider, AgentLabels.EpicApproved, maxPagesToFetch, ct);
            foreach (var approved in approvedIssues)
                projectQueue.Add((approved, PipelineRunType.Decomposition, decompositionTemplate));

            if (projectQueue.Count > 0)
                projectLevelDecompositionQueues[project.Id] = projectQueue;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Project '{ProjectName}' project-level epic polling failed: {Error}",
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
        var result = new List<T>();
        int page = 1;
        const int pageSize = PipelineConstants.DefaultPageSize;

        while (true)
        {
            var pagedResult = await fetchPage(page, pageSize, ct);
            result.AddRange(pagedResult.Items);
            if (!pagedResult.HasMore) break;
            if (page >= maxPages) break;
            page++;
        }

        return result;
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
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>> decompositionQueues,
        Dictionary<string, List<PullRequestSummary>> agentDonePrQueues)
    {
        issueQueues[templateId.Value] = new List<IssueSummary>();
        prQueues[templateId.Value] = new List<PullRequestSummary>();
        decompositionQueues[templateId.Value] = new List<(IssueSummary, PipelineRunType)>();
        agentDonePrQueues[templateId.Value] = new List<PullRequestSummary>();
    }

    /// <summary>
    /// Selects the repository template for a project-level epic decomposition dispatch.
    /// Returns the first decomposition-enabled template in the project (by TemplateIds position).
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
