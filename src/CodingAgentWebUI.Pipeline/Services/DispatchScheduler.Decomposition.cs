using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services;

internal sealed partial class DispatchScheduler
{
    /// <summary>
    /// Dispatches one round of template-based decomposition (one per template). Filters by DecompositionEnabled,
    /// checks concurrency limit, dequeues candidates with duplicate checks, then dispatches.
    /// Returns additional decomposition dispatch count for coordinator tracking.
    /// </summary>
    private async Task<(bool madeProgress, int consumed, int processed, int failed, int additionalDecompDispatches)> DispatchDecompositionRoundAsync(
        RoundDispatchContext ctx,
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>> decompositionQueues,
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

            // Re-check concurrency limit before each dispatch
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

            var epic = TryDequeueValidEpic(queue, template, ctx);
            if (epic is null) return DispatchAttemptResult.Skip;

            var epicItem = epic.Value;
            var phaseLabel = epicItem.Phase == PipelineRunType.DecompositionAnalysis ? "analysis" : "decomposition";

            ctx.TrackingReportIssue(epicItem.Issue.Identifier);
            ctx.ReportStatus($"🧩 Dispatching epic #{epicItem.Issue.Identifier} {phaseLabel} from '{template.Name}'");
            ctx.NotifyChange();

            var decompProject = ctx.TemplateProjectLookup.GetValueOrDefault(template.Id);
            var dispatched = await DispatchViaOrchestrationOrLegacyAsync(
                async ct => await _dispatchOrchestration!.PrepareDecompositionDistributionRequestAsync(
                    new DecompositionDispatchOrchestrationRequest
                    {
                        EpicIdentifier = epicItem.Issue.Identifier,
                        EpicTitle = epicItem.Issue.Title ?? "",
                        PhaseType = epicItem.Phase,
                        IssueProviderId = template.IssueProviderId,
                        RepoProviderId = template.RepoProviderId,
                        BrainProviderId = template.BrainProviderId,
                        InitiatedBy = "loop",
                        // TODO: Add a test where templateProjectLookup is missing an entry for a pollable template
                        // to guard against regression and validate fallback PipelineProject behavior downstream.
                        Project = decompProject ?? new PipelineProject { Id = "", Name = UnknownProjectName }
                    },
                    ct),
                () => JobDistributionRequest.FromTemplate(
                    template, epicItem.Issue, epicItem.Phase, initiatedBy: "loop",
                    projectId: decompProject?.Id, projectName: decompProject?.Name),
                stopToken);

            if (dispatched)
            {
                additionalDecompDispatches++;
                _logger.Information("Dispatched epic #{EpicIdentifier} ({Phase}) from template '{Template}'",
                    epicItem.Issue.Identifier, epicItem.Phase, template.Name);
            }

            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision",
                dispatched ? PipelineTelemetry.LoopDecisions.Dispatched : PipelineTelemetry.LoopDecisions.SkippedNoAgent));

            return new DispatchAttemptResult(dispatched);
        }, ctx.RemainingBudget, ctx.GetCurrentIssueIdentifier, stoppingToken, ct);

        return (madeProgress, consumed, processed, failed, additionalDecompDispatches);
    }

    /// <summary>
    /// Dequeues the next valid epic candidate from <paramref name="queue"/>, skipping items
    /// that are already being processed or are in the active identifiers set.
    /// Returns null if no valid candidate remains.
    /// </summary>
    private (IssueSummary Issue, PipelineRunType Phase)? TryDequeueValidEpic(
        List<(IssueSummary Issue, PipelineRunType Phase)> queue,
        PipelineJobTemplate template,
        RoundDispatchContext ctx)
    {
        while (queue.Count > 0)
        {
            var candidate = queue[0];
            queue.RemoveAt(0);

            if (IsIssueAlreadyActive(candidate.Issue.Identifier, template.IssueProviderId, ctx))
            {
                PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                continue;
            }

            return candidate;
        }
        return null;
    }

    /// <summary>
    /// Dispatches project-level decomposition epics. Iterates project queues with one dispatch
    /// attempt per project per round (fair alternation). Does not use DispatchRoundAsync — manages
    /// its own iteration, try/catch, and counter tracking.
    /// Returns additional decomposition dispatch count for coordinator tracking.
    /// </summary>
    private async Task<(bool madeProgress, int consumed, int processed, int failed, int additionalDecompDispatches)> DispatchProjectLevelDecompositionRoundAsync(
        RoundDispatchContext ctx,
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>> projectLevelDecompositionQueues,
        PipelineConfiguration config,
        int activeDecompositionCount,
        CancellationToken stoppingToken,
        CancellationToken ct)
    {
        bool madeProgress = false;
        int consumed = 0;
        int processed = 0;
        int failed = 0;
        int additionalDecompDispatches = 0;

        foreach (var kvp in projectLevelDecompositionQueues.ToList())
        {
            if (IsProjectLevelDispatchLimitReached(ctx, consumed, activeDecompositionCount, additionalDecompDispatches, config, ct, stoppingToken))
                break;

            // Fair alternation: TryDequeueValidProjectLevelEpic may drain multiple already-processing items
            // before returning the first valid candidate (or null). Combined with the snapshot ToList() above,
            // this guarantees at most one dispatch attempt per project per round — the same invariant as the
            // original inner-while-then-break pattern. Items drained as "already processing" are permanently
            // removed from the snapshot for this cycle, which is consistent with original behaviour.
            // TODO: [WARNING] The comment below ("returns exactly one candidate per call") is misleading —
            // TryDequeueValidProjectLevelEpic may dequeue and discard multiple already-processing items before
            // returning. Update the comment if confusion arises during future changes.
            var candidate = TryDequeueValidProjectLevelEpic(kvp.Value, ctx);
            if (candidate is null) continue;

            var (dispatched, epicFailed) = await TryDispatchProjectLevelCandidateAsync(candidate.Value, ctx, stoppingToken);
            if (dispatched || epicFailed)
            {
                // TODO: [WARNING] Asymmetry with DispatchDecompositionRoundAsync (template-based path): here,
                // consumed is only incremented when dispatched || epicFailed (i.e. a no-agent skip does not
                // increment consumed). DispatchRoundAsync (used by the template path) increments consumed
                // only when result.Dispatched is true, so both paths share this same behaviour for the
                // no-agent case. However, the template path always increments consumed on exception regardless
                // of dispatch outcome — a subtle difference. The remaining-budget guard
                // (ctx.RemainingBudget - consumed <= 0) may allow more iterations than intended when every
                // candidate is skipped by the no-agent path. Pre-existing asymmetry; no regression introduced
                // by this PR.
                consumed++;
                madeProgress = true;
                processed++;
                additionalDecompDispatches += dispatched ? 1 : 0;
                failed += epicFailed ? 1 : 0;
            }
        }

        CleanupEmptyProjectQueues(projectLevelDecompositionQueues);

        return (madeProgress, consumed, processed, failed, additionalDecompDispatches);
    }

    private static bool IsProjectLevelDispatchLimitReached(
        RoundDispatchContext ctx,
        int consumed,
        int activeDecompositionCount,
        int additionalDecompDispatches,
        PipelineConfiguration config,
        CancellationToken ct,
        CancellationToken stoppingToken)
    {
        return ctx.RemainingBudget - consumed <= 0
            || ct.IsCancellationRequested
            || stoppingToken.IsCancellationRequested
            || activeDecompositionCount + additionalDecompDispatches >= config.MaxConcurrentDecompositions;
    }

    private static void CleanupEmptyProjectQueues(
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>> queues)
    {
        foreach (var key in queues.Where(kvp => kvp.Value.Count == 0).Select(kvp => kvp.Key).ToList())
            queues.Remove(key);
    }

    /// <summary>
    /// Reports status, notifies change, and dispatches a single project-level epic candidate.
    /// Returns (dispatched, epicFailed).
    /// </summary>
    private async Task<(bool dispatched, bool epicFailed)> TryDispatchProjectLevelCandidateAsync(
        (IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template) candidate,
        RoundDispatchContext ctx,
        CancellationToken stoppingToken)
    {
        var phaseLabel = candidate.Phase == PipelineRunType.DecompositionAnalysis ? "analysis" : "decomposition";
        ctx.TrackingReportIssue(candidate.Issue.Identifier);
        ctx.ReportStatus($"🧩 Dispatching project-level epic #{candidate.Issue.Identifier} {phaseLabel} from '{candidate.Template.Name}'");
        ctx.NotifyChange();

        return await DispatchProjectLevelEpicAsync(candidate, ctx, stoppingToken);
    }

    /// <summary>
    /// Dequeues the next valid project-level epic candidate from <paramref name="queue"/>, skipping
    /// items that are already being processed or are in the active identifiers set.
    /// Returns null if no valid candidate remains (caller should continue to next project).
    /// </summary>
    private (IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)? TryDequeueValidProjectLevelEpic(
        List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)> queue,
        RoundDispatchContext ctx)
    {
        while (queue.Count > 0)
        {
            var candidate = queue[0];
            queue.RemoveAt(0);

            if (_orchestration.IsIssueBeingProcessed(candidate.Issue.Identifier, candidate.Template.IssueProviderId))
            {
                PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                continue;
            }
            if (ctx.ActiveIssueIdentifiers.Contains((candidate.Issue.Identifier, candidate.Template.IssueProviderId)))
            {
                PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                continue;
            }

            return candidate;
        }
        return null;
    }

    /// <summary>
    /// Dispatches a single project-level epic via orchestration or legacy path.
    /// Returns (dispatched, failed) — both false means dispatch was skipped (no agent).
    /// </summary>
    private async Task<(bool dispatched, bool failed)> DispatchProjectLevelEpicAsync(
        (IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template) candidate,
        RoundDispatchContext ctx,
        CancellationToken stoppingToken)
    {
        try
        {
            var projLevelProject = ctx.TemplateProjectLookup.GetValueOrDefault(candidate.Template.Id);
            var dispatched = await DispatchViaOrchestrationOrLegacyAsync(
                async ct => await _dispatchOrchestration!.PrepareDecompositionDistributionRequestAsync(
                    new DecompositionDispatchOrchestrationRequest
                    {
                        EpicIdentifier = candidate.Issue.Identifier,
                        EpicTitle = candidate.Issue.Title ?? "",
                        PhaseType = candidate.Phase,
                        IssueProviderId = candidate.Template.IssueProviderId,
                        RepoProviderId = candidate.Template.RepoProviderId,
                        BrainProviderId = candidate.Template.BrainProviderId,
                        InitiatedBy = "loop",
                        Project = projLevelProject ?? new PipelineProject { Id = "", Name = UnknownProjectName },
                        DecompositionSource = "project-level"
                    },
                    ct),
                () => JobDistributionRequest.FromTemplate(
                    candidate.Template, candidate.Issue, candidate.Phase,
                    initiatedBy: "loop", decompositionSource: "project-level",
                    projectId: projLevelProject?.Id, projectName: projLevelProject?.Name),
                stoppingToken);

            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision",
                dispatched ? PipelineTelemetry.LoopDecisions.Dispatched : PipelineTelemetry.LoopDecisions.SkippedNoAgent));

            if (dispatched)
            {
                // Consistent with DispatchRoundAsync: successful dispatch counts as processed.
                // TODO: Add a targeted unit test that exercises DispatchFairRoundRobinAsync with a successful
                // project-level decomposition and asserts the returned ProcessedCount includes it.
                _logger.Information("Dispatched project-level epic #{EpicIdentifier} ({Phase}) via template '{Template}'",
                    candidate.Issue.Identifier, candidate.Phase, candidate.Template.Name);
            }

            return (dispatched, false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Returns (false, false) so the caller's foreach tail is a no-op (neither counter incremented).
            // The limitReached guard at the top of the next iteration then breaks the loop.
            return (false, false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Project-level decomposition dispatch failed for epic #{EpicIdentifier}: {Error}",
                candidate.Issue.Identifier, ex.Message);
            // Consistent with DispatchRoundAsync: failures count as both processed and failed.
            // TODO: Add a targeted unit test that triggers a dispatch failure for a project-level
            // decomposition candidate and asserts DispatchResult contains the expected ProcessedCount
            // and FailedCount. This is a behavioral change (previously neither was incremented on failure).
            return (false, true);
        }
    }
}
