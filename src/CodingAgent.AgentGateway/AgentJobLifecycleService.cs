using System.Diagnostics;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using ILogger = Serilog.ILogger;

namespace CodingAgent.AgentGateway;

/// <summary>
/// Encapsulates job-lifecycle business logic extracted from AgentHub.Pipeline.cs.
/// Handles job acceptance, rejection, completion, and step transitions.
/// The hub delegates to this service after resolving SignalR-specific context.
/// </summary>
public sealed class AgentJobLifecycleService : IAgentJobLifecycleService
{
    private readonly IAgentHubFacade _facade;
    private readonly ILabelService _labelService;
    private readonly IHubIssueOperations _issueOps;
    private readonly IChangeNotifier _changeNotifier;
    private readonly Microsoft.Extensions.Hosting.IHostApplicationLifetime _appLifetime;
    private readonly IFeedbackCommentOutbox _outbox;
    private readonly ILogger _logger;

    private readonly IJobCompletionStrategy _regularStrategy;
    private readonly IJobCompletionStrategy _consolidationStrategy;
    private readonly AgentIdleTransitioner _idleTransitioner;

    public AgentJobLifecycleService(AgentJobLifecycleServiceDependencies deps)
    {
        // TODO: [WARNING] Add ArgumentNullException.ThrowIfNull(deps) here to produce a clear
        // ArgumentNullException instead of a NullReferenceException when deps is null.
        // Consistent with the ThrowIfNull pattern used in other public constructors in this assembly
        // (e.g. AgentHubFacade, AgentHub). Constructor is public so callers outside the assembly
        // can reach it. (DotNetSpecialist review finding.)
        _facade = deps.Facade;
        _labelService = deps.LabelService;
        _issueOps = deps.IssueOps;
        _changeNotifier = deps.ChangeNotifier;
        _appLifetime = deps.AppLifetime;
        _outbox = deps.Outbox;
        _logger = deps.Logger;

        _regularStrategy = new RegularJobCompletionStrategy(deps.Facade, deps.LifecycleManager, deps.ChangeNotifier, deps.Logger);
        _consolidationStrategy = new ConsolidationJobCompletionStrategy(deps.Facade, deps.ChangeNotifier, deps.Logger);
        _idleTransitioner = new AgentIdleTransitioner(deps.Facade, deps.Logger);
        // Strategies are instantiated with new rather than injected via DI. Follow-up work item:
        // register IJobCompletionStrategy implementations (keyed/named) in DI and inject them through
        // the constructor to make AgentJobLifecycleService fully unit-testable at the strategy level.
    }

