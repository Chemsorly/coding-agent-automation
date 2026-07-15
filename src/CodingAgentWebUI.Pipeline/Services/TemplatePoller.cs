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

    internal TemplatePoller(ProviderCacheManager cacheManager, Serilog.ILogger logger)
    {
        _cacheManager = cacheManager;
        _logger = logger;
    }

    /// <summary>
    /// Polls once per pollable template for issues, PRs, and decomposition candidates.
    /// </summary>
    internal async Task<(Dictionary<string, List<IssueSummary>> IssueQueues, Dictionary<string, List<PullRequestSummary>> PrQueues, Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>> DecompositionQueues)>
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
                // ── Issue polling (only when ImplementationEnabled) ──
                if (template.ImplementationEnabled)
                {
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
                    }
                    else
                    {
                        var issues = await FetchAgentNextIssuesForProviderAsync(provider, maxPagesToFetch, ct);
                        issueQueues[template.Id] = issues;
                    }
                }
                else
                {
                    issueQueues[template.Id] = new List<IssueSummary>();
                }

                // ── PR polling (only when ReviewEnabled) ──
                // Wrapped in its own try-catch so that a PR polling failure does not
                // discard the already-fetched issue queue for this template.
                prQueues[template.Id] = new List<PullRequestSummary>();
                if (template.ReviewEnabled)
                {
                    try
                    {
                        if (!_cacheManager.RepoProviders.TryGetValue(template.RepoProviderId, out var repoProvider))
                        {
                            _logger.Warning("Template '{TemplateName}': repo provider '{RepoProviderId}' not found in cache, skipping PR polling",
                                template.Name, template.RepoProviderId);
                        }
                        else
                        {
                            var prs = await FetchAgentNextPullRequestsAsync(repoProvider, maxPagesToFetch, ct);
                            prQueues[template.Id] = prs;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Template '{TemplateName}' PR polling failed, issue polling unaffected: {Error}",
                            template.Name, ex.Message);
                    }
                }

                // ── Decomposition polling (only when DecompositionEnabled) ──
                // Wrapped in its own try-catch so that a decomposition polling failure does not
                // discard the already-fetched issue/PR queues for this template.
                decompositionQueues[template.Id] = new List<(IssueSummary, PipelineRunType)>();
                if (template.DecompositionEnabled)
                {
                    try
                    {
                        if (!_cacheManager.IssueProviders.TryGetValue(template.IssueProviderId, out var decompProvider))
                        {
                            _logger.Warning("Template '{TemplateName}': issue provider '{IssueProviderId}' not found in cache, skipping decomposition polling",
                                template.Name, template.IssueProviderId);
                        }
                        else
                        {
                            // Validate that RepoProviderId references an existing provider config (Req 1.3)
                            // IssueProviderId is already validated by the provider cache lookup above.
                            if (!_cacheManager.RepoProviders.ContainsKey(template.RepoProviderId))
                            {
                                _logger.Warning("Template '{TemplateName}': decomposition skipped — RepoProviderId '{RepoProviderId}' references non-existent provider config",
                                    template.Name, template.RepoProviderId);
                            }
                            else
                            {
                                // Poll for agent:epic issues (Phase 1 candidates)
                                var epicIssues = await FetchEpicIssuesAsync(decompProvider, AgentLabels.Epic, maxPagesToFetch, ct);
                                foreach (var epic in epicIssues)
                                    decompositionQueues[template.Id].Add((epic, PipelineRunType.DecompositionAnalysis));

                                // Poll for agent:epic-approved issues (Phase 2 candidates)
                                var approvedIssues = await FetchEpicIssuesAsync(decompProvider, AgentLabels.EpicApproved, maxPagesToFetch, ct);
                                foreach (var approved in approvedIssues)
                                    decompositionQueues[template.Id].Add((approved, PipelineRunType.Decomposition));
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Template '{TemplateName}' decomposition polling failed, issue/PR polling unaffected: {Error}",
                            template.Name, ex.Message);
                    }
                }

                // Success — update status
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
            catch (OperationCanceledException)
            {
                break;
            }
            catch (RateLimitExceededException ex)
            {
                _logger.Warning("Template '{TemplateName}' rate limited until {ResetAt}", template.Name, ex.ResetAt);
                var prevStatus = templateStatuses.TryGetValue(template.Id, out var s) ? s : ConfigStatusSnapshot.Empty;
                templateStatuses[template.Id] = prevStatus with
                {
                    LastPollTime = DateTimeOffset.UtcNow,
                    RateLimitResetAt = ex.ResetAt,
                    IsCurrentlyPolling = false
                };
                ClearQueuesForTemplate(template.Id, issueQueues, prQueues, decompositionQueues);
            }
            catch (Exception ex) when (IsAuthError(ex))
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
                ClearQueuesForTemplate(template.Id, issueQueues, prQueues, decompositionQueues);
            }
            catch (Exception ex)
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
                ClearQueuesForTemplate(template.Id, issueQueues, prQueues, decompositionQueues);
            }
        }

        return (issueQueues, prQueues, decompositionQueues);
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

            var epicProviderId = project.EpicIssueProviderId!;

            // Validate that EpicIssueProviderId references an existing provider config in the cache
            if (!_cacheManager.IssueProviders.TryGetValue(epicProviderId, out var epicProvider))
            {
                _logger.Warning("Project '{ProjectName}': EpicIssueProviderId '{EpicProviderId}' not found in provider cache, skipping project-level epic polling",
                    project.Name, epicProviderId);
                continue;
            }

            // Select the first decomposition-enabled template in the project
            var decompositionTemplate = SelectDecompositionTemplate(project, templateLookup);
            if (decompositionTemplate is null)
            {
                _logger.Warning("Project '{ProjectName}': no decomposition-enabled template found, skipping project-level epic polling",
                    project.Name);
                continue;
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

        return projectLevelDecompositionQueues;
    }

    /// <summary>Fetches agent:next issues from a specific provider (used in multi-template mode).</summary>
    private async Task<List<IssueSummary>> FetchAgentNextIssuesForProviderAsync(
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
    private async Task<List<PullRequestSummary>> FetchAgentNextPullRequestsAsync(
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
    private async Task<List<IssueSummary>> FetchEpicIssuesAsync(
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
    /// Clears all three queue dictionaries for a given template. Used in error catch blocks
    /// to ensure a failed template doesn't leave stale partial data in queues.
    /// </summary>
    internal static void ClearQueuesForTemplate(
        string templateId,
        Dictionary<string, List<IssueSummary>> issueQueues,
        Dictionary<string, List<PullRequestSummary>> prQueues,
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>> decompositionQueues)
    {
        issueQueues[templateId] = new List<IssueSummary>();
        prQueues[templateId] = new List<PullRequestSummary>();
        decompositionQueues[templateId] = new List<(IssueSummary, PipelineRunType)>();
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
