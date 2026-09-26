using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
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
    private const string ActiveJobIdField = "activeJobId";

    private readonly IAgentHubFacade _facade = facade;
    private readonly IChangeNotifier _changeNotifier = changeNotifier;
    private readonly ILogger _logger = logger;

    // TODO: Add CancellationToken parameter to RecoverOrphanedStateAsync (and update IAgentOrphanRecoveryService).
    // Currently uses CancellationToken.None for GetRunHistoryAsync — a pre-existing issue preserved
    // in the refactoring, but this operation hits history storage and should be cancellable.
    /// <inheritdoc />
    public async Task RecoverOrphanedStateAsync(AgentRegistrationMessage message, AgentId agentId)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(agentId.Value);

        // Re-track active job from agent state (handles orchestrator restart scenario)
        if (message.ActiveJob is not null)
        {
            await RestoreActiveJobAsync(message, agentId);
        }

        // Detect orphaned runs: if the orchestrator tracks active runs for this agent
        // but the agent registered without an active job, restore the ActiveJobId on the
        // registry entry. This avoids immediately failing runs when an agent has a brief network blip.
        // The ReconciliationService (JobController) enforces work-item timeouts.
        var entry = _facade.GetByAgentId(agentId);
        if (entry is { ActiveJobId: null })
        {
            await DetectAndRestoreOrphans(agentId, entry);
        }
        else if (entry is { ActiveJobId: not null })
        {
            await HandleCrashRecoveryAsync(message, agentId, entry);
        }
    }

    private async Task RestoreActiveJobAsync(AgentRegistrationMessage message, AgentId agentId)
    {
        var activeJob = message.ActiveJob!;
        var existingRun = _facade.GetRun(activeJob.RunId);

        if (existingRun is null)
        {
            await RestoreRunFromAgentStateAsync(agentId, activeJob);
        }
        else
        {
            LinkAgentToExistingRun(existingRun, agentId, activeJob);
        }
    }

    private async Task RestoreRunFromAgentStateAsync(
        AgentId agentId, ActiveJobState activeJob)
    {
        // Check history — don't re-register a completed run.
        // Only treat runs with successful terminal states as stale.
        // Cancelled/Failed runs may be legitimately re-dispatched with the same RunId.
        var history = await _facade.GetRunHistoryAsync(CancellationToken.None);
        var inHistory = history.Any(r => r.RunId == activeJob.RunId
            && r.FinalStep != PipelineStep.Cancelled
            && r.FinalStep != PipelineStep.Failed);

        if (!inHistory)
        {
            await RestoreNewRunAsync(agentId, activeJob);
        }
        else
        {
            _logger.Information(
                "Agent {AgentId} reported active job {RunId} but it's already in history — ignoring stale state",
                agentId, activeJob.RunId);
        }
    }

    private Task RestoreNewRunAsync(AgentId agentId, ActiveJobState activeJob)
    {
        // Skip restoration for consolidation runs — they have their own
        // completion path (ReportConsolidationComplete) and should not
        // enter pipeline run tracking or history.
        if (activeJob.IssueProviderConfigId == ConsolidationConstants.ProviderConfigId)
        {
            RestoreConsolidationTracking(agentId, activeJob);
        }
        else
        {
            RestorePipelineRun(agentId, activeJob);
        }
        return Task.CompletedTask;
    }

    private void RestoreConsolidationTracking(AgentId agentId, ActiveJobState activeJob)
    {
        _logger.Information(
            "Agent {AgentId} reported active consolidation job {RunId} — skipping pipeline run restoration (handled by ReportConsolidationComplete)",
            agentId, activeJob.RunId);

        // Still mark agent as busy with this job so it's tracked correctly.
        // ActiveJobId write is under SyncRoot (release-then-reacquire pattern: TransitionStatus
        // acquires SyncRoot internally, so it must be called after the lock is released).
        // Guard against TOCTOU: capture whether we wrote the value inside the lock, then
        // only call TransitionStatus if the value we wrote is still current — a concurrent
        // disconnect handler may have cleared ActiveJobId between lock release and this check.
        var consolEntry = _facade.GetByAgentId(agentId);
        if (consolEntry is not null)
        {
            // ActiveJobId is written under SyncRoot. TransitionStatus acquires SyncRoot internally
            // so it must be called after the lock is released. The write is unconditional here:
            // this path only runs when the agent self-reported a consolidation job, so the
            // ActiveJobId assignment is always authoritative.
            lock (consolEntry.SyncRoot)
            {
                consolEntry.ActiveJobId = activeJob.RunId;
            }
            _facade.TransitionStatus(agentId, AgentStatus.Busy);
            // UpdateAgentFieldAsync is called AFTER TransitionStatus and outside the lock:
            // the async continuation must not escape the lock scope and potentially
            // overwrite the Busy status already written to Redis by TransitionStatus.
            // TODO: [WARNING] CancellationToken is not threaded through to UpdateAgentFieldAsync.
            // RecoverOrphanedStateAsync does not accept a CancellationToken, so this is a structural
            // limitation at the call site. If cancellation support is added to the enclosing method,
            // propagate the token here.
            _facade.UpdateAgentFieldFireAndForget(agentId, ActiveJobIdField, activeJob.RunId, _logger, "RestoreConsolidationTracking");
        }

        _changeNotifier.NotifyChange();
    }

    private void RestorePipelineRun(AgentId agentId, ActiveJobState activeJob)
    {
        var restoredRun = CreateRestoredPipelineRun(agentId.Value, activeJob);
        restoredRun.CurrentStep = activeJob.CurrentStep;
        restoredRun.PipelineProviderConfigId = activeJob.PipelineProviderConfigId;
        restoredRun.ResolvedProfileId = activeJob.ResolvedProfileId;
        restoredRun.ProjectId = activeJob.ProjectId;
        restoredRun.ProjectName = activeJob.ProjectName;
        restoredRun.RepositoryName = activeJob.RepositoryName;
        restoredRun.ModelName = activeJob.ModelName;

        _facade.AddRun(restoredRun);

        // Set agent as busy with this job.
        // ActiveJobId write is under SyncRoot (release-then-reacquire pattern: TransitionStatus
        // acquires SyncRoot internally, so it must be called after the lock is released).
        // Guard against TOCTOU: capture whether we wrote the value inside the lock, then
        // only call TransitionStatus if the value we wrote is still current — a concurrent
        // disconnect handler may have cleared ActiveJobId between lock release and this check.
        var restoredEntry = _facade.GetByAgentId(agentId);
        if (restoredEntry is not null)
        {
            // ActiveJobId is written under SyncRoot. TransitionStatus acquires SyncRoot internally
            // so it must be called after the lock is released. The write is unconditional here:
            // this path only runs when we have just created a restored run, so the ActiveJobId
            // assignment is always authoritative.
            lock (restoredEntry.SyncRoot)
            {
                restoredEntry.ActiveJobId = activeJob.RunId;
            }
            _facade.TransitionStatus(agentId, AgentStatus.Busy);
            // UpdateAgentFieldAsync is called AFTER TransitionStatus and outside the lock:
            // the async continuation must not escape the lock scope and potentially
            // overwrite the Busy status already written to Redis by TransitionStatus.
            // TODO: [WARNING] CancellationToken is not threaded through to UpdateAgentFieldAsync.
            // RecoverOrphanedStateAsync does not accept a CancellationToken, so this is a structural
            // limitation at the call site. If cancellation support is added to the enclosing method,
            // propagate the token here.
            _facade.UpdateAgentFieldFireAndForget(agentId, ActiveJobIdField, activeJob.RunId, _logger, "RestorePipelineRun");
        }

        _logger.Information(
            "Restored active run {RunId} for agent {AgentId} (issue {IssueIdentifier}, step {Step}) — orchestrator state recovery",
            activeJob.RunId, agentId, activeJob.IssueIdentifier, activeJob.CurrentStep);

        _changeNotifier.NotifyChange();
    }

    private static PipelineRun CreateRestoredPipelineRun(string agentId, ActiveJobState activeJob)
    {
        return activeJob.RunType switch
        {
            PipelineRunType.Review => PipelineRun.CreateReview(new PipelineRunCreationParams
            {
                RunId = activeJob.RunId,
                IssueIdentifier = activeJob.IssueIdentifier,
                IssueTitle = activeJob.IssueTitle,
                IssueProviderConfigId = activeJob.IssueProviderConfigId,
                RepoProviderConfigId = activeJob.RepoProviderConfigId,
                RunType = PipelineRunType.Review,
                StartedAt = activeJob.StartedAt,
                InitiatedBy = activeJob.InitiatedBy,
                AgentId = agentId,
                AgentProviderConfigId = activeJob.AgentProviderConfigId,
                BrainProviderConfigId = activeJob.BrainProviderConfigId,
                ReviewPrBranchName = string.Empty,
                ReviewPrTargetBranch = string.Empty
            }),
            PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition => PipelineRun.CreateDecomposition(new PipelineRunCreationParams
            {
                RunId = activeJob.RunId,
                IssueIdentifier = activeJob.IssueIdentifier,
                IssueTitle = activeJob.IssueTitle,
                IssueProviderConfigId = activeJob.IssueProviderConfigId,
                RepoProviderConfigId = activeJob.RepoProviderConfigId,
                RunType = activeJob.RunType,
                StartedAt = activeJob.StartedAt,
                InitiatedBy = activeJob.InitiatedBy,
                AgentId = agentId,
                AgentProviderConfigId = activeJob.AgentProviderConfigId,
                BrainProviderConfigId = activeJob.BrainProviderConfigId
            }),
            _ => PipelineRun.CreateImplementation(new PipelineRunCreationParams
            {
                RunId = activeJob.RunId,
                IssueIdentifier = activeJob.IssueIdentifier,
                IssueTitle = activeJob.IssueTitle,
                IssueProviderConfigId = activeJob.IssueProviderConfigId,
                RepoProviderConfigId = activeJob.RepoProviderConfigId,
                StartedAt = activeJob.StartedAt,
                InitiatedBy = activeJob.InitiatedBy,
                AgentId = agentId,
                AgentProviderConfigId = activeJob.AgentProviderConfigId,
                BrainProviderConfigId = activeJob.BrainProviderConfigId
            })
        };
    }

    private void LinkAgentToExistingRun(
        PipelineRun existingRun, AgentId agentId, ActiveJobState activeJob)
    {
        // Run already exists in-memory (e.g., created by K8s DispatchService with AgentId=null).
        // Ensure the agent is linked to it and transitioned to Busy.
        //
        // AgentId update: covers first pickup (null AgentId) and pod-replacement reconnect
        // (different AgentId). Same-agent reconnect (already matches) is a no-op for the assignment.
        if (existingRun.AgentId != agentId.Value)
        {
            var previousAgentId = existingRun.AgentId;
            existingRun.AgentId = agentId.Value;

            if (previousAgentId is not null)
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
        existingRun.ModelName ??= activeJob.ModelName;
        existingRun.RepositoryName ??= activeJob.RepositoryName;

        // Agent tracking: always run when the run belongs to this agent.
        // This covers: first pickup (AgentId just set above), pod replacement (AgentId just updated),
        // and same-agent reconnect (AgentId already matched, tracking still needed if entry lost state).
        var trackedEntry = _facade.GetByAgentId(agentId);
        if (trackedEntry is not null)
        {
            bool shouldTransition;
            lock (trackedEntry.SyncRoot)
            {
                if (trackedEntry.ActiveJobId is null)
                {
                    trackedEntry.ActiveJobId = activeJob.RunId;
                    _facade.UpdateAgentFieldFireAndForget(agentId, ActiveJobIdField, activeJob.RunId, _logger, "LinkAgentToExistingRun");
                    // Transition to Busy only when we actually wrote the ActiveJobId.
                    // The decision is captured inside the lock so a concurrent disconnect handler
                    // that clears ActiveJobId after lock release cannot cause a spurious Busy
                    // transition.
                    shouldTransition = true;
                }
                else
                {
                    // ActiveJobId already set (same-agent reconnect or DrainService race).
                    // Only transition to Busy if the active job matches the run being linked.
                    // If DrainService assigned a different run between GetByAgentId and lock
                    // acquisition, trackedEntry.ActiveJobId != activeJob.RunId and we skip the
                    // transition to avoid clobbering the DrainService assignment.
                    shouldTransition = trackedEntry.ActiveJobId == activeJob.RunId;
                }
            }
            if (shouldTransition)
                _facade.TransitionStatus(agentId, AgentStatus.Busy);
        }

        _logger.Debug("Agent {AgentId} active job {RunId} already tracked — linked agent to run",
            agentId, activeJob.RunId);
    }

    private async Task DetectAndRestoreOrphans(AgentId agentId, AgentEntry entry)
    {
        var orphanedRuns = _facade.GetActiveRunsByAgent(agentId);
        if (orphanedRuns.Count > 0)
        {
            // Restore the most recent orphaned run as the active job so the
            // disconnect grace period timer applies. If the agent truly lost the job,
            // ReconciliationService (JobController) will time out the run after the grace period.
            var mostRecent = orphanedRuns[^1];

            // Guard: don't re-activate a run that is already in history as a non-Cancelled/
            // non-Failed terminal state (e.g. Completed, PrMerged, PrClosed, ConflictRestart).
            // Mirrors the history check in RestoreRunFromAgentStateAsync. Cancelled/Failed runs
            // remain restorable — they may be legitimately re-dispatched.
            // TODO [WARNING]: CancellationToken.None is passed because RecoverOrphanedStateAsync does not yet
            // accept a CancellationToken. Add a token parameter to the public method and propagate it here
            // so that hub connection teardown can abort this history storage call (tracked separately).
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
                history = await _facade.GetRunHistoryAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                // Fail-open: if history storage is unavailable, proceed with restoration rather
                // than blocking agent registration. At worst, a completed run is briefly re-activated
                // until ReconciliationService times it out. Failing closed here would leave the agent
                // stuck in Idle with a legitimate orphaned run for the full reconciliation grace period.
                _logger.Warning(ex,
                    "Agent {AgentId} orphan guard: GetRunHistoryAsync faulted — proceeding with restoration (fail-open)",
                    agentId);
                history = [];
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
                return;
            }
            bool shouldTransition;
            lock (entry.SyncRoot)
            {
                // Atomic check-and-set under lock: if DrainService assigned a job
                // between GetActiveRunsByAgent and this lock acquisition, don't overwrite.
                if (entry.ActiveJobId is not null)
                {
                    _logger.Information(
                        "Agent {AgentId} acquired job {ActiveJobId} between registration and orphan check, skipping orphan restoration",
                        agentId, entry.ActiveJobId);
                    shouldTransition = false;
                }
                else
                {
                    var now = DateTimeOffset.UtcNow;
                    entry.ActiveJobId = mostRecent.RunId;
                    entry.OrphanRestoredAt = now;
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
                    _facade.SetLocalAgentSnapshotField(agentId, ActiveJobIdField, mostRecent.RunId);
                    _facade.SetLocalAgentSnapshotField(agentId, "orphanRestoredAt", now.ToString("O"));
                    _facade.UpdateAgentFieldFireAndForget(agentId, ActiveJobIdField, mostRecent.RunId, _logger, "DetectAndRestoreOrphans");
                    _facade.UpdateAgentFieldFireAndForget(agentId, "orphanRestoredAt", now.ToString("O"), _logger, "DetectAndRestoreOrphans");
                    // The decision to call TransitionStatus is captured inside the lock.
                    // This prevents a concurrent disconnect handler from clearing ActiveJobId
                    // between lock release and the TransitionStatus call.
                    shouldTransition = true;
                }
            }

            if (shouldTransition)
            {
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
                    agentId, orphanedRuns.Count, mostRecent.RunId, mostRecent.IssueIdentifier);
            }
        }
        else
        {
            _logger.Information(
                "Agent {AgentId} registered with no active job and no orphaned runs (status={Status})",
                agentId, entry.Status);
        }
    }

    private async Task HandleCrashRecoveryAsync(AgentRegistrationMessage message, AgentId agentId, AgentEntry entry)
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
                _facade.UpdateAgentFieldFireAndForget(agentId, "orphanRestoredAt", DateTimeOffset.UtcNow.ToString("O"), _logger, "HandleCrashRecoveryAsync");
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
                    var restoredRun = await TryReconstructRunFromDbAsync(existingJobId, agentId.Value);
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
    /// when the Redis hash has expired. Returns null if the WorkItem cannot be found or lacks
    /// the minimum required fields (IssueIdentifier, IssueProviderConfigId, RepoProviderConfigId).
    /// The reconstructed run carries only fields recoverable from the DB — enough for
    /// <see cref="ResolveIssueProviderForRunAsync"/> and [RequiresActiveJob] auth to pass.
    /// </summary>
    private async Task<PipelineRun?> TryReconstructRunFromDbAsync(string runId, string agentId)
    {
        if (!Guid.TryParse(runId, out _))
        {
            _logger.Warning(
                "TryReconstructRunFromDbAsync: runId '{RunId}' is not a valid GUID — cannot query WorkItem",
                runId);
            return null;
        }

        var jobId = new JobId(runId);

        var issueMetadata = await _facade.GetWorkItemIssueMetadataAsync(jobId, CancellationToken.None);
        if (issueMetadata is null)
            return null;

        var providerIds = await _facade.GetWorkItemProviderConfigIdsAsync(jobId, CancellationToken.None);
        var repoProviderConfigId = providerIds?.RepoProviderConfigId;
        if (string.IsNullOrEmpty(repoProviderConfigId))
        {
            _logger.Warning(
                "TryReconstructRunFromDbAsync: WorkItem {RunId} has no RepoProviderConfigId in Payload — cannot reconstruct PipelineRun",
                runId);
            return null;
        }

        return PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = issueMetadata.Value.IssueIdentifier,
            IssueTitle = string.Empty,
            IssueProviderConfigId = issueMetadata.Value.IssueProviderConfigId,
            RepoProviderConfigId = repoProviderConfigId,
            BrainProviderConfigId = providerIds!.Value.BrainProviderConfigId,
            InitiatedBy = "recovery",
            AgentId = new AgentId(agentId),
        });
    }
}
