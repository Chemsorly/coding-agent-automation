using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services;

internal sealed partial class DispatchScheduler
{
    /// <summary>
    /// Dispatches one round of PR reviews (one per template). Filters by ReviewEnabled,
    /// dequeues candidates filtering by labels and duplicates, then dispatches.
    /// </summary>
    private async Task<(bool madeProgress, int consumed, int processed, int failed)> DispatchPrRoundAsync(
        RoundDispatchContext ctx,
        Dictionary<string, List<PullRequestSummary>> prQueues,
        CancellationToken stoppingToken,
        CancellationToken ct)
    {
        return await DispatchRoundAsync(ctx.PollableTemplates, async (template, stopToken) =>
        {
            if (!template.ReviewEnabled) return DispatchAttemptResult.Skip;
            if (!prQueues.TryGetValue(template.Id, out var queue) || queue.Count == 0)
                return DispatchAttemptResult.Skip;

            var pr = TryDequeueValidPr(queue, template, ctx);
            if (pr is null) return DispatchAttemptResult.Skip;

            ctx.TrackingReportIssue(pr.Identifier);
            ctx.ReportStatus($"🔄 Dispatching PR #{pr.Identifier} review from '{template.Name}'");
            ctx.NotifyChange();

            var reviewProject = ctx.TemplateProjectLookup.GetValueOrDefault(template.Id);
            var dispatchOutcome = await DispatchViaOrchestrationAsync(
                async ct =>
                {
                    var reviewDispatchReq = new ReviewDispatchRequest
                    {
                        PrIdentifier = pr.Identifier,
                        PrBranchName = pr.BranchName,
                        PrTitle = pr.Title,
                        PrDescription = pr.Description,
                        PrAuthor = pr.Author,
                        PrUrl = pr.Url,
                        PrTargetBranch = pr.TargetBranch,
                        IssueProviderId = template.IssueProviderId,
                        RepoProviderId = template.RepoProviderId,
                        BrainProviderId = template.BrainProviderId,
                        InitiatedBy = InitiatedByConstants.LoopReview
                    };
                    // TODO: Add a test where templateProjectLookup is missing an entry for a pollable template
                    // to guard against regression (KeyNotFoundException) and validate the fallback behavior.
                    return await _dispatchOrchestration.PrepareReviewDistributionRequestAsync(
                        reviewDispatchReq,
                        reviewProject ?? new PipelineProject { Id = "", Name = UnknownProjectName },
                        ct);
                },
                stopToken);

            if (dispatchOutcome == DispatchAttemptOutcome.AlreadyQueued)
            {
                // 409 — live WorkItem already exists. Do not count as dispatched, do not consume budget.
                // TODO [WARNING]: ActiveIssueIdentifiers is not updated here, so if a second template
                // in the same cycle also queues this PR, it will reach PrepareReviewDistributionRequestAsync
                // and call the API again, receiving a second 409. This is safe (handled correctly) but
                // results in an extra prepare+distribute round-trip per duplicate per cycle. Consider
                // adding the identifier to ctx.ActiveIssueIdentifiers on AlreadyQueued to short-circuit
                // the redundant API call in the second template's turn.
                PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision",
                    PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                return DispatchAttemptResult.Skip;
            }

            var dispatched = dispatchOutcome == DispatchAttemptOutcome.Dispatched;

            if (dispatched)
            {
                _logger.Information("Dispatched PR #{PrIdentifier} review from template '{Template}'",
                    pr.Identifier, template.Name);
                // Add to the in-cycle active set so a second template queuing the same PR
                // in this cycle sees it as already active and skips it.
                // PR work items are keyed by RepoProviderId (not IssueProviderId), consistent
                // with how IsIssueAlreadyActive checks PRs.
                ctx.ActiveIssueIdentifiers.Add((pr.Identifier, template.RepoProviderId));
            }

            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision",
                dispatched ? PipelineTelemetry.LoopDecisions.Dispatched : PipelineTelemetry.LoopDecisions.SkippedNoAgent));

            return new DispatchAttemptResult(dispatched);
        }, ctx.RemainingBudget, ctx.GetCurrentIssueIdentifier, stoppingToken, ct);
    }

    /// <summary>
    /// Dequeues the next valid PR candidate from <paramref name="queue"/>, skipping items
    /// filtered by label or already-processing deduplication.
    /// Returns null if no valid candidate remains.
    /// </summary>
    private PullRequestSummary? TryDequeueValidPr(
        List<PullRequestSummary> queue,
        PipelineJobTemplate template,
        RoundDispatchContext ctx)
    {
        while (queue.Count > 0)
        {
            var candidate = queue[0];
            queue.RemoveAt(0);

            if (candidate.Labels.Contains(AgentLabels.Error) ||
                candidate.Labels.Contains(AgentLabels.InProgress) ||
                candidate.Labels.Contains(AgentLabels.Done) ||
                candidate.Labels.Contains(AgentLabels.Cancelled))
            {
                PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedFilteredByLabel));
                continue;
            }
            // A pull request is identified by its repository and number: its review work item is keyed by
            // the repository, so issue #N in the tracker does not block pull request !N.
            if (IsIssueAlreadyActive(candidate.Identifier, template.RepoProviderId, ctx))
            {
                PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                continue;
            }

            return candidate;
        }
        return null;
    }
}