    /// <inheritdoc />
    public async Task HandleJobAcceptedAsync(JobId jobId, AgentEntry? agent, CancellationToken ct)
    {
        // Transition WorkItem from Dispatched → Running FIRST.
        // Side-effects (TransitionStatus, NotifyChange) must only be committed after the DB write
        // succeeds. Committing them before risks a split-state: agent registry says Busy while the
        // WorkItem remains Dispatched, causing EnforceTimeoutsAsync to classify the job as Failed.
        bool transitioned;
        try
        {
            transitioned = await _facade.TransitionWorkItemAsync(jobId, WorkItemStatus.Running, ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to transition WorkItem {JobId} to Running on JobAccepted", jobId.Value);
            return; // Do not mark agent Busy — leave prior state intact for recovery
        }

        if (!transitioned)
        {
            _logger.Warning("WorkItem {JobId} transition to Running was rejected on JobAccepted — agent will not be marked Busy", jobId.Value);
            return; // Do not mark agent Busy — WorkItem may already be in a terminal state
        }

        // Side-effects committed only after the DB write succeeds.
        if (agent is not null)
        {
            _facade.TransitionStatus(agent.AgentId, AgentStatus.Busy);
            _logger.Information("Agent {AgentId} accepted job {JobId}", agent.AgentId, jobId.Value);
            _changeNotifier.NotifyChange();
        }
    }

    /// <inheritdoc />
    public async Task HandleJobRejectedAsync(JobId jobId, AgentEntry? agent, string reason, CancellationToken ct)
    {
        _logger.Warning("Agent {AgentId} rejected job {JobId}: {Reason}", agent?.AgentId, jobId.Value, reason);

        // Clean up the orphaned run so the issue can be re-dispatched
        var run = _facade.GetRun(jobId);
        if (run is not null)
        {
            _facade.RemoveRun(jobId);
            try
            {
                await HandleRejectedRunCleanupAsync(jobId, run, reason, ct);
            }
            finally
            {
                // Transition agent back to Idle unconditionally — even if cleanup throws.
                // Placed in finally so a DB/network failure inside HandleRejectedRunCleanupAsync
                // does not leave the agent stuck Busy until ReconciliationService timeout.
                ResetAgentToIdle(agent);
            }
            return; // agent state already reset in finally above; skip the no-run path below
        }
        else
        {
            _logger.Warning("Agent rejected job {JobId} but no active run found — may have been cleaned up already", jobId.Value);
        }

        // Reached only when run is null — transition agent back to Idle for the no-run path
        ResetAgentToIdle(agent);
    }

    private void ResetAgentToIdle(AgentEntry? agent)
    {
        if (agent is null) return;

        var now = DateTimeOffset.UtcNow;
        agent.ActiveJobId = null;
        agent.LastJobCompletedAt = now; // Push to back of FIFO queue to prevent same-agent re-dispatch loop
        _facade.UpdateAgentFieldFireAndForget(agent.AgentId, AgentFieldNames.ActiveJobId, null, _logger, "ResetAgentToIdle");
        _facade.UpdateAgentFieldFireAndForget(agent.AgentId, AgentFieldNames.LastJobCompletedAt, now.ToString("O"), _logger, "ResetAgentToIdle");
        _facade.TransitionStatus(agent.AgentId, AgentStatus.Idle);
    }

    private async Task HandleRejectedRunCleanupAsync(JobId jobId, PipelineRun run, string reason, CancellationToken ct)
    {
        // Check retry count to decide: re-queue or permanently fail
        const int maxRejectionRetries = 3;
        var retryCount = await _facade.GetWorkItemRetryCountAsync(jobId, ct);
        var shouldRequeue = retryCount < maxRejectionRetries;

        if (shouldRequeue)
        {
            shouldRequeue = await TryRequeueRejectedRunAsync(jobId, run, retryCount, maxRejectionRetries, ct);
        }

        if (!shouldRequeue)
        {
            await PermanentlyFailRejectedRunAsync(jobId, run, reason, maxRejectionRetries, ct);
        }

        _logger.Warning("Cleaned up rejected run {JobId} for issue {IssueIdentifier} (step={Step}, agent={AgentId}, retryCount={RetryCount}). " +
            "This indicates a dispatch race condition — investigate if recurring.",
            jobId.Value, run.IssueIdentifier, run.CurrentStep, run.AgentId, retryCount);

        _changeNotifier.NotifyChange();
    }

    private async Task<bool> TryRequeueRejectedRunAsync(JobId jobId, PipelineRun run, int retryCount, int maxRejectionRetries, CancellationToken ct)
    {
        // Re-queue: transition back to Pending with incremented RetryCount.
        // The drain service will pick it up again on the next cycle.
        try
        {
            await _facade.RequeueWorkItemAsync(jobId, ct);
            _logger.Information(
                "JobRejected: re-queued job {JobId} for issue {IssueIdentifier} (retry {RetryCount}/{MaxRetries})",
                jobId.Value, run.IssueIdentifier, retryCount + 1, maxRejectionRetries);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to re-queue WorkItem {JobId}, falling back to permanent failure", jobId.Value);
            return false;
        }
    }

    private async Task PermanentlyFailRejectedRunAsync(JobId jobId, PipelineRun run, string reason, int maxRejectionRetries, CancellationToken ct)
    {
        // Max retries exhausted (or re-queue failed) — permanent failure. Human intervention needed.

        try
        {
            var rejectionError = $"Job rejected by agent after {maxRejectionRetries} attempts: {reason}";
            await _facade.TransitionWorkItemAsync(jobId, WorkItemStatus.Failed, ct,
                rejectionError, FailureReason.InfrastructureFailure);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to transition WorkItem {JobId} to Failed on JobRejected", jobId.Value);
        }

        _logger.Warning("JobRejected: swapping label to agent:error for issue {IssueIdentifier} (jobId={JobId}, retries exhausted)",
            run.IssueIdentifier, jobId.Value);
        // TODO: [WARNING] This call hardcodes run.IssueProviderConfigId and LabelTargetKind.Issue, which is
        // incorrect for Review runs — those route via run.ProviderConfigIdForLabel (RepoProviderConfigId) and
        // LabelTargetKind.PullRequest. Use the run-aware overload instead:
        //   await _labelService.TrySwapLabelAsync(run, AgentLabels.Error, _logger, "...", ct);
        // PermanentlyFailRejectedRunAsync is reachable for all run types (HandleRejectedRunCleanupAsync
        // does not filter on RunType), so a rejected Review run will attempt to swap the wrong target.
        // TODO: [WARNING] OCE behavior changed: the old inline catch (Exception ex) swallowed OCE; the new
        // call uses SwallowCancellation=false (the default), so OCE now propagates out of this method into
        // HandleRejectedRunCleanupAsync (called from a finally block in HandleJobRejectedAsync). Verify that
        // propagating OCE here during post-rejection cleanup does not strand agent state.
        // TODO: [WARNING] TrySwapLabelAsync now returns Task<bool> (true = applied, false = non-fatal
        // exception swallowed). The bool is intentionally discarded here — this is fire-and-forget.
        // If diagnostics on swap failures are ever needed, capture and log the result.
        await _labelService.TrySwapLabelAsync(
            run.IssueProviderConfigId, run.IssueIdentifier, AgentLabels.Error, LabelTargetKind.Issue,
            _logger, "AgentJobLifecycleService.PermanentlyFailRejectedRunAsync", ct);
    }

    /// <inheritdoc />
    public async Task HandleJobCompletedAsync(JobId jobId, AgentEntry? agent, JobCompletionPayload payload, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Hub.ReportJobCompleted");
        activity?.SetTag("job_id", jobId.Value);

        var run = _facade.GetRun(jobId);

        // runWasAlive: true if the run existed in memory and was processed normally (or if the run
        // was not found at all — orphan path has its own handling). false only when
        // RegularJobCompletionStrategy.ExecuteAsync signals that CompleteRunAsync returned null,
        // meaning another path (HTTP Failed POST or RevertFailedDistributionAsync) already
        // terminated the run. In that case we skip the label swap to preserve the other path's label.
        bool runWasAlive = true;

        if (run is not null)
        {
            // Select strategy based on run type and execute run-type-specific completion logic.
            // Neither strategy touches agent state — that is this method's responsibility.
            IJobCompletionStrategy strategy = run.IssueProviderConfigId == ConsolidationConstants.ProviderConfigId
                ? _consolidationStrategy
                : _regularStrategy;

            runWasAlive = await strategy.ExecuteAsync(jobId, run, payload, activity, ct);
        }
        else
        {
            await HandleOrphanedRunCompletedAsync(jobId, payload, ct);
        }

        // Transition agent to Idle BEFORE slow I/O operations (label swap, comment posting).
        // This ensures agent availability is not gated on external provider latency.
        // NOTE: We do NOT call Signal() here. The agent will send AgentReady after clearing
        // its local _activeJobId (via ReleaseJobSlotAndSignalReadyAsync), which triggers
        // the safe Signal path. Signaling here caused a race condition where the drain
        // service dispatched to the agent before it cleared its local slot, resulting in
        // immediate rejection and permanent work item loss.
        _idleTransitioner.TransitionToIdle(agent, run, jobId.Value);

        // Non-fatal post-completion bookkeeping: label swap and feedback comment.
        // These may involve external API calls and can be slow — executed after agent
        // is already marked Idle so it doesn't block availability.
        // Note: These run inline (not fire-and-forget) to maintain testability and ensure
        // label swaps complete before the hub method returns. The agent is already Idle
        // in the registry, so the dispatcher can assign it work via the periodic drain sweep.
        // Consolidation runs skip bookkeeping — they have no associated issue labels or feedback comments.
        if (run is not null && run.IssueProviderConfigId != ConsolidationConstants.ProviderConfigId)
        {
            // skipLabelSwap=true when the run was already terminated by another path (HTTP Failed POST).
            // In that case the HTTP path already set the correct label (e.g. agent:needs-refinement),
            // and we must not overwrite it. The outbox enqueue and feedback comment still run.
            // TODO: [WARNING] skipLabelSwap is derived from !runWasAlive, which is set to false whenever
            // CompleteRunAsync returns null — regardless of the payload's WorkItemStatus. The timeout
            // acceptance criterion (agent:cancelled suppressed after JobController sets agent:error) is
            // satisfied incidentally by this same mechanism, but for a Succeeded or Cancelled payload
            // where CompleteRunAsync returns null (e.g. race with RevertFailedDistributionAsync or double
            // hub delivery), skipLabelSwap=true will silently suppress agent:done/agent:next, leaving
            // the issue without a terminal label until OrphanedLabelRecoveryService corrects it.
            // Consider scoping the null-return guard to Failed payloads only (workItemStatus == Failed),
            // or using a richer return type from RegularJobCompletionStrategy to distinguish
            // "already terminated by HTTP Failed" from "race with RevertFailed/double delivery".
            // See review findings [WARNING] #4 (Correctness) and [WARNING] #1 (DotNetSpecialist).
            await PostCompletionBookkeepingAsync(jobId, run, payload, skipLabelSwap: !runWasAlive, ct);
        }
    }

    private async Task HandleOrphanedRunCompletedAsync(JobId jobId, JobCompletionPayload payload, CancellationToken ct)
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

        // Fetch issue metadata for the label swap below. Fetched for ALL terminal statuses
        // (Succeeded, Failed, Cancelled) because the label is currently agent:next, reverted by
        // RevertFailedDistributionAsync. Duplicate dispatch is prevented by the partial unique
        // index on WorkItems (IssueIdentifier, IssueProviderConfigId) filtered to non-terminal
        // statuses, plus the in-process IsIssueBeingProcessed check at dispatch time.
        var issueMetadata = await _facade.GetWorkItemIssueMetadataAsync(jobId, ct);

        // Best-effort label correction after recovery (label is currently agent:next from RevertFailedDistributionAsync)
        if (workItemStatus == WorkItemStatus.Succeeded)
        {
            await TrySwapLabelAfterOrphanedRecoveryAsync(jobId, issueMetadata, ct);
        }
    }

