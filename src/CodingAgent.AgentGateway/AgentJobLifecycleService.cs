using System.Diagnostics;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.Extensions.Hosting;
using ILogger = Serilog.ILogger;

namespace CodingAgent.AgentGateway;

/// <summary>
/// Encapsulates job-lifecycle business logic extracted from AgentHub.Pipeline.cs.
/// Handles job acceptance, rejection, completion, and step transitions.
/// The hub delegates to this service after resolving SignalR-specific context.
/// </summary>
public sealed class AgentJobLifecycleService : IAgentJobLifecycleService
{
    private const string FieldActiveJobId = "activeJobId";
    private const string FieldLastJobCompletedAt = "lastJobCompletedAt";
    private const string FieldOrphanRestoredAt = "orphanRestoredAt";

    private readonly IAgentHubFacade _facade;
    private readonly ILabelService _labelService;
    private readonly IHubIssueOperations _issueOps;
    private readonly IChangeNotifier _changeNotifier;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly IFeedbackCommentOutbox _outbox;
    private readonly ILogger _logger;

    private readonly IJobCompletionStrategy _regularStrategy;
    private readonly IJobCompletionStrategy _consolidationStrategy;

    public AgentJobLifecycleService( // NOSONAR S107 — 8 params; grouped into a record would require callers to change
        IAgentHubFacade facade,
        IRunLifecycleManager lifecycleManager,
        ILabelService labelService,
        IHubIssueOperations issueOps,
        IChangeNotifier changeNotifier,
        IHostApplicationLifetime appLifetime,
        IFeedbackCommentOutbox outbox,
        ILogger logger)
    {
        _facade = facade;
        _labelService = labelService;
        _issueOps = issueOps;
        _changeNotifier = changeNotifier;
        _appLifetime = appLifetime;
        _outbox = outbox;
        _logger = logger;

        _regularStrategy = new RegularJobCompletionStrategy(facade, lifecycleManager, changeNotifier, logger);
        _consolidationStrategy = new ConsolidationJobCompletionStrategy(facade, changeNotifier, logger);
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
        _facade.UpdateAgentFieldFireAndForget(agent.AgentId, FieldActiveJobId, null, _logger, "ResetAgentToIdle");
        _facade.UpdateAgentFieldFireAndForget(agent.AgentId, FieldLastJobCompletedAt, now.ToString("O"), _logger, "ResetAgentToIdle");
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

        if (run is not null)
        {
            // Select strategy based on run type and execute run-type-specific completion logic.
            // Neither strategy touches agent state — that is this method's responsibility.
            IJobCompletionStrategy strategy = run.IssueProviderConfigId == ConsolidationConstants.ProviderConfigId
                ? _consolidationStrategy
                : _regularStrategy;

            await strategy.ExecuteAsync(jobId, run, payload, activity, ct);
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
        if (agent is not null)
        {
            agent.ActiveJobId = null;
            agent.OrphanRestoredAt = null;
            agent.LastJobCompletedAt = DateTimeOffset.UtcNow;
            _facade.UpdateAgentFieldFireAndForget(agent.AgentId, FieldActiveJobId, null, _logger, "HandleJobCompletedAsync");
            _facade.UpdateAgentFieldFireAndForget(agent.AgentId, FieldOrphanRestoredAt, null, _logger, "HandleJobCompletedAsync");
            _facade.UpdateAgentFieldFireAndForget(agent.AgentId, FieldLastJobCompletedAt, DateTimeOffset.UtcNow.ToString("O"), _logger, "HandleJobCompletedAsync");
            _facade.TransitionStatus(agent.AgentId, AgentStatus.Idle);
        }
        else if (run?.AgentId is not null)
        {
            // Fallback: agent lookup returned null (connection dropped, hash expired) but we know the
            // AgentId from the run. Attempt to clear agent state to prevent it from being locked in
            // Busy indefinitely until ReconciliationService.EnforceTimeoutsAsync times it out.
            var agentId = new AgentId(run.AgentId);
            _facade.UpdateAgentFieldFireAndForget(agentId, FieldActiveJobId, null, _logger, "HandleJobCompletedAsync (run fallback path)");
            _facade.TransitionStatus(agentId, AgentStatus.Idle);
            _logger.Warning(
                "HandleJobCompletedAsync: agent lookup returned null for job {JobId} (agentId={AgentId}) — clearing state via run fallback to prevent Busy lock",
                jobId.Value, run.AgentId);
        }

        // Non-fatal post-completion bookkeeping: label swap and feedback comment.
        // These may involve external API calls and can be slow — executed after agent
        // is already marked Idle so it doesn't block availability.
        // Note: These run inline (not fire-and-forget) to maintain testability and ensure
        // label swaps complete before the hub method returns. The agent is already Idle
        // in the registry, so the dispatcher can assign it work via the periodic drain sweep.
        // Consolidation runs skip bookkeeping — they have no associated issue labels or feedback comments.
        if (run is not null && run.IssueProviderConfigId != ConsolidationConstants.ProviderConfigId)
        {
            await PostCompletionBookkeepingAsync(jobId, run, payload, ct);
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

    private async Task PostCompletionBookkeepingAsync(JobId jobId, PipelineRun run, JobCompletionPayload payload, CancellationToken ct)
    {
        // ── Durable outbox enqueue (must happen BEFORE cts is created) ──────────────────
        // Scope: this outbox closes the "pod dies during bookkeeping" window (see prior :395 note).
        // It does NOT close the "ReportJobCompleted was rejected / never invoked (reconnect race)"
        // window — there the enqueue below never runs because PostCompletionBookkeepingAsync is
        // never reached. That window is reduced by Part A's reconnect-gate but not fully closed
        // by Part B. Fully closing it would require enqueuing on the durable HTTP-primary completion
        // path (WorkItemEndpoints), where run.Feedback is unavailable; deferred as a follow-up.
        //
        // Guard mirrors FeedbackCommentFormatter.FormatComment: only enqueue when
        // Description is non-null (a null Description would produce no comment body and
        // create an un-deliverable row that exhausts maxAttempts without ever posting).
        // Using CancellationToken.None explicitly so this DB write survives ApplicationStopping —
        // the entire point of the outbox is to persist comments that the cancellable fast path drops.
        Guid outboxEntryId = Guid.Empty;
        if (run.Feedback?.Issue?.Description is not null)
        {
            var entry = BuildOutboxEntry(run);
            outboxEntryId = entry.Id;
            // Enqueue is best-effort: a transient DB failure must not abort the label swap below.
            // Before this outbox was introduced, PostCompletionBookkeepingAsync performed no DB
            // writes, so a Postgres blip could not bypass SwapLabelAsync. We preserve that guarantee
            // by catching and logging any exception from EnqueueAsync rather than propagating it.
            // If the enqueue fails, the comment is not durable for this run (same behaviour as before
            // the outbox was introduced), but the label swap proceeds normally.
            try
            {
                await _outbox.EnqueueAsync(entry, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex,
                    "Job {JobId} failed to enqueue feedback comment outbox row (run={RunId}) — " +
                    "comment durability is degraded for this run but label swap will proceed",
                    jobId.Value, entry.RunId);
                outboxEntryId = Guid.Empty; // don't attempt MarkCompleted if enqueue failed
            }
        }

        // Link the caller's token with the host's ApplicationStopping token.
        // This ensures bookkeeping is aborted on graceful pod shutdown even when the hub
        // calls in with CancellationToken.None (the caller-supplied token is not yet meaningful).
        // When the hub call site passes Context.ConnectionAborted instead of CancellationToken.None,
        // the ct leg of the linked source will become meaningful (connection-abort cancellation).
        // Currently only _appLifetime.ApplicationStopping is an effective cancellation source here.
        // Note: cts is disposed after PostCompletionBookkeepingAsync returns. Both awaited call sites
        // (SwapLabelAsync and PostIssueFeedbackCommentAsync) pass cts.Token directly and do not store
        // it beyond their own await scope, so disposal is safe in the current implementation.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _appLifetime.ApplicationStopping);

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

        try
        {
            if (label is not null)
            {
                _logger.Information(
                    "Job {JobId} ReportJobCompleted swapping label to {Label} for issue {IssueIdentifier} (finalStep={FinalStep}, finalLabel={FinalLabel})",
                    jobId.Value, label, run.IssueIdentifier, payload.FinalStep, payload.FinalLabel ?? "null");
                var swLabel = Stopwatch.StartNew();
                await _issueOps.SwapLabelAsync(run, label, cts.Token);
                _logger.Information("Job {JobId} SwapLabelAsync completed in {ElapsedMs}ms", jobId.Value, swLabel.ElapsedMilliseconds);
            }

            // Post issue feedback comment if present (non-fatal)
            var swComment = Stopwatch.StartNew();
            await _issueOps.PostIssueFeedbackCommentAsync(run, cts.Token);
            _logger.Information("Job {JobId} PostIssueFeedbackCommentAsync completed in {ElapsedMs}ms", jobId.Value, swComment.ElapsedMilliseconds);

            // Inline fast-path succeeded — mark the outbox row Completed so the relay skips it.
            // This is best-effort: a transient DB failure here does not undo the already-committed
            // label swap and comment post. The outbox row remains Pending and FeedbackCommentRelayService
            // will redeliver the comment (at-least-once). Failing the hub call for this is wrong.
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
            // the outbox row was enqueued above with CancellationToken.None before cts was created.
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
                ApplyStepMetadata(run, metadata);

            // Persist mutated run back to the store (no-op for in-memory; required for Redis).
            _facade.ReplaceRun(run);

            _logger.Information("Job {JobId} step transition {Previous} → {Step}",
                jobId.Value, previousStep, step);
            _changeNotifier.NotifyChange();
        }
    }

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
                    run.BaselineHealthPassed = TryParseBool(value);
                    break;
                case "AnalysisSkipped":
                    run.AnalysisSkipped = TryParseBool(value) == true;
                    break;
                case "FilesChangedCount":
                    run.FilesChangedCount = TryParseInt(value) ?? run.FilesChangedCount;
                    break;
                case "LinesAdded":
                    run.LinesAdded = TryParseInt(value) ?? run.LinesAdded;
                    break;
                case "LinesRemoved":
                    run.LinesRemoved = TryParseInt(value) ?? run.LinesRemoved;
                    break;
                case "CodeReviewIterationsCompleted":
                    run.CodeReviewIterationsCompleted = TryParseInt(value) ?? run.CodeReviewIterationsCompleted;
                    break;
                case "CodeReviewIterationsTotal":
                    run.CodeReviewIterationsTotal = TryParseInt(value) ?? run.CodeReviewIterationsTotal;
                    break;
                case "CodeReviewIterationInProgress":
                    run.CodeReviewIterationInProgress = TryParseInt(value) ?? run.CodeReviewIterationInProgress;
                    break;
                case "OpenIssuesDownloaded":
                    run.OpenIssuesDownloaded = TryParseInt(value) ?? run.OpenIssuesDownloaded;
                    break;
                case "DecompositionSubIssuesCreated":
                    run.DecompositionSubIssuesCreated = TryParseInt(value) ?? run.DecompositionSubIssuesCreated;
                    break;
                case "DecompositionSubIssuesAttempted":
                    run.DecompositionSubIssuesAttempted = TryParseInt(value) ?? run.DecompositionSubIssuesAttempted;
                    break;
                case "RetryCount":
                    run.RetryCount = TryParseInt(value) ?? run.RetryCount;
                    break;
                case "InfrastructureRetryCount":
                    run.InfrastructureRetryCount = TryParseInt(value) ?? run.InfrastructureRetryCount;
                    break;
                case "TotalTokens":
                    run.TotalTokens = TryParseLong(value) ?? run.TotalTokens;
                    break;
                case "TotalCost":
                    run.TotalCost = TryParseDecimalInvariant(value) ?? run.TotalCost;
                    break;
                case "CodeReviewCriticalCount":
                    pendingCritical = TryParseInt(value);
                    break;
                case "CodeReviewWarningCount":
                    pendingWarning = TryParseInt(value);
                    break;
                case "CodeReviewSuggestionCount":
                    pendingSuggestion = TryParseInt(value);
                    break;
                case "CodeReviewAgentsRun":
                    run.CodeReviewAgentsRun = value.Split('\x1F', StringSplitOptions.RemoveEmptyEntries);
                    break;
                case "PullRequestUrl":
                    if (!string.IsNullOrEmpty(value))
                        run.PullRequestUrl = value;
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

    private static int? TryParseInt(string value) =>
        int.TryParse(value, out var n) ? n : null;

    private static long? TryParseLong(string value) =>
        long.TryParse(value, out var n) ? n : null;

    private static bool? TryParseBool(string value) =>
        bool.TryParse(value, out var b) ? b : null;

    private static decimal? TryParseDecimalInvariant(string value) =>
        decimal.TryParse(value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
}
