using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services;

internal sealed partial class DispatchScheduler
{
    // TODO: [WARNING] _eligibilityEvaluator is declared here in the .Issues.cs partial file but is
    // also referenced by DispatchScheduler.cs and DispatchScheduler.Decomposition.cs. Shared static
    // fields for partial classes should live in the primary DispatchScheduler.cs file to make the
    // dependency visible to maintainers reading those files. If .Issues.cs is ever removed or split,
    // the other partials lose this field without a compile-time signal until references are updated.
    private static readonly DispatchEligibilityEvaluator _eligibilityEvaluator = new();

    // The narrow filter set used by the issue-dispatch path:
    // only Error and NeedsRefinement block dispatch here.
    // BlockedIssuesService uses its own wider NotReadyLabels set.
    private static readonly IReadOnlySet<string> _issueDispatchFilterLabels =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            AgentLabels.Error,
            AgentLabels.NeedsRefinement,
        };

    /// <summary>
    /// Dispatches one round of issues (one per template). Filters by ImplementationEnabled,
    /// dequeues candidates filtering by labels and duplicates, checks dependencies, then dispatches.
    /// </summary>
    private async Task<(bool madeProgress, int consumed, int processed, int failed)> DispatchIssueRoundAsync(
        RoundDispatchContext ctx,
        Dictionary<string, List<IssueSummary>> issueQueues,
        Dictionary<string, Dictionary<int, bool>> cycleStateCaches,
        CancellationToken stoppingToken,
        CancellationToken ct)
    {
        return await DispatchRoundAsync(ctx.PollableTemplates, async (template, stopToken) =>
        {
            if (!template.ImplementationEnabled) return DispatchAttemptResult.Skip;
            if (!issueQueues.TryGetValue(template.Id, out var queue) || queue.Count == 0)
                return DispatchAttemptResult.Skip;

            var issue = await TryDequeueValidIssueAsync(queue, template, ctx, cycleStateCaches, ct);
            if (issue is null) return DispatchAttemptResult.Skip;

            ctx.TrackingReportIssue(issue.Identifier);
            ctx.ReportStatus($"🔄 Dispatching #{issue.Identifier} from '{template.Name}'");
            ctx.NotifyChange();

            var dispatchProject = ctx.TemplateProjectLookup.GetValueOrDefault(template.Id);
            _logger.Information("Dispatching issue {Issue} with project '{ProjectName}' (id={ProjectId}, template={TemplateId})",
                issue.Identifier, dispatchProject?.Name ?? "NULL", dispatchProject?.Id ?? "NULL", template.Id);

            var dispatchOutcome = await DispatchViaOrchestrationAsync(
                async ct => await _dispatchOrchestration.PrepareDistributionRequestAsync(
                    new ImplementationDispatchOrchestrationRequest
                    {
                        IssueIdentifier = issue.Identifier,
                        IssueProviderId = template.IssueProviderId,
                        RepoProviderId = template.RepoProviderId,
                        BrainProviderId = template.BrainProviderId,
                        PipelineProviderId = template.PipelineProviderId,
                        InitiatedBy = InitiatedByConstants.LoopIssue,
                        Project = dispatchProject ?? new PipelineProject { Id = "", Name = UnknownProjectName }
                    },
                    ct),
                stopToken);

            if (dispatchOutcome == DispatchAttemptOutcome.Dispatched)
                _logger.Information("Dispatched issue #{Issue} from template '{Template}'",
                    issue.Identifier, template.Name);

            var result = FinalizeDispatchOutcome(dispatchOutcome, issue.Identifier, template.IssueProviderId, ctx);

            // Emit Loop.Enqueue span only when a WorkItem was actually created — no span on skips.
            if (result.Dispatched)
            {
                using var enqueueActivity = PipelineTelemetry.ActivitySource.StartActivity("Loop.Enqueue");
                enqueueActivity?.SetTag("issue_identifier", issue.Identifier);
                enqueueActivity?.SetTag("template_name", template.Name);
            }

            return result;
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
        Dictionary<string, Dictionary<int, bool>> cycleStateCaches,
        CancellationToken ct)
    {
        while (queue.Count > 0)
        {
            var candidate = queue[0];
            queue.RemoveAt(0);

            var labelResult = _eligibilityEvaluator.EvaluateLabelFilter(
                candidate.Labels ?? Array.Empty<string>(), _issueDispatchFilterLabels);
            if (!labelResult.IsEligible)
            {
                PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedFilteredByLabel));
                continue;
            }

            // TODO: [WARNING] IsIssueAlreadyActive merges two logically distinct checks with different
            // staleness semantics: (1) IsIssueBeingProcessed checks live orchestration state;
            // (2) ActiveIssueIdentifiers checks the in-memory cycle snapshot.
            // Both checks must be preserved — the evaluator receives both results as separate booleans
            // so neither can be silently dropped or short-circuited.
            var activeResult = _eligibilityEvaluator.EvaluateActiveElsewhere(
                isBeingProcessed: _orchestration.IsIssueBeingProcessed(candidate.Identifier, template.IssueProviderId),
                isInActiveSet: ctx.ActiveIssueIdentifiers.Contains((candidate.Identifier, template.IssueProviderId)));
            if (!activeResult.IsEligible)
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

                // Issue numbers are unique only within a tracker, so each tracker keeps its own cache:
                // "#12 is closed" in one tracker says nothing about #12 in another.
                if (!cycleStateCaches.TryGetValue(template.IssueProviderId, out var trackerStateCache))
                    cycleStateCaches[template.IssueProviderId] = trackerStateCache = new Dictionary<int, bool>();

                var depResult = await _eligibilityEvaluator.EvaluateDependencyAsync(
                    candidate.Identifier, candidate.Description, provider, trackerStateCache, _dependencyChecker, ct);
                if (!depResult.IsEligible)
                {
                    // TODO: [WARNING] The structured log property {BlockedBy} is now bound to depResult.Reason
                    // (a prose string like "Blocked by open issue(s): #5, #12") instead of the original
                    // depResult.BlockedBy (IReadOnlyList<int>). Any log consumer, dashboard, or alert that
                    // parses {BlockedBy} as a numeric list will break. Consider renaming the property to
                    // {BlockedByReason} to match the new string semantics, or expose BlockedBy list from
                    // DispatchEligibilityResult so the structured type is preserved.
                    _logger.Information("Issue #{Identifier} blocked by open issues: {BlockedBy}. Skipping dispatch.",
                        candidate.Identifier, depResult.Reason);
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
