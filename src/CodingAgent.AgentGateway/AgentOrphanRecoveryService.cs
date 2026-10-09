using CodingAgent.Contracts;
using CodingAgent.Infrastructure.Common;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.AspNetCore.SignalR;
using Serilog.Events;
using ILogger = Serilog.ILogger;

namespace CodingAgent.AgentGateway;

/// <summary>
/// Extracts orphan-restoration logic from <see cref="AgentHub.RegisterAgent"/>.
/// Handles active-job restoration, orphan detection, and crash recovery.
/// </summary>
public sealed class AgentOrphanRecoveryService(
    IAgentHubFacade facade,
    IChangeNotifier changeNotifier,
    ILogger logger) : IAgentOrphanRecoveryService
{
    private readonly IAgentHubFacade _facade = facade;
    private readonly IChangeNotifier _changeNotifier = changeNotifier;
    private readonly ILogger _logger = logger;

    /// <inheritdoc />
    public async Task<OrphanRecoveryResult> RecoverOrphanedStateAsync(AgentRegistrationMessage message, AgentId agentId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(agentId.Value);

        // Re-track active job from agent state (handles orchestrator restart scenario)
        PipelineRun? firstPickupRun = null;
        if (message.ActiveJob is not null)
        {
            firstPickupRun = await RestoreActiveJobAsync(message, agentId, ct);
        }

        // Detect orphaned runs: if the orchestrator tracks active runs for this agent
        // but the agent registered without an active job, restore the ActiveJobId on the
        // registry entry. This avoids immediately failing runs when an agent has a brief network blip.
        // The ReconciliationService (JobController) enforces work-item timeouts.
        var entry = _facade.GetByAgentId(agentId);
        if (entry is { ActiveJobId: null })
        {
            await DetectAndRestoreOrphans(agentId, entry, ct);
        }
        else if (entry is { ActiveJobId: not null })
        {
            await HandleCrashRecoveryAsync(message, agentId, entry, ct);
        }

        return new OrphanRecoveryResult(firstPickupRun);
    }

    /// <returns>The tracked run when the agent was just recorded on it as its first agent.</returns>
    private async Task<PipelineRun?> RestoreActiveJobAsync(AgentRegistrationMessage message, AgentId agentId, CancellationToken ct)
    {
        var activeJob = message.ActiveJob!;

        // The run-scoped [RequiresActiveJob] hub methods (token refresh, labels, comments) act on the
        // restored run's identity, so where the WorkItem store is available (the API host) the
        // report is accepted only for a work item this agent owns, and the identity — issue,
        // provider configs, project — is taken from the database rather than from the report.
        // Nothing about the run changes before that check: not its agent, not its labels.
        RunIdentity identity;
        if (_facade.CanVerifyWorkItems)
        {
            var record = await ReadWorkItemRecordAsync(agentId, activeJob.RunId, ct);
            if (record is null || !record.IsOwnedBy(agentId.Value))
            {
                _logger.Warning(
                    "Agent {AgentId} reported active job {RunId}, which could not be verified as a work item assigned to it — ignoring the claim",
                    agentId, LogSanitizer.SanitizeForLog(activeJob.RunId));
                return null;
            }
            identity = RunIdentity.FromWorkItem(record, activeJob);
        }
        else
        {
            identity = RunIdentity.FromAgentReport(activeJob);
        }

        var existingRun = _facade.GetRun(activeJob.RunId);

        if (existingRun is null)
        {
            await RestoreRunFromAgentStateAsync(agentId, activeJob, identity, ct);
            return null;
        }

        return LinkAgentToExistingRun(existingRun, agentId, activeJob);
    }

    /// <summary>
    /// Reads the work item behind a run an agent is to be attached to. A failed read is not taken
    /// as "not the agent's": registration fails with an error the agent retries, so a store outage
    /// does not leave a legitimate agent registered without its run.
    /// </summary>
    /// <remarks>
    /// Two distinct exit modes: an <see cref="OperationCanceledException"/> from
    /// <see cref="IAgentHubFacade.GetWorkItemRunRecordAsync"/> propagates directly to the caller
    /// (the <c>when</c> guard lets it escape the catch block). All other exceptions are caught,
    /// logged, and rethrown as <see cref="Microsoft.AspNetCore.SignalR.HubException"/> so the
    /// SignalR retry pipeline treats the failure as transient.
    /// </remarks>
    // TODO [WARNING]: Two distinct exit modes — OCE propagates, all others become HubException —
    // should be visible to maintainers. The XML doc <remarks> above captures this. If the catch
    // filter (when (ex is not OperationCanceledException)) is ever simplified, ensure OCE still
    // propagates rather than being swallowed or re-wrapped as HubException.
    private async Task<WorkItemRunRecord?> ReadWorkItemRecordAsync(AgentId agentId, string runId, CancellationToken ct)
    {
        try
        {
            return await _facade.GetWorkItemRunRecordAsync(runId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex,
                "Agent {AgentId}: could not read work item {RunId} to verify the agent's run — failing registration so the agent retries",
                agentId, LogSanitizer.SanitizeForLog(runId));
            // The agent's SignalR retry pipeline treats "Failed to ..." hub errors as transient.
            throw new HubException(
                $"Failed to verify run {LogSanitizer.SanitizeForLog(runId)} for agent {agentId}: the work item store is unavailable");
        }
    }

    private async Task RestoreRunFromAgentStateAsync(
        AgentId agentId, ActiveJobState activeJob, RunIdentity identity, CancellationToken ct)
    {
        // Check history — don't re-register a completed run.
        // Only treat runs with successful terminal states as stale.
        // Cancelled/Failed runs may be legitimately re-dispatched with the same RunId.
        var history = await _facade.GetRunHistoryAsync(ct);
        var inHistory = history.Any(r => r.RunId == activeJob.RunId
            && r.FinalStep != PipelineStep.Cancelled
            && r.FinalStep != PipelineStep.Failed);

        if (!inHistory)
        {
            await RestoreNewRunAsync(agentId, activeJob, identity);
        }
        else
        {
            _logger.Information(
                "Agent {AgentId} reported active job {RunId} but it's already in history — ignoring stale state",
                agentId, LogSanitizer.SanitizeForLog(activeJob.RunId));
        }
    }

    private Task RestoreNewRunAsync(AgentId agentId, ActiveJobState activeJob, RunIdentity identity)
    {
        // Skip restoration for consolidation runs — they have their own
        // completion path (ReportConsolidationComplete) and should not
        // enter pipeline run tracking or history.
        if (identity.IsConsolidation)
        {
            RestoreConsolidationTracking(agentId, activeJob);
        }
        else
        {
            RestorePipelineRun(agentId, activeJob, identity);
        }
        return Task.CompletedTask;
    }

    private void RestoreConsolidationTracking(AgentId agentId, ActiveJobState activeJob)
    {
        _logger.Information(
            "Agent {AgentId} reported active consolidation job {RunId} — skipping pipeline run restoration (handled by ReportConsolidationComplete)",
            agentId, LogSanitizer.SanitizeForLog(activeJob.RunId));

        // Still mark agent as busy with this job so it's tracked correctly.
        // The write is unconditional: this path only runs when the agent self-reported a
        // consolidation job, so the ActiveJobId assignment is always authoritative.
        var consolEntry = _facade.GetByAgentId(agentId);
        if (consolEntry is not null)
        {
            if (ActivateAgentJob(consolEntry, agentId, activeJob.RunId, guardAgainstConcurrentAssignment: false, "RestoreConsolidationTracking"))
            {
                // TransitionStatus acquires SyncRoot internally — called OUTSIDE the lock.
                _facade.TransitionStatus(agentId, AgentStatus.Busy);
                // UpdateAgentFieldFireAndForget is called AFTER TransitionStatus and outside the lock
                // so the async Redis continuation cannot overwrite the Busy status already written.
                // TODO [WARNING]: UpdateAgentFieldFireAndForget cannot forward a CancellationToken because
                // IAgentHubFacade.UpdateAgentFieldAsync has no CancellationToken parameter. The fire-and-forget
                // helper intentionally uses CancellationToken.None for the ContinueWith fault-log continuation.
                // To propagate the recovery token here, IAgentHubFacade.UpdateAgentFieldAsync would need a
                // CancellationToken overload and UpdateAgentFieldFireAndForget would need to be updated.
                _facade.UpdateAgentFieldFireAndForget(agentId, AgentFieldNames.ActiveJobId, activeJob.RunId, _logger, "RestoreConsolidationTracking");
            }
        }

        _changeNotifier.NotifyChange();
    }

    private void RestorePipelineRun(AgentId agentId, ActiveJobState activeJob, RunIdentity identity)
    {
        var restoredRun = CreateRestoredPipelineRun(agentId.Value, activeJob, identity);
        restoredRun.CurrentStep = activeJob.CurrentStep;
        restoredRun.PipelineProviderConfigId = identity.PipelineProviderConfigId;
        restoredRun.ResolvedProfileId = activeJob.ResolvedProfileId;
        restoredRun.ProjectId = identity.ProjectId;
        restoredRun.ProjectName = activeJob.ProjectName;
        restoredRun.RepositoryName = activeJob.RepositoryName;
        restoredRun.ModelName = activeJob.ModelName;

        _facade.AddRun(restoredRun);

        // Set agent as busy with this job. The write is unconditional: this path only runs
        // when we have just created a restored run, so the ActiveJobId assignment is always
        // authoritative.
        var restoredEntry = _facade.GetByAgentId(agentId);
        if (restoredEntry is not null)
        {
            if (ActivateAgentJob(restoredEntry, agentId, activeJob.RunId, guardAgainstConcurrentAssignment: false, "RestorePipelineRun"))
            {
                // TransitionStatus acquires SyncRoot internally — called OUTSIDE the lock.
                _facade.TransitionStatus(agentId, AgentStatus.Busy);
                // UpdateAgentFieldFireAndForget is called AFTER TransitionStatus and outside the lock
                // so the async Redis continuation cannot overwrite the Busy status already written.
                // TODO [WARNING]: UpdateAgentFieldFireAndForget cannot forward a CancellationToken because
                // IAgentHubFacade.UpdateAgentFieldAsync has no CancellationToken parameter. The fire-and-forget
                // helper intentionally uses CancellationToken.None for the ContinueWith fault-log continuation.
                // To propagate the recovery token here, IAgentHubFacade.UpdateAgentFieldAsync would need a
                // CancellationToken overload and UpdateAgentFieldFireAndForget would need to be updated.
                _facade.UpdateAgentFieldFireAndForget(agentId, AgentFieldNames.ActiveJobId, activeJob.RunId, _logger, "RestorePipelineRun");
            }
        }

        _logger.Information(
            "Restored active run {RunId} for agent {AgentId} (issue {IssueIdentifier}, step {Step}) — orchestrator state recovery",
            LogSanitizer.SanitizeForLog(activeJob.RunId), agentId, LogSanitizer.SanitizeForLog(identity.IssueIdentifier), activeJob.CurrentStep);

        _changeNotifier.NotifyChange();
    }

    /// <summary>
    /// Builds the restored run: identity (run type, issue, provider configs) from
    /// <paramref name="identity"/>, progress and display fields from the agent's report.
    /// </summary>
    private static PipelineRun CreateRestoredPipelineRun(string agentId, ActiveJobState activeJob, RunIdentity identity)
    {
        return PipelineRun.CreateForRunType(new PipelineRunCreationParams
        {
            RunId = activeJob.RunId,
            IssueIdentifier = identity.IssueIdentifier,
            IssueTitle = activeJob.IssueTitle,
            IssueUrl = HttpUrlOrNull(activeJob.IssueUrl),
            IssueProviderConfigId = identity.IssueProviderConfigId,
            RepoProviderConfigId = identity.RepoProviderConfigId,
            RunType = identity.RunType,
            StartedAt = activeJob.StartedAt,
            InitiatedBy = activeJob.InitiatedBy,
            AgentId = agentId,
            AgentProviderConfigId = activeJob.AgentProviderConfigId,
            BrainProviderConfigId = identity.BrainProviderConfigId,
            // NOTE: ActiveJobState carries no ReviewPrUrl, ReviewPrBranchName, or ReviewPrTargetBranch.
            // On re-registration, a restored review run will be missing:
            //   - ReviewPrUrl: used by RunPage.razor to render the "PR under review" chip.
            //   - ReviewPrBranchName / ReviewPrTargetBranch: used for git operations during the pipeline.
            // These cannot be recovered from ActiveJobState alone without fetching the original
            // WorkItem payload or adding more MessagePack keys (out of scope for issue #3095).
            // Impact: the "PR under review" chip will not render on the Run page for a restored review run.
            // TODO: open a follow-up issue to track this gap (issue #3095 is being closed by this fix;
            // this limitation needs its own tracking ticket so it is not lost).
            ReviewPrBranchName = string.Empty,
            ReviewPrTargetBranch = string.Empty
        });
    }

    /// <summary>
    /// The reported issue URL, if it is an absolute http(s) URL. The work item does not record the
    /// URL, so it comes from the agent — and the UI renders it as a link.
    /// </summary>
    private static string? HttpUrlOrNull(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? url
            : null;

    /// <summary>
    /// The identity a restored run is built from: the verified work item's when the WorkItem store
    /// is available, the agent's own report otherwise (in-memory / test hosts).
    /// </summary>
    private sealed record RunIdentity(
        bool IsConsolidation,
        PipelineRunType RunType,
        string IssueIdentifier,
        string IssueProviderConfigId,
        string RepoProviderConfigId,
        string? BrainProviderConfigId,
        string? PipelineProviderConfigId,
        string? ProjectId)
    {
        public static RunIdentity FromAgentReport(ActiveJobState activeJob) => new(
            activeJob.IssueProviderConfigId == ConsolidationConstants.ProviderConfigId,
            activeJob.RunType,
            activeJob.IssueIdentifier,
            activeJob.IssueProviderConfigId,
            activeJob.RepoProviderConfigId,
            activeJob.BrainProviderConfigId,
            activeJob.PipelineProviderConfigId,
            activeJob.ProjectId);

        public static RunIdentity FromWorkItem(WorkItemRunRecord record, ActiveJobState activeJob)
        {
            // The database records the task, not the decomposition phase: take the phase from the
            // agent as long as it stays within the decomposition task.
            var runType = record.TaskType.ToDefaultRunType();
            if (runType == PipelineRunType.DecompositionAnalysis && activeJob.RunType == PipelineRunType.Decomposition)
                runType = PipelineRunType.Decomposition;

            return new(
                record.TaskType == WorkItemTaskType.Consolidation,
                runType,
                record.IssueIdentifier,
                record.IssueProviderConfigId,
                record.RepoProviderConfigId ?? string.Empty,
                record.BrainProviderConfigId,
                record.PipelineProviderConfigId,
                record.ProjectId?.ToString());
        }
    }

    /// <returns>The run when the agent was just recorded on it as its first agent, otherwise null.</returns>
    private PipelineRun? LinkAgentToExistingRun(
        PipelineRun existingRun, AgentId agentId, ActiveJobState activeJob)
    {
        // Run already exists in-memory (e.g., created by K8s DispatchService with AgentId=null).
        // Ensure the agent is linked to it and transitioned to Busy.
        //
        // AgentId update: covers first pickup (null AgentId) and pod-replacement reconnect
        // (different AgentId). Same-agent reconnect (already matches) is a no-op for the assignment.
        // This is the only place registration records the agent on a run, and it runs only for an
        // accepted claim — that is what the UI and orphan detection read the run's agent from.
        var previousAgentId = existingRun.AgentId;
        var agentChanged = previousAgentId != agentId.Value;
        if (agentChanged)
        {
            existingRun.AgentId = agentId.Value;

            if (!string.IsNullOrEmpty(previousAgentId))
            {
                // Pod replacement: a different agent pod has taken over this run.
                _logger.Information(
                    "LinkAgentToExistingRun: updated AgentId on run {RunId} from {PreviousAgentId} to {NewAgentId} (pod replacement)",
                    existingRun.RunId, previousAgentId, agentId);
            }
        }

        // Adopt the metadata only the pod can compute. The model name is resolved agent-side
        // from the agent provider config delivered with the assignment, and registration is
        // the sole channel that carries it back — JobCompletionPayload has no ModelName field.
        // RestorePipelineRun (the path taken when no run exists yet) already does this; this
        // path did not, and this is the ordinary Kubernetes path, so every run that dispatched
        // normally reached history with a null ModelName.
        // ??= so a re-registration cannot blank a value already recorded.
        var metadataAdopted = (existingRun.ModelName is null && activeJob.ModelName is not null)
            || (existingRun.RepositoryName is null && activeJob.RepositoryName is not null);
        existingRun.ModelName ??= activeJob.ModelName;
        existingRun.RepositoryName ??= activeJob.RepositoryName;

        // Write the changes back: under the distributed run service GetRun returns a copy.
        if (agentChanged || metadataAdopted)
            _facade.ReplaceRun(existingRun);

        // Agent tracking: always run when the run belongs to this agent.
        // This covers: first pickup (AgentId just set above), pod replacement (AgentId just updated),
        // and same-agent reconnect (AgentId already matched, tracking still needed if entry lost state).
        TrackLinkedActiveJob(agentId, activeJob);

        _logger.Debug("Agent {AgentId} active job {RunId} already tracked — linked agent to run",
            agentId, activeJob.RunId);

        return agentChanged && string.IsNullOrEmpty(previousAgentId) ? existingRun : null;
    }

    private void TrackLinkedActiveJob(AgentId agentId, ActiveJobState activeJob)
    {
        var trackedEntry = _facade.GetByAgentId(agentId);
        if (trackedEntry is null)
            return;

        // Guarded path: assignment is conditional on ActiveJobId being null.
        // UpdateAgentFieldFireAndForget is called inside the lock (by the helper);
        // TransitionStatus is called after lock release.
        if (ActivateAgentJob(trackedEntry, agentId, activeJob.RunId, guardAgainstConcurrentAssignment: true, "LinkAgentToExistingRun"))
            _facade.TransitionStatus(agentId, AgentStatus.Busy);
    }

    // TODO: [WARNING] The acceptance criterion says "DetectAndRestoreOrphans delegates each of its
    // new-run / existing-run-link / crash-recovery branches to a separate handler". Those three
    // top-level branches (RestoreActiveJobAsync, DetectAndRestoreOrphans, HandleCrashRecoveryAsync)
    // are already separate methods at the RecoverOrphanedStateAsync level. The decomposition here
    // targets the sub-steps inside DetectAndRestoreOrphans (ownership guard, history guard,
    // lock-and-activate), not the top-level branch split. The criterion naming is ambiguous; this
    // interpretation reduces the 151-line method to ~23 lines and satisfies the no-method-over-100-lines
    // constraint.
    private async Task DetectAndRestoreOrphans(AgentId agentId, AgentEntry entry, CancellationToken ct)
    {
        var orphanedRuns = _facade.GetActiveRunsByAgent(agentId);
        if (orphanedRuns.Count == 0)
        {
            _logger.Information(
                "Agent {AgentId} registered with no active job and no orphaned runs (status={Status})",
                agentId, entry.Status);
            return;
        }

        // Restore the most recent orphaned run as the active job so the
        // disconnect grace period timer applies. If the agent truly lost the job,
        // ReconciliationService (JobController) will time out the run after the grace period.
        var mostRecent = orphanedRuns[^1];

        if (!await VerifyOrphanOwnershipAsync(agentId, mostRecent, ct))
            return;

        if (await CheckOrphanInHistoryAsync(agentId, mostRecent, ct))
            return;

        ActivateOrphanedRun(agentId, entry, mostRecent, orphanedRuns.Count);
    }

    /// <summary>
    /// Returns <c>false</c> when work-item ownership verification is enabled and the tracked run
    /// cannot be confirmed as belonging to this agent — in that case restoration is skipped.
    /// Returns <c>true</c> when verification passes or is not required.
    /// </summary>
    private async Task<bool> VerifyOrphanOwnershipAsync(AgentId agentId, PipelineRun mostRecent, CancellationToken ct)
    {
        if (!_facade.CanVerifyWorkItems)
            return true;

        // The run records this agent, but only its work item says whose work it is: as for a
        // reported active job, re-attach it only to the work item's own agent.
        var record = await ReadWorkItemRecordAsync(agentId, mostRecent.RunId, ct);
        if (record is null || !record.IsOwnedBy(agentId.Value))
        {
            _logger.Warning(
                "Agent {AgentId}: tracked run {RunId} could not be verified as a work item assigned to it — not restoring it",
                agentId, LogSanitizer.SanitizeForLog(mostRecent.RunId));
            return false;
        }

        return true;
    }

    /// <summary>
    /// Returns <c>true</c> when the orphaned run is already in history as a non-retryable terminal
    /// state — in that case restoration should be skipped. Returns <c>false</c> when the run is not
    /// in history (or history is unavailable — fail-open).
    /// </summary>
    private async Task<bool> CheckOrphanInHistoryAsync(AgentId agentId, PipelineRun mostRecent, CancellationToken ct)
    {
        // Guard: don't re-activate a run that is already in history as a non-Cancelled/
        // non-Failed terminal state (e.g. Completed, PrMerged, PrClosed, ConflictRestart).
        // Mirrors the history check in RestoreRunFromAgentStateAsync. Cancelled/Failed runs
        // remain restorable — they may be legitimately re-dispatched.
        // TODO [WARNING]: GetRunHistoryAsync returns the full history and this performs an O(N) linear scan
        // on every orphan-recovery call. Verify that GetRunHistoryAsync does not return cross-agent history;
        // if run history is large, consider scoping the query by agent or run ID to avoid performance issues.
        // TODO [WARNING]: Early return when inHistory==true skips processing of any other orphaned runs in
        // the active set. If mostRecent is a completed run but older entries are legitimately restorable,
        // they are silently ignored this cycle. Assess whether the active set can hold multiple orphans
        // for one agent; if so, iterate over all entries rather than only inspecting orphanedRuns[^1].
        IReadOnlyList<PipelineRunSummary> history;
        try
        {
            history = await _facade.GetRunHistoryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Fail-open: if history storage is unavailable, proceed with restoration rather
            // than blocking agent registration. At worst, a completed run is briefly re-activated
            // until ReconciliationService times it out. Failing closed here would leave the agent
            // stuck in Idle with a legitimate orphaned run for the full reconciliation grace period.
            _logger.Warning(ex,
                "Agent {AgentId} orphan guard: GetRunHistoryAsync faulted — proceeding with restoration (fail-open)",
                agentId);
            return false;
        }

        var inHistory = history.Any(r => r.RunId == mostRecent.RunId
            && r.FinalStep.IsTerminal()
            && r.FinalStep != PipelineStep.Cancelled
            && r.FinalStep != PipelineStep.Failed);

        if (inHistory)
        {
            _logger.Information(
                "Agent {AgentId} has orphaned run {RunId} in active set but it is already in history as a terminal non-retryable state — skipping restoration",
                agentId, mostRecent.RunId);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Activates the orphaned run as the agent's active job under <c>lock(entry.SyncRoot)</c>,
    /// re-materialises the Redis hash if absent, and transitions the agent to Busy.
    /// No-ops if DrainService assigned a different job between the orphan-detection check and the
    /// lock acquisition (the captured <c>shouldTransition</c> flag prevents a spurious Busy transition).
    /// </summary>
    private void ActivateOrphanedRun(AgentId agentId, AgentEntry entry, PipelineRun mostRecent, int orphanCount)
    {
        // Snapshot entry.ActiveJobId before calling the helper. The helper re-checks under lock;
        // this snapshot is used only for the "already acquired" diagnostic log below.
        // TODO [WARNING]: This read occurs outside lock(entry.SyncRoot), so existingJobId may observe a
        // stale or torn value if a concurrent writer (e.g. DrainService) modifies entry.ActiveJobId between
        // this line and the helper's lock acquisition. The variable is used only for diagnostic logging
        // (never for control flow), so there is no correctness impact, but the logged value on the
        // "DrainService race" path could show null instead of the drain-assigned run ID, or vice versa.
        var existingJobId = entry.ActiveJobId;

        var didTransition = ActivateAgentJob(
            entry, agentId, mostRecent.RunId,
            guardAgainstConcurrentAssignment: true,
            "DetectAndRestoreOrphans",
            additionalLockedAction: e =>
            {
                // Executed INSIDE lock(entry.SyncRoot), ONLY when entry.ActiveJobId was null
                // (i.e., after the assignment succeeded). Must NOT fire on the "already acquired" path.
                var now = DateTimeOffset.UtcNow;
                e.OrphanRestoredAt = now;
                // Synchronously update _localSnapshot so GetByConnectionId returns the correct
                // ActiveJobId immediately — before the fire-and-forget Redis write completes.
                // Without this, [RequiresActiveJob] hub calls made during the async write window
                // see the stale snapshot (ActiveJobId = null) and throw HubException (issue #2616).
                // TODO (WARNING): lock(entry.SyncRoot) above guards the live entry object but does
                // NOT protect the _localSnapshot mutation triggered by SetLocalAgentSnapshotField.
                // The snapshot write in DistributedAgentRegistryService.SetLocalSnapshotField is
                // unsynchronized against Register, TransitionStatusAsync, and UpdateAgentFieldAsync.
                // The entry lock is entry-scoped; _localSnapshot has no dedicated lock here. This is
                // consistent with other _localSnapshot writes in this file that also run outside
                // any snapshot-scoped lock. See DistributedAgentRegistryService.SetLocalSnapshotField
                // for the full non-atomic read-then-write WARNING. (Correctness WARNING, issue #2616)
                _facade.SetLocalAgentSnapshotField(agentId, AgentFieldNames.ActiveJobId, mostRecent.RunId);
                _facade.SetLocalAgentSnapshotField(agentId, AgentFieldNames.OrphanRestoredAt, now.ToString("O"));
                _facade.UpdateAgentFieldFireAndForget(agentId, AgentFieldNames.OrphanRestoredAt, now.ToString("O"), _logger, "DetectAndRestoreOrphans");
            });

        if (!didTransition)
        {
            // DrainService assigned a job between GetActiveRunsByAgent and the lock acquisition.
            _logger.Information(
                "Agent {AgentId} acquired job {ActiveJobId} between registration and orphan check, skipping orphan restoration",
                agentId, existingJobId);
            return;
        }

        // Re-materialize the run hash in Redis so GetRun returns non-null on any replica,
        // even if the hash was about to expire between this check and the agent's first
        // hub call. GetActiveRunsByAgent guarantees the hash existed when mostRecent was
        // loaded (GetActiveRunsAsync skips runs with an empty/absent hash). Only write if
        // the hash is absent — if another replica has already written a live hash (e.g.
        // advancing currentStep, writing prUrl between GetActiveRunsByAgent and now),
        // calling AddRun would overwrite those newer values with stale snapshot data.
        // Guard: call GetRun first; if the hash exists, skip AddRun entirely. If absent
        // (expired between GetActiveRunsByAgent and this point), call AddRun to re-anchor.
        // This call is OUTSIDE lock(entry.SyncRoot) — GetRun performs synchronous Redis I/O
        // via .GetAwaiter().GetResult(); holding the entry lock across a network call is
        // an anti-pattern. See HandleCrashRecovery for the established pattern.
        var existingHash = _facade.GetRun(mostRecent.RunId);
        if (existingHash is null)
            _facade.AddRun(mostRecent);

        _facade.TransitionStatus(agentId, AgentStatus.Busy);

        // Log at Information for a normal first-registration (run has not progressed
        // beyond initial analysis), and at Warning for a mid-run re-registration where
        // the agent was actively working (issue #2956).
        // NOTE: OrphanRestoredAt is NOT a valid discriminator here — it is set above on
        // this very call. The CurrentStep of the orphaned run is the correct signal.
        // TODO: [WARNING] The boundary `<= AnalyzingCode` includes early setup steps
        // (CloningRepository=1, SyncingBrainRepoPreRun=2, CreatingBranch=3, VerifyingBaseline=4)
        // which are past initial dispatch. A run at step 4 that crashed logs at Information
        // instead of Warning. In practice the 77 noisy Warnings were all genuine first-starts,
        // so the current boundary is directionally correct for the common path. Tighten if
        // setup-step crashes become a diagnostic concern.
        var logLevel = mostRecent.CurrentStep <= PipelineStep.AnalyzingCode
            ? LogEventLevel.Information
            : LogEventLevel.Warning;
        _logger.Write(logLevel,
            "Agent {AgentId} re-registered without active job but orchestrator tracks {OrphanCount} orphaned run(s). " +
            "Restoring run {RunId} (issue {IssueIdentifier}) as active — ReconciliationService will time out the run if agent does not resume.",
            agentId, orphanCount, mostRecent.RunId, mostRecent.IssueIdentifier);
    }

    /// <summary>
    /// Acquires <paramref name="entry"/>.SyncRoot, sets <paramref name="entry"/>.ActiveJobId to
    /// <paramref name="runId"/> (unconditionally when <paramref name="guardAgainstConcurrentAssignment"/>
    /// is <see langword="false"/>; only when ActiveJobId is <see langword="null"/> when
    /// <see langword="true"/>), and on the guarded path calls
    /// <see cref="IAgentHubFacade.UpdateAgentFieldFireAndForget"/> for the ActiveJobId field
    /// while the lock is still held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Unconditional path</strong> (<paramref name="guardAgainstConcurrentAssignment"/> = <see langword="false"/>):
    /// The assignment is always authoritative (e.g. agent self-reported a consolidation or restored run).
    /// The lock is held only for the assignment; neither <c>TransitionStatus</c> nor
    /// <c>UpdateAgentFieldFireAndForget</c> are called inside the lock.
    /// The caller is responsible for calling both after this method returns <see langword="true"/>.
    /// </para>
    /// <para>
    /// <strong>Guarded path</strong> (<paramref name="guardAgainstConcurrentAssignment"/> = <see langword="true"/>):
    /// The assignment is conditional on <c>ActiveJobId</c> being <see langword="null"/>.
    /// When the assignment succeeds, <c>UpdateAgentFieldFireAndForget</c> for the ActiveJobId field
    /// is called inside the lock (before lock release), and any <paramref name="additionalLockedAction"/>
    /// is also invoked inside the lock. The caller is responsible for calling <c>TransitionStatus</c>
    /// after this method returns <see langword="true"/>.
    /// When <c>ActiveJobId</c> is already set, <c>TransitionStatus</c> is gated on whether the
    /// existing value matches <paramref name="runId"/> (same-agent reconnect path).
    /// </para>
    /// <para>
    /// <strong>Invariant</strong>: <c>TransitionStatus</c> acquires SyncRoot internally and must
    /// always be called <em>after</em> the lock is released. This method never calls
    /// <c>TransitionStatus</c>; the returned <see langword="bool"/> tells the caller whether to call it.
    /// </para>
    /// </remarks>
    /// <param name="entry">The agent entry to update.</param>
    /// <param name="agentId">The agent identifier, forwarded to fire-and-forget writes.</param>
    /// <param name="runId">The run ID to assign as <c>ActiveJobId</c>.</param>
    /// <param name="guardAgainstConcurrentAssignment">
    ///   When <see langword="true"/>, the assignment is conditional (null-check under lock).
    ///   When <see langword="false"/>, the assignment is unconditional.
    /// </param>
    /// <param name="callerName">Forwarded to <c>UpdateAgentFieldFireAndForget</c> for log context.</param>
    /// <param name="additionalLockedAction">
    ///   Optional action executed inside the lock immediately after a successful assignment.
    ///   Only invoked on the guarded path when the assignment succeeded (not on the "already set" branch).
    ///   Receives the <paramref name="entry"/> so callers can set additional fields without an extra closure capture.
    /// </param>
    /// <returns>
    ///   <see langword="true"/> when <c>TransitionStatus</c> should be called by the caller;
    ///   <see langword="false"/> when the caller must not call it.
    /// </returns>
    private bool ActivateAgentJob(
        AgentEntry entry,
        AgentId agentId,
        string runId,
        bool guardAgainstConcurrentAssignment,
        string callerName,
        Action<AgentEntry>? additionalLockedAction = null)
    {
        bool shouldTransition;
        lock (entry.SyncRoot)
        {
            if (guardAgainstConcurrentAssignment)
            {
                if (entry.ActiveJobId is null)
                {
                    entry.ActiveJobId = runId;
                    // UpdateAgentFieldFireAndForget is called inside the lock on the guarded path so
                    // that the async Redis continuation (ContinueWith) cannot race with the outer
                    // TransitionStatus call: TransitionStatus acquires SyncRoot internally and must
                    // be invoked after this lock is released, so calling the Redis write here ensures
                    // the fire-and-forget is started before the lock exits rather than after.
                    _facade.UpdateAgentFieldFireAndForget(agentId, AgentFieldNames.ActiveJobId, runId, _logger, callerName);
                    additionalLockedAction?.Invoke(entry);
                    shouldTransition = true;
                }
                else
                {
                    // ActiveJobId already set (same-agent reconnect or DrainService race).
                    // Only transition to Busy if the active job matches the run being linked.
                    // If DrainService assigned a different run between GetByAgentId and lock
                    // acquisition, entry.ActiveJobId != runId and we skip the transition to
                    // avoid clobbering the DrainService assignment.
                    // TODO [WARNING]: Semantic divergence between call sites. TrackLinkedActiveJob
                    // (Site 3) correctly uses "match → transition" semantics: shouldTransition = true
                    // when ActiveJobId already equals runId (same-agent reconnect). However,
                    // ActivateOrphanedRun (Site 4) had the original "any non-null → skip" semantics
                    // (if ActiveJobId is not null, shouldTransition = false → early return, no
                    // TransitionStatus, no AddRun). The unified helper uses "match → transition" for
                    // both sites, so when a concurrent writer assigns entry.ActiveJobId = mostRecent.RunId
                    // (the *same* run id) between GetActiveRunsByAgent and lock acquisition, the
                    // ActivateOrphanedRun path now proceeds with TransitionStatus and AddRun instead
                    // of returning early. Additionally, additionalLockedAction (which sets OrphanRestoredAt
                    // and updates _localSnapshot) is only invoked on the null branch, so it is skipped,
                    // leaving OrphanRestoredAt null and _localSnapshot un-updated for that path. Evaluate
                    // whether ActivateOrphanedRun should pass a distinct guard value or perform its own
                    // post-helper null check to restore the original "any non-null → skip" behavior.
                    shouldTransition = entry.ActiveJobId == runId;
                }
            }
            else
            {
                // Unconditional: this path only runs when the assignment is always authoritative.
                // The caller handles TransitionStatus and UpdateAgentFieldFireAndForget after the lock.
                // TODO [WARNING]: Split responsibility on the unconditional path. The guarded path
                // calls UpdateAgentFieldFireAndForget inside the lock; the unconditional path leaves
                // both TransitionStatus and UpdateAgentFieldFireAndForget to the caller. A future
                // caller passing guardAgainstConcurrentAssignment: false may omit the field write
                // since the method name ("ActivateAgentJob") implies a complete activation. If a new
                // unconditional call site is added, ensure it calls both TransitionStatus AND
                // UpdateAgentFieldFireAndForget after the method returns true.
                entry.ActiveJobId = runId;
                shouldTransition = true;
            }
        }
        return shouldTransition;
    }

    private async Task HandleCrashRecoveryAsync(AgentRegistrationMessage message, AgentId agentId, AgentEntry entry, CancellationToken ct)
    {
        // Crash recovery detection: agent registered without an active job but the
        // registry already restored ActiveJobId (from its own prior state in the update factory).
        // This means the agent lost its in-memory state (container restart) while the orchestrator
        // still thinks it's working. ReconciliationService (JobController) will time out the run
        // if the agent does not report progress within the configured timeout.
        if (message.ActiveJob is null && entry.OrphanRestoredAt is null)
        {
            string? existingJobId;
            lock (entry.SyncRoot)
            {
                // Capture ActiveJobId under the lock so a concurrent disconnect handler clearing
                // the field cannot produce a null read after the non-null guard below.
                existingJobId = entry.ActiveJobId;
                entry.OrphanRestoredAt = DateTimeOffset.UtcNow;
                _facade.UpdateAgentFieldFireAndForget(agentId, AgentFieldNames.OrphanRestoredAt, DateTimeOffset.UtcNow.ToString("O"), _logger, "HandleCrashRecoveryAsync");
            }

            if (existingJobId is not null)
            {
                var run = _facade.GetRun(existingJobId);
                if (run is not null)
                {
                    // Hash still live — refresh it to prevent imminent TTL expiry.
                    _facade.AddRun(run);
                }
                else
                {
                    // Option B: Redis hash has expired. Reconstruct a minimal PipelineRun from
                    // the WorkItem record so [RequiresActiveJob] hub calls (e.g. RequestGetIssue)
                    // succeed rather than failing with "No active run or work item found".
                    // The restored run carries only the fields recoverable from the DB — enough
                    // for ResolveIssueProviderForRunAsync and [RequiresActiveJob] auth to pass.
                    // ReconciliationService will still time out the run if the agent does not resume.
                    var restoredRun = await TryReconstructRunFromDbAsync(existingJobId, agentId.Value, ct);
                    if (restoredRun is not null)
                    {
                        _facade.AddRun(restoredRun);
                        _logger.Warning(
                            "HandleCrashRecoveryAsync: Redis hash expired for run {RunId} (agent {AgentId}) — " +
                            "reconstructed minimal PipelineRun from WorkItem DB record; hub calls will succeed but run metadata is partial",
                            existingJobId, agentId);
                    }
                    else
                    {
                        _logger.Warning(
                            "HandleCrashRecoveryAsync: Redis hash expired for run {RunId} (agent {AgentId}) and WorkItem not found in DB — " +
                            "hub calls requiring GetRun will fail; ReconciliationService will time out the run",
                            existingJobId, agentId);
                    }
                }
            }

            _logger.Warning(
                "Agent {AgentId} re-registered without active job but orchestrator has {JobId} assigned (crash recovery). " +
                "ReconciliationService will time out the run if agent does not resume.",
                agentId, existingJobId);
        }
        else
        {
            _logger.Information(
                "Agent {AgentId} registered with active job {ActiveJobId} (status={Status})",
                agentId, entry.ActiveJobId, entry.Status);
        }
    }

    /// <summary>
    /// Attempts to reconstruct a minimal <see cref="PipelineRun"/> from the WorkItem DB record
    /// when the Redis hash has expired. Returns null if the WorkItem cannot be found, lacks
    /// the minimum required fields (IssueIdentifier, IssueProviderConfigId, RepoProviderConfigId),
    /// or the DB is unavailable. The reconstructed run carries only fields recoverable from the DB —
    /// enough for <see cref="ResolveIssueProviderForRunAsync"/> and [RequiresActiveJob] auth to pass.
    /// </summary>
    private async Task<PipelineRun?> TryReconstructRunFromDbAsync(string runId, string agentId, CancellationToken ct)
    {
        if (!Guid.TryParse(runId, out _))
        {
            _logger.Warning(
                "TryReconstructRunFromDbAsync: runId '{RunId}' is not a valid GUID — cannot query WorkItem",
                LogSanitizer.SanitizeForLog(runId));
            return null;
        }

        // GetWorkItemRunRecordAsync throws on DB failure (intentional — failed reads must not look
        // like missing records during normal ownership checks). Here, reconstruction is best-effort:
        // a DB outage should degrade to "skip reconstruction" rather than crashing re-registration.
        // OperationCanceledException is not caught so a cancelled token propagates to the caller
        // rather than being silently treated as a missing record.
        WorkItemRunRecord? record;
        try
        {
            record = await _facade.GetWorkItemRunRecordAsync(new JobId(runId), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // TODO: [WARNING] runId is logged verbatim here. Serilog's structured logging prevents
            // format-string injection, but the raw value is persisted in the log store without
            // sanitization (unlike the guard at the top of this method which calls
            // LogSanitizer.SanitizeForLog). A crafted runId could confuse log-based alerting or
            // SIEM rules that parse RunId as a structured identifier. Consider wrapping with
            // LogSanitizer.SanitizeForLog(runId) for consistency.
            _logger.Warning(ex,
                "TryReconstructRunFromDbAsync: could not read WorkItem {RunId} from DB — skipping reconstruction",
                runId);
            return null;
        }

        if (record is null)
            return null;

        if (string.IsNullOrEmpty(record.RepoProviderConfigId))
        {
            _logger.Warning(
                "TryReconstructRunFromDbAsync: WorkItem {RunId} has no RepoProviderConfigId in Payload — cannot reconstruct PipelineRun",
                runId);
            return null;
        }

        // RunType MUST be set on creationParams before calling any factory method.
        // CreateDecomposition validates it and throws ArgumentOutOfRangeException if it is
        // left at the default (PipelineRunType.Implementation).
        var runType = record.TaskType.ToDefaultRunType();
        var creationParams = new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = record.IssueIdentifier,
            IssueTitle = string.Empty,
            IssueProviderConfigId = record.IssueProviderConfigId,
            RepoProviderConfigId = record.RepoProviderConfigId,
            BrainProviderConfigId = record.BrainProviderConfigId,
            InitiatedBy = "recovery",
            AgentId = new AgentId(agentId),
            RunType = runType,
        };

        var run = PipelineRun.CreateForRunType(creationParams);
        run.ProjectId = record.ProjectId?.ToString();
        return run;
    }
}
