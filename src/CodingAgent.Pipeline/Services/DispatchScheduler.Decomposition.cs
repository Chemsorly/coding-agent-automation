using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services;

internal sealed partial class DispatchScheduler
{
    /// <summary>
    /// Dispatches one round of decomposition (one per template). Filters by DecompositionEnabled,
    /// checks concurrency limit, dequeues candidates with duplicate checks, then dispatches.
    /// A template's queue holds its own repo epics and, for a project's executor template, the
    /// project epics; each candidate carries the tracker its epic lives in, and the run is bound to it.
    /// Returns additional decomposition dispatch count for coordinator tracking.
    /// </summary>
    private async Task<(bool madeProgress, int consumed, int processed, int failed, int additionalDecompDispatches)> DispatchDecompositionRoundAsync(
        RoundDispatchContext ctx,
        Dictionary<string, List<EpicCandidate>> decompositionQueues,
        PipelineConfiguration config,
        int activeDecompositionCount,
        CancellationToken stoppingToken,
        CancellationToken ct)
    {
        int additionalDecompDispatches = 0;

        var (madeProgress, consumed, processed, failed) = await DispatchRoundAsync(ctx.PollableTemplates, async (template, stopToken) =>
        {
            if (!template.DecompositionEnabled) return DispatchAttemptResult.Skip;
            if (!decompositionQueues.TryGetValue(template.Id, out var queue) || queue.Count == 0)
                return DispatchAttemptResult.Skip;

            // Re-check concurrency limit before each dispatch.
            // TODO: [WARNING] additionalDecompDispatches is a closure variable mutated inside this lambda and
            // read here for the concurrency guard. DispatchRoundAsync invokes the lambda sequentially, so
            // this is safe today. If DispatchRoundAsync is ever changed to invoke delegates concurrently,
            // this unsynchronised read/write becomes a race condition. Consider passing a ref-counted guard
            // or using an interlocked counter if concurrent dispatch is introduced.
            if (activeDecompositionCount + additionalDecompDispatches >= config.MaxConcurrentDecompositions)
            {
                _logger.Information("Decomposition concurrency limit reached ({Active}/{Max}), skipping remaining decomposition dispatch",
                    activeDecompositionCount + additionalDecompDispatches, config.MaxConcurrentDecompositions);
                return DispatchAttemptResult.Abort;
            }

            var epic = TryDequeueValidEpic(queue, ctx);
            if (epic is null) return DispatchAttemptResult.Skip;

            var (result, dispatched) = await DispatchDecompositionCandidateAsync(epic.Value, template, ctx, stopToken);
            if (dispatched) additionalDecompDispatches++;
            return result;
        }, ctx.RemainingBudget, ctx.GetCurrentIssueIdentifier, stoppingToken, ct);

        return (madeProgress, consumed, processed, failed, additionalDecompDispatches);
    }

    /// <summary>
    /// Prepares and dispatches a single epic candidate. Returns the dispatch attempt result and
    /// whether a new WorkItem was actually dispatched (vs skipped due to 409 or no agent).
    /// </summary>
    private async Task<(DispatchAttemptResult result, bool dispatched)> DispatchDecompositionCandidateAsync(
        EpicCandidate epicItem,
        PipelineJobTemplate template,
        RoundDispatchContext ctx,
        CancellationToken stopToken)
    {
        var phaseLabel = epicItem.Phase == PipelineRunType.DecompositionAnalysis ? "analysis" : "decomposition";

        ctx.TrackingReportIssue(epicItem.Issue.Identifier);
        ctx.ReportStatus($"🧩 Dispatching epic #{epicItem.Issue.Identifier} {phaseLabel} from '{template.Name}'");
        ctx.NotifyChange();

        var decompProject = ctx.TemplateProjectLookup.GetValueOrDefault(template.Id);
        var dispatchOutcome = await DispatchViaOrchestrationAsync(
            async ct => await _dispatchOrchestration.PrepareDecompositionDistributionRequestAsync(
                new DecompositionDispatchOrchestrationRequest
                {
                    EpicIdentifier = epicItem.Issue.Identifier,
                    EpicTitle = epicItem.Issue.Title ?? "",
                    PhaseType = epicItem.Phase,
                    IssueProviderId = epicItem.IssueProviderId,
                    RepoProviderId = template.RepoProviderId,
                    BrainProviderId = template.BrainProviderId,
                    InitiatedBy = InitiatedByConstants.LoopDecomposition,
                    // TODO: Add a test where templateProjectLookup is missing an entry for a pollable template
                    // to guard against regression and validate fallback PipelineProject behavior downstream.
                    Project = decompProject ?? new PipelineProject { Id = "", Name = UnknownProjectName }
                },
                ct),
            stopToken);

        if (dispatchOutcome == DispatchAttemptOutcome.AlreadyQueued)
        {
            // 409 — live WorkItem already exists. Do not count as dispatched, do not consume budget.
            // TODO [WARNING]: ActiveIssueIdentifiers is not updated here, so if a second template
            // in the same cycle also queues this epic, it will reach PrepareDecompositionDistributionRequestAsync
            // and call the API again, receiving a second 409. This is safe (handled correctly) but
            // results in an extra prepare+distribute round-trip per duplicate per cycle. Consider
            // adding the identifier to ctx.ActiveIssueIdentifiers on AlreadyQueued to short-circuit
            // the redundant API call in the second template's turn.
            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision",
                PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
            return (DispatchAttemptResult.Skip, false);
        }

        var dispatched = dispatchOutcome == DispatchAttemptOutcome.Dispatched;

        if (dispatched)
        {
            _logger.Information("Dispatched epic #{EpicIdentifier} in tracker {IssueProviderId} ({Phase}) from template '{Template}'",
                epicItem.Issue.Identifier, epicItem.IssueProviderId, epicItem.Phase, template.Name);
            // Add to the in-cycle active set so a second template queuing the same epic
            // in this cycle sees it as already active and skips it.
            ctx.ActiveIssueIdentifiers.Add((epicItem.Issue.Identifier, epicItem.IssueProviderId));
        }

        PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision",
            dispatched ? PipelineTelemetry.LoopDecisions.Dispatched : PipelineTelemetry.LoopDecisions.SkippedNoAgent));

        return (new DispatchAttemptResult(dispatched), dispatched);
    }

    /// <summary>
    /// Dequeues the next valid epic candidate from <paramref name="queue"/>, skipping items
    /// that are already being processed or are in the active identifiers set.
    /// Returns null if no valid candidate remains.
    /// </summary>
    private EpicCandidate? TryDequeueValidEpic(
        List<EpicCandidate> queue,
        RoundDispatchContext ctx)
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
