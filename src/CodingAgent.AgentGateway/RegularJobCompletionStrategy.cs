using System.Diagnostics;
using CodingAgent.Infrastructure.Common;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgent.AgentGateway;

/// <summary>
/// Handles completion of regular (non-consolidation) pipeline runs.
/// Extracted from <see cref="AgentJobLifecycleService.HandleRegularRunCompletedAsync"/>.
/// </summary>
/// <remarks>
/// Does not perform agent-idle transitions — that is the caller's responsibility.
/// </remarks>
internal sealed class RegularJobCompletionStrategy : IJobCompletionStrategy
{
    private readonly IAgentHubFacade _facade;
    private readonly IRunLifecycleManager _lifecycleManager;
    private readonly IChangeNotifier _changeNotifier;
    private readonly ILogger _logger;

    public RegularJobCompletionStrategy(
        IAgentHubFacade facade,
        IRunLifecycleManager lifecycleManager,
        IChangeNotifier changeNotifier,
        ILogger logger)
    {
        _facade = facade;
        _lifecycleManager = lifecycleManager;
        _changeNotifier = changeNotifier;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> ExecuteAsync(JobId jobId, PipelineRun run, JobCompletionPayload payload,
                                         Activity? activity, CancellationToken ct)
    {
        // Update run with completion data
        JobCompletionMapper.Apply(run, payload);

        // Persist Apply's mutations back to Redis so CompleteRunAsync's RemoveRun
        // deserializes the updated state. Without this, DistributedRunService.RemoveRun
        // re-reads the pre-Apply Redis snapshot and history is persisted with nulls/zeros.
        // Same pattern as WorkItemEndpoints.ClaimWorkItem — see that file's comment for
        // a full explanation of why ReplaceRun is required (not just GetRun + mutate).
        // In OrchestratorRunService (in-memory), this is a no-op: the same reference is
        // already in the dictionary, so re-assigning it has no observable effect.
        _facade.ReplaceRun(run);

        activity?.SetTag("success", payload.FinalStep == PipelineStep.Completed);

        var (workItemStatus, errorMsg, failureEnum) =
            CompletionOutcomeResolver.Resolve(payload.FinalStep, run.FailureReason, payload.FailureCategory,
                "Agent reported failure");
        // run.FailureReason is used here because JobCompletionMapper.Apply has already been called above,
        // copying payload.FailureReason → run.FailureReason. The values are equivalent at this point.

        // Use lifecycle manager to atomically: remove run, transition DB WorkItem,
        // persist history, and mark issue complete in dedup tracker.
        bool outcomeApplied = true;
        try
        {
            var completedRun = await _lifecycleManager.CompleteRunAsync(jobId.Value, workItemStatus, ct,
                errorMsg, failureEnum);
            if (completedRun is null)
            {
                // Race: another path (e.g. HTTP Failed POST or RevertFailedDistributionAsync) terminated
                // the run first. Either it removed the run between GetRun and CompleteRunAsync, which then
                // skipped the DB WorkItem transition, or it moved the WorkItem to a different terminal
                // status, which CompleteRunAsync then labelled. Attempt direct DB transition — will use
                // infrastructure-failure recovery fallback if needed.
                // The label follows the WorkItem's status: when the transition is rejected, the other
                // path's status and label stand and the caller skips its label swap. When it takes
                // effect (a Succeeded run that outlived a Timeout or InfrastructureFailure, #2146) or the
                // item is already at this status, the caller applies this payload's label; keeping the
                // other path's agent:error would contradict the recovered status.
                _logger.Warning(
                    "CompleteRunAsync returned null for job {JobId} (another terminal path won — HTTP Failed or RevertFailedDistributionAsync), attempting direct DB transition",
                    jobId.Value);
                outcomeApplied = await _facade.TransitionWorkItemAsync(jobId.Value, workItemStatus, ct, errorMsg, failureEnum);
            }
        }
        catch (Exception ex)
        {
            await DefensiveRunCleanupAsync(jobId, run, payload, workItemStatus, ex);
        }

        _logger.Information(
            "Job {JobId} completed: step={FinalStep}, PR={PullRequestUrl}",
            jobId.Value, payload.FinalStep, LogSanitizer.SanitizeForLog(payload.PullRequestUrl ?? "none"));

        _changeNotifier.NotifyChange();
        return outcomeApplied;
    }

    private async Task DefensiveRunCleanupAsync(
        JobId jobId, PipelineRun run, JobCompletionPayload payload, WorkItemStatus workItemStatus, Exception outerEx)
    {
        _logger.Warning(outerEx, "CompleteRunAsync failed for job {JobId} (status={Status}), performing defensive cleanup", jobId.Value, workItemStatus);

        // Defensive cleanup: if CompleteRunAsync threw (e.g., DB failure mid-operation),
        // the dedup guard and active runs list may not have been cleaned up.
        // Without this, the issue becomes permanently blocked from re-dispatch.
        _facade.RemoveRun(jobId.Value);

        var (_, errorMsg, failureEnum) =
            CompletionOutcomeResolver.Resolve(payload.FinalStep, run.FailureReason, payload.FailureCategory,
                "Agent reported failure (defensive cleanup after exception)");
        // workItemStatus (from caller) is authoritative for the state transition.
        // The resolver's returned Status is discarded — we call Resolve only for the error strings.

        // intentional: ct may already be cancelled (e.g., during host shutdown — that is exactly when
        // this defensive path fires). Passing ct would short-circuit the transition silently.
        await _facade.TransitionWorkItemAsync(jobId.Value, workItemStatus, CancellationToken.None, errorMsg, failureEnum);
    }
}