    private Task TrySwapLabelAfterOrphanedRecoveryAsync(
        JobId jobId,
        (string IssueIdentifier, string IssueProviderConfigId)? metadata,
        CancellationToken ct)
    {
        if (!metadata.HasValue) return Task.CompletedTask;
        return _labelService.TrySwapLabelAsync(
            metadata.Value.IssueProviderConfigId, metadata.Value.IssueIdentifier,
            AgentLabels.Done, LabelTargetKind.Issue,
            _logger, $"AgentJobLifecycleService.TrySwapLabelAfterOrphanedRecovery (job {jobId.Value})",
            ct);
    }

    private async Task PostCompletionBookkeepingAsync(JobId jobId, PipelineRun run, JobCompletionPayload payload, bool skipLabelSwap, CancellationToken ct)
    {
        // Outbox enqueue must happen BEFORE creating the linked CTS (survives ApplicationStopping).
        var outboxEntryId = await EnqueueFeedbackOutboxEntryAsync(jobId, run);

        // Link the caller's token with the host's ApplicationStopping token.
        // Note: cts is disposed after this method returns. Both awaited call sites pass cts.Token
        // directly and do not store it beyond their own await scope, so disposal is safe here.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _appLifetime.ApplicationStopping);

        await SwapLabelAndPostCommentAsync(jobId, run, payload, outboxEntryId, skipLabelSwap, cts.Token);
    }

