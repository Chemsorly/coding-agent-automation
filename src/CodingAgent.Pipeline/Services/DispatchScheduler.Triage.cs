using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services;

internal sealed partial class DispatchScheduler
{
    /// <summary>
    /// Dispatches one round of triage (one per template). Triage shares the Decomposition priority tier but
    /// has no concurrency cap of its own: a triage run is a single investigation, and the cycle budget
    /// still bounds it. Each candidate carries the tracker its issue lives in, and the run is bound to it.
    /// </summary>
    private Task<(bool madeProgress, int consumed, int processed, int failed)> DispatchTriageRoundAsync(
        RoundDispatchContext ctx,
        Dictionary<string, List<TriageCandidate>> triageQueues,
        CancellationToken stoppingToken,
        CancellationToken ct) =>
        DispatchRoundAsync(ctx.PollableTemplates, async (template, stopToken) =>
        {
            if (!template.TriageEnabled) return DispatchAttemptResult.Skip;
            if (!triageQueues.TryGetValue(template.Id, out var queue) || queue.Count == 0)
                return DispatchAttemptResult.Skip;

            var candidate = TryDequeueValidTriage(queue, ctx);
            if (candidate is null) return DispatchAttemptResult.Skip;

            return await DispatchTriageCandidateAsync(candidate.Value, template, ctx, stopToken);
        }, ctx.RemainingBudget, ctx.GetCurrentIssueIdentifier, stoppingToken, ct);

    private async Task<DispatchAttemptResult> DispatchTriageCandidateAsync(
        TriageCandidate candidate,
        PipelineJobTemplate template,
        RoundDispatchContext ctx,
        CancellationToken stopToken)
    {
        ctx.TrackingReportIssue(candidate.Issue.Identifier);
        ctx.ReportStatus($"🔎 Dispatching triage of #{candidate.Issue.Identifier} from '{template.Name}'");
        ctx.NotifyChange();

        var project = ctx.TemplateProjectLookup.GetValueOrDefault(template.Id);
        var dispatchOutcome = await DispatchViaOrchestrationAsync(
            async ct => await _dispatchOrchestration.PrepareTriageDistributionRequestAsync(
                new TriageDispatchOrchestrationRequest
                {
                    IssueIdentifier = candidate.Issue.Identifier,
                    IssueProviderId = candidate.IssueProviderId,
                    RepoProviderId = template.RepoProviderId,
                    BrainProviderId = template.BrainProviderId,
                    InitiatedBy = InitiatedByConstants.LoopTriage,
                    Project = project ?? new PipelineProject { Id = "", Name = UnknownProjectName }
                },
                ct),
            stopToken);

        if (dispatchOutcome == DispatchAttemptOutcome.Dispatched)
            _logger.Information("Dispatched triage of #{IssueIdentifier} in tracker {IssueProviderId} from template '{Template}'",
                candidate.Issue.Identifier, candidate.IssueProviderId, template.Name);

        return FinalizeDispatchOutcome(dispatchOutcome, candidate.Issue.Identifier, candidate.IssueProviderId, ctx);
    }

    /// <summary>
    /// Dequeues the next triage candidate that is not already being processed. Returns null when none remains.
    /// </summary>
    private TriageCandidate? TryDequeueValidTriage(List<TriageCandidate> queue, RoundDispatchContext ctx)
    {
        while (queue.Count > 0)
        {
            var candidate = queue[0];
            queue.RemoveAt(0);

            if (IsIssueAlreadyActive(candidate.Issue.Identifier, candidate.IssueProviderId, ctx))
            {
                PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                continue;
            }

            return candidate;
        }
        return null;
    }
}
