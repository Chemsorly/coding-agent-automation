using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services;

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
    /// Shared helper that encapsulates the DB-vs-legacy dispatch branching.
    /// </summary>
    private async Task<bool> DispatchViaOrchestrationOrLegacyAsync(
        Func<CancellationToken, Task<JobDistributionRequest?>> prepareDbRequest,
        Func<JobDistributionRequest> buildLegacyRequest,
        CancellationToken ct)
    {
        if (_dispatchOrchestration is not null)
        {
            var request = await prepareDbRequest(ct);
            if (request is null) return false;
            var outcome = await _dispatchOrchestration.DistributeAndFinalizeAsync(request, ct);
            return outcome.Success;
        }
        else
        {
            var minimalRequest = buildLegacyRequest();
            var result = await _workDistributor!.DistributeAsync(minimalRequest, ct);
            return result.Success;
        }
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

    /// <summary>
    /// Checks whether any project-level decomposition queue has eligible epics remaining.
    /// </summary>
    internal static bool HasEligibleProjectLevelDecomposition(
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>> projectLevelQueues)
    {
        foreach (var kvp in projectLevelQueues)
        {
            if (kvp.Value.Count > 0)
                return true;
        }
        return false;
    }
}
