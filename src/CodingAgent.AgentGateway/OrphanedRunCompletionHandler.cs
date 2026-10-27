using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgent.AgentGateway;

/// <summary>
/// Handles the null-run (orphaned) completion path extracted from
/// <see cref="AgentJobLifecycleService.HandleJobCompletedAsync"/>.
/// <para>
/// This path is reached when <c>GetRun(jobId)</c> returns null — typically because
/// <c>RevertFailedDistributionAsync</c> already cleaned up after a delivery timeout,
/// but the agent actually received and completed the job.
/// </para>
/// <para>
/// This class is intentionally NOT an <see cref="IJobCompletionStrategy"/> implementation.
/// <see cref="IJobCompletionStrategy.ExecuteAsync"/> requires a non-null <see cref="PipelineRun"/>,
/// which this path does not have. It follows the same <c>internal sealed</c>, <c>new</c>-constructed
/// pattern as <see cref="AgentIdleTransitioner"/>.
/// </para>
/// </summary>
internal sealed class OrphanedRunCompletionHandler
{
    private readonly IAgentHubFacade _facade;
    private readonly ILabelService _labelService;
    private readonly ILogger _logger;

    internal OrphanedRunCompletionHandler(IAgentHubFacade facade, ILabelService labelService, ILogger logger)
    {
        _facade = facade;
        _labelService = labelService;
        _logger = logger;
    }

    /// <summary>
    /// Attempts direct DB recovery for a job whose run was not found in memory, then performs
    /// a best-effort label swap when the job completed successfully.
    /// </summary>
    internal async Task HandleAsync(JobId jobId, JobCompletionPayload payload, CancellationToken ct)
    {
        // Run not in memory — this happens when RevertFailedDistributionAsync already cleaned up
        // after a delivery timeout, but the agent actually received and completed the job.
        // Attempt direct DB recovery: if the WorkItem is in Failed with InfrastructureFailure or
        // Timeout reason (both represent "server gave up waiting, agent may still succeed"), transition
        // it to the appropriate terminal status.
        var (workItemStatus, recoveryErrorMsg, recoveryFailureEnum) =
            CompletionOutcomeResolver.Resolve(payload.FinalStep, payload.FailureReason, payload.FailureCategory,
                "Agent reported failure (run not in memory)");

        _logger.Warning(
            "ReportJobCompleted for job {JobId} — run not found, attempting DB recovery (finalStep={FinalStep})",
            jobId.Value, payload.FinalStep);

        await _facade.TransitionWorkItemAsync(jobId, workItemStatus, ct, recoveryErrorMsg, recoveryFailureEnum);

        // Fetch the full work item record for the label swap below.
        // The record includes TaskType and RepoProviderConfigId which are needed to:
        //   - select the correct label target (PullRequest for Review runs, Issue for all others)
        //   - apply payload.FinalLabel when valid (e.g. agent:epic-review for a DecompositionAnalysis)
        // The reverted label on the issue depends on the run type (see RevertFailedDistributionAsync).
        // Duplicate dispatch is prevented by the partial unique index on WorkItems
        // (IssueIdentifier, IssueProviderConfigId) filtered to non-terminal statuses,
        // plus the in-process IsIssueBeingProcessed check at dispatch time.
        var runRecord = await _facade.GetWorkItemRunRecordAsync(jobId, ct);

        // Best-effort label correction after recovery.
        if (workItemStatus == WorkItemStatus.Succeeded)
        {
            await TrySwapLabelAfterOrphanedRecoveryAsync(jobId, runRecord, payload, ct);
        }
    }

    private Task TrySwapLabelAfterOrphanedRecoveryAsync(
        JobId jobId,
        WorkItemRunRecord? runRecord,
        JobCompletionPayload payload,
        CancellationToken ct)
    {
        if (runRecord is null) return Task.CompletedTask;

        // Determine the label to apply — mirrors the logic in SwapLabelAndPostCommentAsync:
        // use payload.FinalLabel when it is a known swap-target agent label, else fall back to a
        // step-based default that depends on the task type.
        //   - DecompositionAnalysis (Phase 1) sends FinalLabel = agent:epic-review via PostDecompositionPlanStep.
        //   - Decomposition (Phase 2) sends no FinalLabel → falls back to agent:done (Completed default).
        //   - Review sends no FinalLabel → falls back to agent:done (same as the normal completion path).
        //   - Implementation / Consolidation → agent:done.
        // SwapTargets is All minus agent:generated. agent:generated is a provenance/kind label that must
        // not be used as a pipeline status — using All.Contains would allow it to corrupt the label
        // state machine by stripping every real status label and writing the provenance label instead.
        var finalLabel = payload.FinalLabel is not null && AgentLabels.SwapTargets.Contains(payload.FinalLabel)
            ? payload.FinalLabel
            : null;
        // This method is reached only when workItemStatus == WorkItemStatus.Succeeded.
        // CompletionOutcomeResolver.Resolve returns Succeeded for exactly three FinalStep values:
        //   {Completed, ConflictRestart, PrMerged}.
        // Completed → apply a task-type-dependent label (see switch below).
        // ConflictRestart and PrMerged → fall through to _ => null (no label applied), mirroring
        // AgentJobLifecycleService; the warning log below fires to surface the no-op to operators.
        var label = finalLabel ?? payload.FinalStep switch
        {
            PipelineStep.Completed => runRecord.TaskType switch
            {
                WorkItemTaskType.Review => AgentLabels.Done,
                // A completed triage waits for a person to review its drafts in the app
                WorkItemTaskType.Triage => AgentLabels.TriageReview,
                _ => AgentLabels.Done
            },
            _ => null
        };

        if (label is null)
        {
            _logger.Warning(
                "TrySwapLabelAfterOrphanedRecovery: no label to apply for job {JobId} (finalStep={FinalStep})",
                jobId.Value, payload.FinalStep);
            return Task.CompletedTask;
        }

        // Route to the correct provider and target kind.
        // Review runs label the pull request; all others label the issue.
        string providerConfigId;
        LabelTargetKind targetKind;
        if (runRecord.TaskType == WorkItemTaskType.Review)
        {
            if (runRecord.RepoProviderConfigId is null)
            {
                // TODO: [WARNING] Falling back to IssueProviderConfigId while still applying
                // LabelTargetKind.PullRequest is incorrect when issues and pull requests are hosted
                // on different providers (e.g. Jira + GitHub). The issue-tracker provider config
                // cannot reliably resolve a pull-request target on the code-hosting provider.
                // The safer behaviour is to skip the swap entirely when RepoProviderConfigId is null
                // on a Review run — log the warning and return Task.CompletedTask — rather than
                // silently routing to the wrong provider. See review findings (Correctness reviewer,
                // line 390; DotNetSpecialist reviewer, line 397).
                _logger.Warning(
                    "TrySwapLabelAfterOrphanedRecovery: Review run {JobId} has null RepoProviderConfigId — falling back to IssueProviderConfigId",
                    jobId.Value);
                providerConfigId = runRecord.IssueProviderConfigId;
            }
            else
            {
                providerConfigId = runRecord.RepoProviderConfigId;
            }
            targetKind = LabelTargetKind.PullRequest;
        }
        else
        {
            providerConfigId = runRecord.IssueProviderConfigId;
            targetKind = LabelTargetKind.Issue;
        }

        return _labelService.TrySwapLabelAsync(
            providerConfigId, runRecord.IssueIdentifier, label, targetKind,
            _logger, $"OrphanedRunCompletionHandler.TrySwapLabelAfterOrphanedRecovery (job {jobId.Value})",
            ct);
    }
}
