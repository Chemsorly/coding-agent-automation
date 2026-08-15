using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services;

internal sealed partial class DispatchScheduler
{
    /// <summary>
    /// Dispatches one round of issues (one per template). Filters by ImplementationEnabled,
    /// dequeues candidates filtering by labels and duplicates, checks dependencies, then dispatches.
    /// </summary>
    private async Task<(bool madeProgress, int consumed, int processed, int failed)> DispatchIssueRoundAsync(
        RoundDispatchContext ctx,
        Dictionary<string, List<IssueSummary>> issueQueues,
        Dictionary<int, bool> cycleStateCache,
        CancellationToken stoppingToken,
        CancellationToken ct)
    {
        return await DispatchRoundAsync(ctx.PollableTemplates, async (template, stopToken) =>
        {
            if (!template.ImplementationEnabled) return DispatchAttemptResult.Skip;
            if (!issueQueues.TryGetValue(template.Id, out var queue) || queue.Count == 0)
                return DispatchAttemptResult.Skip;

            var issue = await TryDequeueValidIssueAsync(queue, template, ctx, cycleStateCache, ct);
            if (issue is null) return DispatchAttemptResult.Skip;

            ctx.TrackingReportIssue(issue.Identifier);
            ctx.ReportStatus($"🔄 Dispatching #{issue.Identifier} from '{template.Name}'");
            ctx.NotifyChange();

            var dispatchProject = ctx.TemplateProjectLookup.GetValueOrDefault(template.Id);
            _logger.Information("Dispatching issue {Issue} with project '{ProjectName}' (id={ProjectId}, template={TemplateId})",
                issue.Identifier, dispatchProject?.Name ?? "NULL", dispatchProject?.Id ?? "NULL", template.Id);

            var dispatched = await DispatchViaOrchestrationOrLegacyAsync(
                async ct => await _dispatchOrchestration!.PrepareDistributionRequestAsync(
                    new ImplementationDispatchOrchestrationRequest
                    {
                        IssueIdentifier = issue.Identifier,
                        IssueProviderId = template.IssueProviderId,
                        RepoProviderId = template.RepoProviderId,
                        BrainProviderId = template.BrainProviderId,
                        PipelineProviderId = template.PipelineProviderId,
                        InitiatedBy = "loop",
                        Project = dispatchProject ?? new PipelineProject { Id = "", Name = UnknownProjectName }
                    },
                    ct),
                () => JobDistributionRequest.FromTemplate(
                    template, issue, initiatedBy: "loop",
                    projectId: dispatchProject?.Id, projectName: dispatchProject?.Name),
                stopToken);

            if (dispatched)
                _logger.Information("Dispatched issue #{Issue} from template '{Template}'",
                    issue.Identifier, template.Name);

            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision",
                dispatched ? PipelineTelemetry.LoopDecisions.Dispatched : PipelineTelemetry.LoopDecisions.SkippedNoAgent));

            return new DispatchAttemptResult(dispatched);
        }, ctx.RemainingBudget, ctx.GetCurrentIssueIdentifier, stoppingToken, ct);
    }

    /// <summary>
    /// Dequeues the next valid issue candidate from <paramref name="queue"/>, skipping items
    /// filtered by label, already-processing deduplication, or dependency blocking.
    /// Returns null if no valid candidate remains.
    /// </summary>
    private async Task<IssueSummary?> TryDequeueValidIssueAsync(
        List<IssueSummary> queue,
        PipelineJobTemplate template,
        RoundDispatchContext ctx,
        Dictionary<int, bool> cycleStateCache,
        CancellationToken ct)
    {
        while (queue.Count > 0)
        {
            var candidate = queue[0];
            queue.RemoveAt(0);

            if (candidate.Labels.Contains(AgentLabels.Error) || candidate.Labels.Contains(AgentLabels.NeedsRefinement))
            {
                PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedFilteredByLabel));
                continue;
            }
            if (IsIssueAlreadyActive(candidate.Identifier, template.IssueProviderId, ctx))
            {
                PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                continue;
            }

            if (_dependencyChecker != null)
            {
                if (!_cacheManager.IssueProviders.TryGetValue(template.IssueProviderId, out var provider))
                {
                    _logger.Warning("Provider '{ProviderId}' not in cache during dependency check for #{Identifier}, skipping dispatch",
                        template.IssueProviderId, candidate.Identifier);
                    continue;
                }

                var depResult = await _dependencyChecker.CheckAsync(
                    candidate.Identifier, candidate.Description, provider, cycleStateCache, ct);
                if (!depResult.IsReady)
                {
                    _logger.Information("Issue #{Identifier} blocked by open issues: {BlockedBy}. Skipping dispatch.",
                        candidate.Identifier, depResult.BlockedBy);
                    PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedDependencyBlocked));
                    continue;
                }
            }

            return candidate;
        }
        return null;
    }

    /// <summary>
    /// Returns <c>true</c> if the issue is currently being processed or present in the active identifiers set.
    /// TODO: [WARNING] This method merges two logically distinct checks with different staleness semantics:
    /// (1) IsIssueBeingProcessed checks live orchestration state; (2) ActiveIssueIdentifiers checks the
    /// in-memory cycle snapshot. Do not remove or short-circuit check (1) — it guards against races that
    /// the snapshot alone cannot detect (e.g., an issue dispatched by another agent instance between polls).
    /// </summary>
    private bool IsIssueAlreadyActive(string identifier, ProviderConfigId issueProviderId, RoundDispatchContext ctx)
    {
        if (_orchestration.IsIssueBeingProcessed(identifier, issueProviderId))
            return true;
        if (ctx.ActiveIssueIdentifiers.Contains((identifier, issueProviderId)))
            return true;
        return false;
    }
}
