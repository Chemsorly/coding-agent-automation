using System.Diagnostics;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Hubs;

/// <summary>
/// Encapsulates job-lifecycle business logic extracted from AgentHub.Pipeline.cs.
/// Handles job acceptance, rejection, completion, and step transitions.
/// The hub delegates to this service after resolving SignalR-specific context.
/// </summary>
public sealed class AgentJobLifecycleService : IAgentJobLifecycleService
{
    private readonly IAgentHubFacade _facade;
    private readonly IRunLifecycleManager _lifecycleManager;
    private readonly ILabelSwapper _labelSwapper;
    private readonly IHubIssueOperations _issueOps;
    private readonly PipelineOrchestrationService _orchestration;
    private readonly ILogger _logger;

    public AgentJobLifecycleService(
        IAgentHubFacade facade,
        IRunLifecycleManager lifecycleManager,
        ILabelSwapper labelSwapper,
        IHubIssueOperations issueOps,
        PipelineOrchestrationService orchestration,
        ILogger logger)
    {
        _facade = facade;
        _lifecycleManager = lifecycleManager;
        _labelSwapper = labelSwapper;
        _issueOps = issueOps;
        _orchestration = orchestration;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task HandleJobAcceptedAsync(JobId jobId, AgentEntry? agent, CancellationToken ct)
    {
        if (agent is not null)
        {
            _facade.TransitionStatus(agent.AgentId, AgentStatus.Busy);
            _logger.Information("Agent {AgentId} accepted job {JobId}", agent.AgentId, jobId.Value);
            _orchestration.NotifyChange();
        }

        // Transition WorkItem from Dispatched → Running (DB+SignalR mode).
        // This is critical: without it, ReportJobCompleted cannot transition to Succeeded
        // because Dispatched → Succeeded is not a valid state transition.
        try
        {
            await _facade.TransitionWorkItemAsync(jobId.Value, WorkItemStatus.Running, ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to transition WorkItem {JobId} to Running on JobAccepted", jobId.Value);
        }
    }

    /// <inheritdoc />
    public async Task HandleJobRejectedAsync(JobId jobId, AgentEntry? agent, string reason, CancellationToken ct)
    {
        _logger.Warning("Agent {AgentId} rejected job {JobId}: {Reason}", agent?.AgentId, jobId.Value, reason);

        // Clean up the orphaned run so the issue can be re-dispatched
        var run = _facade.GetRun(jobId.Value);
        if (run is not null)
        {
            _facade.RemoveRun(jobId.Value);

            // Check retry count to decide: re-queue or permanently fail
            const int maxRejectionRetries = 3;
            var retryCount = await _facade.GetWorkItemRetryCountAsync(jobId.Value, ct);
            var shouldRequeue = retryCount < maxRejectionRetries;

            if (shouldRequeue)
            {
                // Re-queue: transition back to Pending with incremented RetryCount.
                // The drain service will pick it up again on the next cycle.
                // Clear the dedup tracker so the drain/loop doesn't consider it "already processing".
                _facade.MarkIssueComplete(run.IssueIdentifier, run.IssueProviderConfigId);
                try
                {
                    await _facade.RequeueWorkItemAsync(jobId.Value, ct);
                    _logger.Information(
                        "JobRejected: re-queued job {JobId} for issue {IssueIdentifier} (retry {RetryCount}/{MaxRetries})",
                        jobId.Value, run.IssueIdentifier, retryCount + 1, maxRejectionRetries);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to re-queue WorkItem {JobId}, falling back to permanent failure", jobId.Value);
                    shouldRequeue = false; // fall through to permanent failure
                }
            }

            if (!shouldRequeue)
            {
                // Max retries exhausted (or re-queue failed) — permanent failure. Human intervention needed.
                _facade.MarkIssueComplete(run.IssueIdentifier, run.IssueProviderConfigId);

                try
                {
                    var rejectionError = $"Job rejected by agent after {maxRejectionRetries} attempts: {reason}";
                    await _facade.TransitionWorkItemAsync(jobId.Value, WorkItemStatus.Failed, ct,
                        rejectionError, FailureReason.InfrastructureFailure);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to transition WorkItem {JobId} to Failed on JobRejected", jobId.Value);
                }

                try
                {
                    _logger.Warning("JobRejected: swapping label to agent:error for issue {IssueIdentifier} (jobId={JobId}, retries exhausted)",
                        run.IssueIdentifier, jobId.Value);
                    await _issueOps.SwapLabelAsync(run, AgentLabels.Error, GetLabelTargetKind(run));
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to revert label for rejected run {JobId} (issue {IssueIdentifier})",
                        jobId.Value, run.IssueIdentifier);
                }
            }

            _logger.Warning("Cleaned up rejected run {JobId} for issue {IssueIdentifier} (step={Step}, agent={AgentId}, retryCount={RetryCount}). " +
                "This indicates a dispatch race condition — investigate if recurring.",
                jobId.Value, run.IssueIdentifier, run.CurrentStep, run.AgentId, retryCount);

            _orchestration.NotifyChange();
        }
        else
        {
            _logger.Warning("Agent rejected job {JobId} but no active run found — may have been cleaned up already", jobId.Value);
        }

        // Transition agent back to Idle (it may still be marked Busy from reservation)
        if (agent is not null)
        {
            agent.ActiveJobId = null;
            agent.LastJobCompletedAt = DateTimeOffset.UtcNow; // Push to back of FIFO queue to prevent same-agent re-dispatch loop
            _facade.TransitionStatus(agent.AgentId, AgentStatus.Idle);

            // Signal drain service — agent is idle and may pick up a different job
            _facade.Signal();
        }
    }

    /// <inheritdoc />
    public async Task HandleJobCompletedAsync(JobId jobId, AgentEntry? agent, JobCompletionPayload payload, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Hub.ReportJobCompleted");
        activity?.SetTag("job_id", jobId.Value);

        var run = _facade.GetRun(jobId.Value);

        if (run is not null)
        {
            // Skip pipeline history persistence for consolidation runs.
            // Consolidation runs have their own completion path (ReportConsolidationComplete)
            // and their own history on the Consolidation page. They enter the PipelineRun
            // tracking only as ghost entries during orchestrator restart rehydration.
            if (run.IssueProviderConfigId == ConsolidationConstants.ProviderConfigId)
            {
                _logger.Information(
                    "ReportJobCompleted: skipping pipeline persistence for consolidation run {JobId} (IssueIdentifier={IssueIdentifier})",
                    jobId.Value, run.IssueIdentifier);

                // Clean up the in-memory run and transition WorkItem (still needed for DB state).
                // Wrapped in try/catch to ensure agent idle transition always happens.
                var consolidationWorkItemStatus = payload.FinalStep switch
                {
                    PipelineStep.Completed => WorkItemStatus.Succeeded,
                    PipelineStep.Cancelled => WorkItemStatus.Cancelled,
                    _ => WorkItemStatus.Failed
                };

                _facade.RemoveRun(jobId.Value);
                _facade.MarkIssueComplete(run.IssueIdentifier, run.IssueProviderConfigId);

                try
                {
                    var consolidationError = consolidationWorkItemStatus == WorkItemStatus.Failed
                        ? payload.FailureReason ?? "Consolidation run failed"
                        : null;
                    var consolidationFailureEnum = consolidationWorkItemStatus == WorkItemStatus.Failed
                        ? payload.FailureCategory ?? FailureReason.AgentError
                        : (FailureReason?)null;
                    await _facade.TransitionWorkItemAsync(jobId.Value, consolidationWorkItemStatus, ct,
                        consolidationError, consolidationFailureEnum);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "ReportJobCompleted: failed to transition consolidation WorkItem {JobId} (non-fatal)", jobId.Value);
                }

                // Transition agent to Idle and signal drain service for next dispatch
                if (agent is not null)
                {
                    agent.ActiveJobId = null;
                    agent.OrphanRestoredAt = null;
                    agent.LastJobCompletedAt = DateTimeOffset.UtcNow;
                    _facade.TransitionStatus(agent.AgentId, AgentStatus.Idle);
                }

                _orchestration.NotifyChange();
                return;
            }

            // Update run with completion data
            JobCompletionMapper.Apply(run, payload);

            activity?.SetTag("success", payload.FinalStep == PipelineStep.Completed);

            // Determine terminal WorkItem status
            var workItemStatus = payload.FinalStep switch
            {
                PipelineStep.Completed => WorkItemStatus.Succeeded,
                PipelineStep.Cancelled => WorkItemStatus.Cancelled,
                _ => WorkItemStatus.Failed
            };

            // Use lifecycle manager to atomically: remove run, transition DB WorkItem,
            // persist history, and mark issue complete in dedup tracker.
            try
            {
                var errorMsg = workItemStatus == WorkItemStatus.Failed
                    ? run.FailureReason ?? "Agent reported failure"
                    : null;
                var failureEnum = workItemStatus == WorkItemStatus.Failed
                    ? payload.FailureCategory ?? FailureReason.AgentError
                    : (FailureReason?)null;
                var completedRun = await _lifecycleManager.CompleteRunAsync(jobId.Value, workItemStatus, ct,
                    errorMsg, failureEnum);
                if (completedRun is null)
                {
                    // Race: run was removed by RevertFailedDistributionAsync between GetRun and CompleteRunAsync.
                    // The DB WorkItem transition inside CompleteRunAsync was skipped (it returns early on null RemoveRun).
                    // Attempt direct DB transition — will use infrastructure-failure recovery fallback if needed.
                    _logger.Warning(
                        "CompleteRunAsync returned null for job {JobId} (race with RevertFailedDistributionAsync), attempting direct DB transition",
                        jobId.Value);
                    await _facade.TransitionWorkItemAsync(jobId.Value, workItemStatus, ct, errorMsg, failureEnum);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "CompleteRunAsync failed for job {JobId} (status={Status}), performing defensive cleanup", jobId.Value, workItemStatus);

                // Defensive cleanup: if CompleteRunAsync threw (e.g., DB failure mid-operation),
                // the dedup guard and active runs list may not have been cleaned up.
                // Without this, the issue becomes permanently blocked from re-dispatch.
                _facade.RemoveRun(jobId.Value);
                _facade.MarkIssueComplete(run.IssueIdentifier, run.IssueProviderConfigId);

                // Attempt to transition WorkItem to terminal state so it doesn't stay stuck in Running.
                try
                {
                    var errorMsg = workItemStatus == WorkItemStatus.Failed
                        ? run.FailureReason ?? "Agent reported failure (defensive cleanup after exception)"
                        : null;
                    var failureEnum = workItemStatus == WorkItemStatus.Failed
                        ? payload.FailureCategory ?? FailureReason.AgentError
                        : (FailureReason?)null;
                    await _facade.TransitionWorkItemAsync(jobId.Value, workItemStatus, ct, errorMsg, failureEnum);
                }
                catch (Exception innerEx)
                {
                    _logger.Warning(innerEx, "Failed to transition WorkItem {JobId} to {Status} during defensive cleanup", jobId.Value, workItemStatus);
                }
            }

            _logger.Information(
                "Job {JobId} completed: step={FinalStep}, PR={PullRequestUrl}",
                jobId.Value, payload.FinalStep, payload.PullRequestUrl ?? "none");

            _orchestration.NotifyChange();
        }
        else
        {
            // Run not in memory — this happens when RevertFailedDistributionAsync already cleaned up
            // after a delivery timeout, but the agent actually received and completed the job.
            // Attempt direct DB recovery: if the WorkItem is in Failed with InfrastructureFailure reason,
            // transition it to the appropriate terminal status.
            var workItemStatus = payload.FinalStep switch
            {
                PipelineStep.Completed => WorkItemStatus.Succeeded,
                PipelineStep.Cancelled => WorkItemStatus.Cancelled,
                _ => WorkItemStatus.Failed
            };

            _logger.Warning(
                "ReportJobCompleted for job {JobId} — run not found, attempting DB recovery (finalStep={FinalStep})",
                jobId.Value, payload.FinalStep);

            var recoveryErrorMsg = workItemStatus == WorkItemStatus.Failed
                ? payload.FailureReason ?? "Agent reported failure (run not in memory)"
                : null;
            var recoveryFailureEnum = workItemStatus == WorkItemStatus.Failed
                ? payload.FailureCategory ?? FailureReason.AgentError
                : (FailureReason?)null;
            await _facade.TransitionWorkItemAsync(jobId.Value, workItemStatus, ct, recoveryErrorMsg, recoveryFailureEnum);

            // TODO: Call _facade.MarkIssueComplete() after successful recovery to update the in-memory dedup tracker.
            // Without it, the closed-loop poll could re-dispatch this issue if the label swap below fails.

            // Best-effort label correction after recovery (label is currently agent:next from RevertFailedDistributionAsync)
            if (workItemStatus == WorkItemStatus.Succeeded)
            {
                try
                {
                    var metadata = await _facade.GetWorkItemIssueMetadataAsync(jobId.Value, ct);
                    if (metadata.HasValue)
                    {
                        await _labelSwapper.SwapLabelAsync(
                            metadata.Value.IssueProviderConfigId,
                            metadata.Value.IssueIdentifier,
                            AgentLabels.Done, LabelTargetKind.Issue, ct);
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to swap label after recovery for job {JobId} (cosmetic)", jobId.Value);
                }
            }
        }

        // Transition agent to Idle BEFORE slow I/O operations (label swap, comment posting).
        // This ensures agent availability is not gated on external provider latency.
        // NOTE: We do NOT call Signal() here. The agent will send AgentReady after clearing
        // its local _activeJobId (via ReleaseJobSlotAndSignalReadyAsync), which triggers
        // the safe Signal path. Signaling here caused a race condition where the drain
        // service dispatched to the agent before it cleared its local slot, resulting in
        // immediate rejection and permanent work item loss.
        if (agent is not null)
        {
            agent.ActiveJobId = null;
            agent.OrphanRestoredAt = null;
            agent.LastJobCompletedAt = DateTimeOffset.UtcNow;
            _facade.TransitionStatus(agent.AgentId, AgentStatus.Idle);
        }

        // Non-fatal post-completion bookkeeping: label swap and feedback comment.
        // These may involve external API calls and can be slow — executed after agent
        // is already marked Idle so it doesn't block availability.
        // Note: These run inline (not fire-and-forget) to maintain testability and ensure
        // label swaps complete before the hub method returns. The agent is already Idle
        // in the registry, so the dispatcher can assign it work via the periodic drain sweep.
        if (run is not null)
        {
            // Swap label based on final outcome (non-fatal).
            // The agent may also attempt a label swap via RequestLabelChange during its own
            // error handling, but that call can race with this handler (run already removed).
            // This is the authoritative swap that guarantees correctness.
            // Only accept FinalLabel if it is a known agent label; ignore arbitrary values.
            var finalLabel = payload.FinalLabel is not null && AgentLabels.All.Contains(payload.FinalLabel)
                ? payload.FinalLabel
                : null;
            var label = finalLabel ?? payload.FinalStep switch
            {
                PipelineStep.Failed => AgentLabels.Error,
                PipelineStep.Completed => AgentLabels.Done,
                PipelineStep.Cancelled => AgentLabels.Cancelled,
                _ => null
            };

            if (label is not null)
            {
                _logger.Information(
                    "Job {JobId} ReportJobCompleted swapping label to {Label} for issue {IssueIdentifier} (finalStep={FinalStep}, finalLabel={FinalLabel})",
                    jobId.Value, label, run.IssueIdentifier, payload.FinalStep, payload.FinalLabel ?? "null");
                var swLabel = Stopwatch.StartNew();
                await _issueOps.SwapLabelAsync(run, label, GetLabelTargetKind(run));
                _logger.Information("Job {JobId} SwapLabelAsync completed in {ElapsedMs}ms", jobId.Value, swLabel.ElapsedMilliseconds);
            }

            // Post issue feedback comment if present (non-fatal)
            var swComment = Stopwatch.StartNew();
            await _issueOps.PostIssueFeedbackCommentAsync(run);
            _logger.Information("Job {JobId} PostIssueFeedbackCommentAsync completed in {ElapsedMs}ms", jobId.Value, swComment.ElapsedMilliseconds);
        }
    }

    /// <inheritdoc />
    public void HandleStepTransition(JobId jobId, PipelineStep step, DateTimeOffset timestamp, Dictionary<string, string>? metadata)
    {
        var run = _facade.GetRun(jobId.Value);
        if (run is not null)
        {
            run.CurrentStep = step;
            var clampedTimestamp = timestamp <= DateTimeOffset.UtcNow
                ? timestamp
                : DateTimeOffset.UtcNow;
            run.LastStepChangeAt = clampedTimestamp;

            // Persist progress to DB for cross-replica timeout enforcement (throttled)
            _ = _facade.TouchLastProgressAsync(jobId.Value, clampedTimestamp, CancellationToken.None);

            // Update HighWaterMark — only advance, never go backward
            // Uses StepOrder.GetOrder (logical execution order) — NOT enum ordinals.
            // Terminal states (Failed, Cancelled) return -1 and are excluded.
            if (step is not (PipelineStep.Failed or PipelineStep.Cancelled)
                && StepOrder.GetOrder(step) > StepOrder.GetOrder(run.HighWaterMark))
                run.HighWaterMark = step;

            // Apply step metadata from the agent (carries data from the just-completed step)
            if (metadata is { Count: > 0 })
                ApplyStepMetadata(run, metadata);

            _logger.Debug("Job {JobId} step transition → {Step}", jobId.Value, step);
            _orchestration.NotifyChange();
        }
    }

    /// <summary>
    /// Determines the correct <see cref="LabelTargetKind"/> based on the run's LabelTargetKind property.
    /// </summary>
    private static LabelTargetKind GetLabelTargetKind(PipelineRun run) => run.LabelTargetKind;

    /// <summary>
    /// Applies key-value metadata from step transitions to the PipelineRun.
    /// Keys use a flat naming convention (e.g., "BranchName", "BaselineHealthPassed").
    /// </summary>
    internal static void ApplyStepMetadata(PipelineRun run, Dictionary<string, string> metadata)
    {
        // Collect code review counts for single-pass atomic update
        int? pendingCritical = null, pendingWarning = null, pendingSuggestion = null;

        foreach (var (key, value) in metadata)
        {
            switch (key)
            {
                case "BranchName":
                    run.BranchName = value;
                    break;
                case "BaselineHealthPassed":
                    run.BaselineHealthPassed = bool.TryParse(value, out var bhp) ? bhp : null;
                    break;
                case "AnalysisSkipped":
                    run.AnalysisSkipped = bool.TryParse(value, out var ask) && ask;
                    break;
                case "FilesChangedCount":
                    if (int.TryParse(value, out var fcc)) run.FilesChangedCount = fcc;
                    break;
                case "LinesAdded":
                    if (int.TryParse(value, out var la)) run.LinesAdded = la;
                    break;
                case "LinesRemoved":
                    if (int.TryParse(value, out var lr)) run.LinesRemoved = lr;
                    break;
                case "CodeReviewIterationsCompleted":
                    if (int.TryParse(value, out var cric)) run.CodeReviewIterationsCompleted = cric;
                    break;
                case "CodeReviewIterationsTotal":
                    if (int.TryParse(value, out var crit)) run.CodeReviewIterationsTotal = crit;
                    break;
                case "CodeReviewIterationInProgress":
                    if (int.TryParse(value, out var crip)) run.CodeReviewIterationInProgress = crip;
                    break;
                case "OpenIssuesDownloaded":
                    if (int.TryParse(value, out var oid)) run.OpenIssuesDownloaded = oid;
                    break;
                case "DecompositionSubIssuesCreated":
                    if (int.TryParse(value, out var dsic)) run.DecompositionSubIssuesCreated = dsic;
                    break;
                case "DecompositionSubIssuesAttempted":
                    if (int.TryParse(value, out var dsia)) run.DecompositionSubIssuesAttempted = dsia;
                    break;
                case "RetryCount":
                    if (int.TryParse(value, out var rc)) run.RetryCount = rc;
                    break;
                case "InfrastructureRetryCount":
                    if (int.TryParse(value, out var irc)) run.InfrastructureRetryCount = irc;
                    break;
                case "TotalTokens":
                    if (long.TryParse(value, out var tt)) run.TotalTokens = tt;
                    break;
                case "TotalCost":
                    if (decimal.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var tc))
                        run.TotalCost = tc;
                    break;
                case "CodeReviewCriticalCount":
                    if (int.TryParse(value, out var crc)) pendingCritical = crc;
                    break;
                case "CodeReviewWarningCount":
                    if (int.TryParse(value, out var crw)) pendingWarning = crw;
                    break;
                case "CodeReviewSuggestionCount":
                    if (int.TryParse(value, out var crs)) pendingSuggestion = crs;
                    break;
                case "CodeReviewAgentsRun":
                    run.CodeReviewAgentsRun = value.Split('\x1F', StringSplitOptions.RemoveEmptyEntries);
                    break;
            }
        }

        // Apply code review counts atomically in a single call (avoids iteration-order dependency)
        if (pendingCritical.HasValue || pendingWarning.HasValue || pendingSuggestion.HasValue)
        {
            run.SetCodeReviewCounts(
                pendingCritical ?? run.CodeReviewCriticalCount,
                pendingWarning ?? run.CodeReviewWarningCount,
                pendingSuggestion ?? run.CodeReviewSuggestionCount);
        }
    }
}
