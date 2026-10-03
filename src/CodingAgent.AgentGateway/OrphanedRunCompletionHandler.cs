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
        // use payload.FinalLabel when it is a known agent label, else fall back to a step-based
        // default that depends on the task type.
        //   - DecompositionAnalysis (Phase 1) sends FinalLabel = agent:epic-review via PostDecompositionPlanStep.
        //   - Decomposition (Phase 2) sends no FinalLabel → falls back to agent:done (Completed default).
        //   - Review sends no FinalLabel → falls back to agent:next (the queue label for re-review if needed).
        //   - Implementation / Consolidation → agent:done.
        // TODO: [WARNING] Validate payload.FinalLabel against AgentLabels.SwapTargets instead of AgentLabels.All.
        // All.Contains("agent:generated") returns true, so a payload with FinalLabel = "agent:generated" would
        // apply the provenance label as a pipeline status, stripping every real status label. SwapTargets
        // excludes agent:generated and is the correct validator for the set of acceptable final labels.
        // The same exposure exists in SwapLabelAndPostCommentAsync — fix both together. See review findings.
        // TODO: [WARNING] AC3 migration gap (issue #3261): this inline `AgentLabels.All.Contains` guard
        // duplicates the FinalLabel-validation half of CompletionOutcomeResolver.ResolveAgentLabel, which was
        // extracted to centralise this pattern. A straight swap to ResolveAgentLabel is NOT behaviour-preserving
        // here because this method uses a task-type-dependent fallback (Review→agent:next, else→agent:done)
        // rather than the status-based fallback (Succeeded→agent:done). A faithful migration would call
        // ResolveAgentLabel only for the FinalLabel validation (pass the knownLabel guard result) and keep the
        // task-type switch for the fallback. The issue description notes this site as "should be migrated if it
        // fits within scope"; it was left out of scope for issue #3261. Track in a follow-up.
        var finalLabel = payload.FinalLabel is not null && AgentLabels.All.Contains(payload.FinalLabel)
            ? payload.FinalLabel
            : null;
        // TODO: [WARNING] The Review fallback label (agent:next) diverges from the normal completion path
        // (SwapLabelAndPostCommentAsync), which applies agent:done unconditionally for all task types
        // including Review when FinalStep = Completed and no FinalLabel is set. A completed Review run
        // that finishes on the normal path gets agent:done; the same run recovered via the orphan path
        // gets agent:next. The comment above saying this "mirrors" SwapLabelAndPostCommentAsync is
        // incorrect for the Review case. Align the two paths or document the intentional divergence.
        // See review findings (Correctness reviewer, line 346).
        // TODO: [WARNING] The Failed and Cancelled arms of this switch are unreachable: this method
        // is only called when workItemStatus == WorkItemStatus.Succeeded (FinalStep == Completed).
        // The Error and Cancelled labels are carried over verbatim from the original
        // AgentJobLifecycleService.TrySwapLabelAfterOrphanedRecoveryAsync and can never execute.
        // Removing them would tighten the method's documented scope; left in place to preserve
        // the extraction as a faithful refactor without introducing additional behavioural changes.
        var label = finalLabel ?? payload.FinalStep switch
        {
            PipelineStep.Completed => runRecord.TaskType switch
            {
                WorkItemTaskType.Review => AgentLabels.Next,
                _ => AgentLabels.Done
            },
            PipelineStep.Failed => AgentLabels.Error,
            PipelineStep.Cancelled => AgentLabels.Cancelled,
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
