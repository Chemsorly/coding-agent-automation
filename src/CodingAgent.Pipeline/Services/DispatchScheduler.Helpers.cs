using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services;

internal sealed partial class DispatchScheduler
{
    /// <summary>
    /// Shared dispatch helper that iterates templates, invokes the dispatch delegate inside
    /// a try/catch, and manages counters and progress tracking.
    /// </summary>
    private async Task<(bool madeProgress, int consumed, int processed, int failed)> DispatchRoundAsync(
        IReadOnlyList<PipelineJobTemplate> pollableTemplates,
        Func<PipelineJobTemplate, CancellationToken, Task<DispatchAttemptResult>> tryDispatchOne,
        int remainingBudget,
        Func<string?> getCurrentIssueIdentifier,
        CancellationToken stoppingToken,
        CancellationToken ct)
    {
        bool madeProgress = false;
        int consumed = 0;
        int processed = 0;
        int failed = 0;

        foreach (var template in pollableTemplates)
        {
            if (remainingBudget - consumed <= 0) break;
            if (ct.IsCancellationRequested) break;

            DispatchAttemptResult result;
            try
            {
                result = await tryDispatchOne(template, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Dispatch failed for {Identifier} from template '{Template}'",
                    getCurrentIssueIdentifier(), template.Name);
                failed++;
                processed++;
                consumed++;
                madeProgress = true;
                continue;
            }

            if (result.AbortRemaining) break;
            if (!result.Attempted) continue;

            if (result.Dispatched)
            {
                processed++;
                consumed++;
                madeProgress = true;
            }
        }

        return (madeProgress, consumed, processed, failed);
    }

    /// <summary>
    /// Shared helper: prepares and dispatches a job distribution request via the orchestration service.
    /// Returns a <see cref="DispatchAttemptOutcome"/> distinguishing a genuine dispatch from a
    /// 409 duplicate-skip (<see cref="DispatchAttemptOutcome.AlreadyQueued"/>) and a failure.
    /// </summary>
    private async Task<DispatchAttemptOutcome> DispatchViaOrchestrationAsync(
        Func<CancellationToken, Task<JobDistributionRequest?>> prepareDbRequest,
        CancellationToken ct)
    {
        var request = await prepareDbRequest(ct);
        if (request is null) return DispatchAttemptOutcome.Failed;
        var outcome = await _dispatchOrchestration.DistributeAndFinalizeAsync(request, ct);
        if (!outcome.Success) return DispatchAttemptOutcome.Failed;
        // TODO [WARNING]: The AlreadyExists check relies on the invariant that AlreadyExists → Success=true
        // (documented in DistributionResult). If a future distributor variant returns Success=false,
        // AlreadyExists=true, the AlreadyExists signal is silently swallowed here under the Failed branch
        // above, causing budget/telemetry miscounting. Consider asserting or enforcing this invariant at
        // the DistributionResult/DispatchOutcome record level.
        if (outcome.AlreadyExists) return DispatchAttemptOutcome.AlreadyQueued;
        return DispatchAttemptOutcome.Dispatched;
    }

    /// <summary>
    /// Shared post-dispatch outcome handler called by all three round partials after
    /// <see cref="DispatchViaOrchestrationAsync"/> returns.
    /// <list type="bullet">
    ///   <item>On <see cref="DispatchAttemptOutcome.AlreadyQueued"/>: emits <c>SkippedAlreadyProcessing</c>
    ///     telemetry and returns <see cref="DispatchAttemptResult.Skip"/> so the caller
    ///     propagates the skip without consuming budget.</item>
    ///   <item>On <see cref="DispatchAttemptOutcome.Dispatched"/>: adds <c>(identifier, providerId)</c>
    ///     to <see cref="RoundDispatchContext.ActiveIssueIdentifiers"/>, emits <c>Dispatched</c>
    ///     telemetry, and returns <c>new DispatchAttemptResult(true)</c>.</item>
    ///   <item>Otherwise (<see cref="DispatchAttemptOutcome.Failed"/>): emits <c>SkippedNoAgent</c>
    ///     telemetry and returns <c>new DispatchAttemptResult(false)</c>.</item>
    /// </list>
    /// The caller is responsible for emitting the success log line (message differs per work type)
    /// before or after calling this method. Work-type-specific spans (e.g. Loop.Enqueue in Issues)
    /// must also remain in the caller.
    /// TODO [WARNING]: On AlreadyQueued, <c>ActiveIssueIdentifiers</c> is not updated here. If a
    /// second template in the same cycle also queues the same item, it will reach the prepare API
    /// again and receive a second 409. This is safe but results in an extra round-trip per
    /// duplicate per cycle. Consider adding the identifier on AlreadyQueued to short-circuit the
    /// redundant call.
    /// </summary>
    private static DispatchAttemptResult FinalizeDispatchOutcome(
        DispatchAttemptOutcome outcome,
        string identifier,
        ProviderConfigId providerId,
        RoundDispatchContext ctx)
    {
        if (outcome == DispatchAttemptOutcome.AlreadyQueued)
        {
            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision,
                PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
            return DispatchAttemptResult.Skip;
        }

        var dispatched = outcome == DispatchAttemptOutcome.Dispatched;

        if (dispatched)
        {
            // Add to the in-cycle active set so a second template queuing the same item
            // in this cycle sees it as already active and skips it.
            ctx.ActiveIssueIdentifiers.Add((identifier, providerId));
        }

        PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision,
            dispatched ? PipelineTelemetry.LoopDecisions.Dispatched : PipelineTelemetry.LoopDecisions.SkippedNoAgent));

        return new DispatchAttemptResult(dispatched);
    }

    /// <summary>
    /// Checks whether any pollable template has eligible items remaining in its queue.
    /// </summary>
    internal static bool HasEligible<T>(
        IReadOnlyList<PipelineJobTemplate> pollableTemplates,
        Dictionary<string, List<T>> queues,
        Func<PipelineJobTemplate, bool> isEnabledForTemplate)
    {
        foreach (var template in pollableTemplates)
        {
            if (!isEnabledForTemplate(template)) continue;
            if (queues.TryGetValue(template.Id, out var queue) && queue.Count > 0)
                return true;
        }
        return false;
    }
}