    /// <summary>
    /// Enqueues a durable feedback comment outbox entry (best-effort, CancellationToken.None).
    /// Returns the entry ID on success, or <see cref="Guid.Empty"/> if enqueue was skipped or failed.
    /// </summary>
    /// <remarks>
    /// Scope: closes the "pod dies during bookkeeping" window. Does NOT close the "ReportJobCompleted
    /// was rejected / never invoked (reconnect race)" window — PostCompletionBookkeepingAsync is
    /// never reached in that case. Fully closing it would require enqueuing on the durable
    /// HTTP-primary completion path (WorkItemEndpoints), where run.Feedback is unavailable; deferred.
    /// Guard mirrors FeedbackCommentFormatter.FormatComment: only enqueue when Description is non-null
    /// (a null Description would produce no comment body and create an un-deliverable outbox row).
    /// Uses CancellationToken.None so this DB write survives ApplicationStopping — that's the entire
    /// point of the outbox: to persist comments that the cancellable fast path drops.
    /// </remarks>
    private async Task<Guid> EnqueueFeedbackOutboxEntryAsync(JobId jobId, PipelineRun run)
    {
        if (run.Feedback?.Issue?.Description is null)
            return Guid.Empty;

        var entry = BuildOutboxEntry(run);
        // Enqueue is best-effort: a transient DB failure must not abort the label swap below.
        // If the enqueue fails, the comment is not durable for this run (same behaviour as before
        // the outbox was introduced), but the label swap proceeds normally.
        try
        {
            await _outbox.EnqueueAsync(entry, CancellationToken.None);
            return entry.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex,
                "Job {JobId} failed to enqueue feedback comment outbox row (run={RunId}) — " +
                "comment durability is degraded for this run but label swap will proceed",
                jobId.Value, entry.RunId);
            return Guid.Empty; // don't attempt MarkCompleted if enqueue failed
        }
    }

    /// <summary>
    /// Swaps the issue label and posts the feedback comment for a completed job.
    /// Catches <see cref="OperationCanceledException"/> (graceful shutdown / connection abort) —
    /// OrphanedLabelRecoveryService will correct any stuck label on its next sweep.
    /// </summary>
    /// <param name="skipLabelSwap">
    /// When <c>true</c>, the label swap is skipped entirely. Used when the run was already terminated
    /// by another path (HTTP <c>Failed</c> POST) that set the authoritative label; this path must not
    /// overwrite it. The outbox row and the feedback comment are still posted regardless.
    /// </param>
    private async Task SwapLabelAndPostCommentAsync(JobId jobId, PipelineRun run, JobCompletionPayload payload, Guid outboxEntryId, bool skipLabelSwap, CancellationToken ct)
    {
        // Swap label based on final outcome (non-fatal).
        // The agent may also attempt a label swap via RequestLabelChange during its own
        // error handling, but that call can race with this handler (run already removed).
        // This is the authoritative swap that guarantees correctness — unless skipLabelSwap
        // is true, in which case the HTTP Failed path already set the authoritative label.
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

        try
        {
            if (label is not null && !skipLabelSwap)
            {
                _logger.Information(
                    "Job {JobId} ReportJobCompleted swapping label to {Label} for issue {IssueIdentifier} (finalStep={FinalStep}, finalLabel={FinalLabel})",
                    jobId.Value, label, run.IssueIdentifier, payload.FinalStep, payload.FinalLabel ?? "null");
                var swLabel = Stopwatch.StartNew();
                await _issueOps.SwapLabelAsync(run, label, ct);
                _logger.Information("Job {JobId} SwapLabelAsync completed in {ElapsedMs}ms", jobId.Value, swLabel.ElapsedMilliseconds);
            }
            else if (skipLabelSwap)
            {
                // TODO: [WARNING] skipLabelSwap=true only suppresses the second swap when the run was
                // already terminated by another path that caused CompleteRunAsync to return null
                // (runWasAlive=false in RegularJobCompletionStrategy). The "no intermediate agent:error"
                // guarantee for the same-replica ordering (hub path wins the race before the HTTP Failed
                // POST) depends on the hub payload carrying the same FinalLabel (agent:needs-refinement)
                // as the HTTP path, not on this skip logic. That invariant holds today because
                // HttpPrimaryCompletionReporter sends the identical JobCompletionPayload over both
                // channels, but if the two payloads ever diverge the same-replica NR outcome would
                // incorrectly use the hub payload's FinalLabel rather than the HTTP path's resolved label.
                _logger.Information(
                    "Job {JobId} ReportJobCompleted skipping label swap — run was already terminated by HTTP path (issue {IssueIdentifier})",
                    jobId.Value, run.IssueIdentifier);
            }

            // Post issue feedback comment if present (non-fatal)
            var swComment = Stopwatch.StartNew();
            await _issueOps.PostIssueFeedbackCommentAsync(run, ct);
            _logger.Information("Job {JobId} PostIssueFeedbackCommentAsync completed in {ElapsedMs}ms", jobId.Value, swComment.ElapsedMilliseconds);

            // Inline fast-path succeeded — mark the outbox row Completed so the relay skips it.
            // Best-effort: a transient DB failure here does not undo the committed label swap and
            // comment post. The outbox row stays Pending; FeedbackCommentRelayService will redeliver.
            if (outboxEntryId != Guid.Empty)
            {
                try
                {
                    await _outbox.MarkCompletedAsync(outboxEntryId, CancellationToken.None);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex,
                        "Failed to mark outbox entry {OutboxEntryId} completed — row will be redelivered",
                        outboxEntryId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown or connection abort — bookkeeping aborted cleanly.
            // OrphanedLabelRecoveryService will correct any stuck agent:in-progress label
            // on its next sweep (default interval: ~30 min).
            // FeedbackCommentRelayService will deliver the feedback comment on its next sweep —
            // the outbox row was enqueued with CancellationToken.None before cts was created.
            _logger.Information(
                "PostCompletionBookkeepingAsync cancelled for job {JobId} — OrphanedLabelRecoveryService will handle label cleanup, FeedbackCommentRelayService will deliver the feedback comment",
                jobId.Value);
        }
    }

    private static FeedbackCommentOutboxEntry BuildOutboxEntry(PipelineRun run) =>
        new()
        {
            Id = Guid.NewGuid(),
            RunId = run.RunId,
            IssueProviderConfigId = run.IssueProviderConfigId,
            IssueIdentifier = run.IssueIdentifier,
            RepoProviderConfigId = run.RepoProviderConfigId,
            PullRequestNumber = run.PullRequestNumber,
            FeedbackJson = System.Text.Json.JsonSerializer.Serialize(
                run.Feedback!.Issue,
                PipelineJsonOptions.Default)
        };

    /// <inheritdoc />
    public void HandleStepTransition(JobId jobId, PipelineStep step, DateTimeOffset timestamp, Dictionary<string, string>? metadata)
    {
        var run = _facade.GetRun(jobId);
        if (run is not null)
        {
            var previousStep = run.CurrentStep;
            run.CurrentStep = step;
            var clampedTimestamp = timestamp <= DateTimeOffset.UtcNow
                ? timestamp
                : DateTimeOffset.UtcNow;
            run.LastStepChangeAt = clampedTimestamp;

            // Persist progress to DB for cross-replica timeout enforcement (throttled)
            _ = _facade.TouchLastProgressAsync(jobId, clampedTimestamp, CancellationToken.None);

            // Update HighWaterMark — only advance, never go backward
            // Uses StepOrder.GetOrder (logical execution order) — NOT enum ordinals.
            // Terminal states (Failed, Cancelled) return -1 and are excluded.
            if (step is not (PipelineStep.Failed or PipelineStep.Cancelled)
                && StepOrder.GetOrder(step) > StepOrder.GetOrder(run.HighWaterMark))
                run.HighWaterMark = step;

            // Apply step metadata from the agent (carries data from the just-completed step)
            if (metadata is { Count: > 0 })
                StepMetadataApplier.Apply(run, metadata);

            // Persist mutated run back to the store (no-op for in-memory; required for Redis).
            _facade.ReplaceRun(run);

            _logger.Information("Job {JobId} step transition {Previous} → {Step}",
                jobId.Value, previousStep, step);
            _changeNotifier.NotifyChange();
        }
    }
}
