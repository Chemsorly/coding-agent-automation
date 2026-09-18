using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgent.AgentGateway;

/// <summary>
/// Extracts orphan-restoration logic from <see cref="AgentHub.RegisterAgent"/>.
/// Handles active-job restoration, orphan detection, and crash recovery.
/// </summary>
public sealed class AgentOrphanRecoveryService : IAgentOrphanRecoveryService
{
    private readonly IAgentHubFacade _facade;
    private readonly IChangeNotifier _changeNotifier;
    private readonly ILogger _logger;

    public AgentOrphanRecoveryService(
        IAgentHubFacade facade,
        IChangeNotifier changeNotifier,
        ILogger logger)
    {
        _facade = facade;
        _changeNotifier = changeNotifier;
        _logger = logger;
    }

    // TODO: Add CancellationToken parameter to RecoverOrphanedStateAsync (and update IAgentOrphanRecoveryService).
    // Currently uses CancellationToken.None for GetRunHistoryAsync — a pre-existing issue preserved
    // in the refactoring, but this operation hits history storage and should be cancellable.
    /// <inheritdoc />
    public async Task RecoverOrphanedStateAsync(AgentRegistrationMessage message, AgentId agentId)
    {
        ArgumentNullException.ThrowIfNull(message);
        // TODO: Replace ArgumentNullException.ThrowIfNull(agentId.Value) with
        // ArgumentException.ThrowIfNullOrEmpty(agentId.Value, nameof(agentId)) — ThrowIfNull on a struct
        // field reports "Value" as the parameter name in exceptions rather than "agentId".
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
            DetectAndRestoreOrphans(agentId, entry);
        }
        else if (entry is { ActiveJobId: not null })
        {
            HandleCrashRecovery(message, agentId, entry);
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
        // ActiveJobId write and the shouldTransition decision are both made inside SyncRoot.
        // TransitionStatus acquires SyncRoot internally, so it must be called after the lock
        // releases — but the decision to call it is captured atomically with the write.
        // If a concurrent disconnect handler cleared ActiveJobId (under the same SyncRoot) after
        // the write but before shouldTransition is evaluated, the comparison will be false and
        // TransitionStatus(Busy) will not be called — avoiding a Disconnected→Busy clobber.
        var consolEntry = _facade.GetByAgentId(agentId);
        if (consolEntry is not null)
        {
            bool shouldTransition;
            lock (consolEntry.SyncRoot)
            {
                consolEntry.ActiveJobId = activeJob.RunId;
                // TODO: [WARNING] UpdateAgentFieldAsync is called while holding SyncRoot.
                // On the in-memory path (AgentRegistryService) this reacquires SyncRoot
                // internally — safe only because Monitor is reentrant on the same thread.
                // On the distributed path the async continuation escapes the lock and may
                // complete after TransitionStatus, causing transient persisted-state skew
                // (reconciliation corrects this, but it is an ordering hazard).
                // Consider hoisting the persistence write outside the lock to make ordering
                // explicit and remove the nested-lock coupling.
                _ = _facade.UpdateAgentFieldAsync(agentId, "activeJobId", activeJob.RunId);
                // Evaluate the transition decision after the persistence call so that any
                // synchronous mutation of ActiveJobId (e.g. a reentrant disconnect handler)
                // that runs inside the lock is taken into account.
                shouldTransition = consolEntry.ActiveJobId == activeJob.RunId;
            }
            if (shouldTransition)
                _facade.TransitionStatus(agentId, AgentStatus.Busy);
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
        // ActiveJobId write and the shouldTransition decision are both made inside SyncRoot.
        // TransitionStatus acquires SyncRoot internally, so it must be called after the lock
        // releases — but the decision to call it is captured atomically with the write.
        // If a concurrent disconnect handler cleared ActiveJobId (under the same SyncRoot) after
        // the write but before shouldTransition is evaluated, the comparison will be false and
        // TransitionStatus(Busy) will not be called — avoiding a Disconnected→Busy clobber.
        var restoredEntry = _facade.GetByAgentId(agentId);
        if (restoredEntry is not null)
        {
            bool shouldTransition;
            lock (restoredEntry.SyncRoot)
            {
                restoredEntry.ActiveJobId = activeJob.RunId;
                // TODO: [WARNING] UpdateAgentFieldAsync is called while holding SyncRoot.
                // On the in-memory path (AgentRegistryService) this reacquires SyncRoot
                // internally — safe only because Monitor is reentrant on the same thread.
                // On the distributed path the async continuation escapes the lock and may
                // complete after TransitionStatus, causing transient persisted-state skew
                // (reconciliation corrects this, but it is an ordering hazard).
                // Consider hoisting the persistence write outside the lock to make ordering
                // explicit and remove the nested-lock coupling.
                _ = _facade.UpdateAgentFieldAsync(agentId, "activeJobId", activeJob.RunId);
                // Evaluate the transition decision after the persistence call so that any
                // synchronous mutation of ActiveJobId (e.g. a reentrant disconnect handler)
                // that runs inside the lock is taken into account.
                shouldTransition = restoredEntry.ActiveJobId == activeJob.RunId;
            }
            if (shouldTransition)
                _facade.TransitionStatus(agentId, AgentStatus.Busy);
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

            // TODO: [WARNING] Use !string.IsNullOrEmpty(previousAgentId) here instead of `is not null`
            // to match RegisterAgent's first-pickup detection (which uses string.IsNullOrEmpty).
            // If AgentId were ever an empty string, `is not null` would log a pod-replacement entry
            // with PreviousAgentId="" while RegisterAgent would treat the same state as a first pickup.
            // The normal dispatch path always sets AgentId=null, so the divergence is theoretical,
            // but the two guards should use the same predicate for consistency.
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
                    _ = _facade.UpdateAgentFieldAsync(agentId, "activeJobId", activeJob.RunId);
                }
                // Evaluate the transition decision under the lock so a concurrent disconnect
                // handler clearing ActiveJobId cannot produce a stale true outside the lock.
                shouldTransition = trackedEntry.ActiveJobId == activeJob.RunId;
            }
            if (shouldTransition)
                _facade.TransitionStatus(agentId, AgentStatus.Busy);
        }

        _logger.Debug("Agent {AgentId} active job {RunId} already tracked — linked agent to run",
            agentId, activeJob.RunId);
    }

    private void DetectAndRestoreOrphans(AgentId agentId, AgentEntry entry)
    {
        var orphanedRuns = _facade.GetActiveRunsByAgent(agentId);
        // TODO: [WARNING] No "genuinely completed run" guard here. RestoreRunFromAgentStateAsync
        // explicitly checks run history and refuses to restore runs whose FinalStep is a non-Cancelled/
        // non-Failed terminal state. DetectAndRestoreOrphans relies entirely on GetActiveRunsByAgent
        // returning only active-set members. If a completed run lingers in the active set due to a
        // RemoveRun lag or failure (the service logs these cases), that run is re-added with no TTL
        // and re-SADD'd, re-activating a finished run. Consider adding a history check mirroring
        // RestoreRunFromAgentStateAsync before calling AddRun.
        if (orphanedRuns.Count > 0)
        {
            // Restore the most recent orphaned run as the active job so the
            // disconnect grace period timer applies. If the agent truly lost the job,
            // ReconciliationService (JobController) will time out the run after the grace period.
            var mostRecent = orphanedRuns[^1];
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
                    _facade.SetLocalAgentSnapshotField(agentId, "activeJobId", mostRecent.RunId);
                    _facade.SetLocalAgentSnapshotField(agentId, "orphanRestoredAt", now.ToString("O"));
                    _ = _facade.UpdateAgentFieldAsync(agentId, "activeJobId", mostRecent.RunId);
                    _ = _facade.UpdateAgentFieldAsync(agentId, "orphanRestoredAt", now.ToString("O"));
                    shouldTransition = true;
                }
            }

            if (shouldTransition)
            {
                // Re-materialize the run hash in Redis so GetRun returns non-null on any replica,
                // even if the hash was about to expire between this check and the agent's first
                // hub call. GetActiveRunsByAgent guarantees the hash existed when mostRecent was
                // loaded (GetActiveRunsAsync skips runs with an empty/absent hash). Calling AddRun
                // refreshes the hash with no TTL — HSET + SADD are both idempotent, so calling
                // this twice for the same run is safe.
                // TODO: [WARNING] AddRun is called unconditionally without first checking whether
                // the hash already exists (no GetRun guard). The issue requirement says "must not
                // write a PipelineRun to Redis if the hash already exists", which is satisfied at
                // the Redis level (HSET overwrites same fields, SADD is a no-op on existing members)
                // but not at the application level. If stricter application-level idempotency is
                // needed, consider guarding with GetRun != null before calling AddRun here.
                // TODO: [WARNING] AddRun performs a full hash overwrite (HashSetAsync with all
                // serialized fields from the mostRecent snapshot). If another replica updated the
                // run hash between GetActiveRunsByAgent and this write (e.g., advancing a step,
                // writing PR fields, updating tokens), those newer values will be silently clobbered
                // back to snapshot state. HSET is only idempotent when both sides are byte-identical;
                // this is a data-loss risk for any actively-progressing run on the orphan path.
                _facade.AddRun(mostRecent);

                _facade.TransitionStatus(agentId, AgentStatus.Busy);

                _logger.Warning(
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

    private void HandleCrashRecovery(AgentRegistrationMessage message, AgentId agentId, AgentEntry entry)
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
                _ = _facade.UpdateAgentFieldAsync(agentId, "orphanRestoredAt", DateTimeOffset.UtcNow.ToString("O"));
            }

            // Re-materialize the run hash so subsequent [RequiresActiveJob] hub calls can find
            // the run via GetRun. entry.ActiveJobId was restored from prior registry state and
            // the hash may have expired since. Read the run first; if the hash still exists,
            // AddRun refreshes it without a TTL (HSET + SADD, both idempotent).
            // If the hash is already gone, nothing can be done — ReconciliationService will
            // time out the run as it would for any unresponsive agent.
            // TODO: [WARNING] When GetRun returns null (hash already expired), this path sets
            // entry.OrphanRestoredAt and proceeds without re-materializing the run. Every
            // subsequent [RequiresActiveJob] hub call (including RequestGetIssue) will still fail
            // with "No active run found" — the same bug the issue describes. At this point
            // the run data is unrecoverable from Redis; a DB fallback (loading the WorkItem from
            // Postgres to reconstruct a minimal PipelineRun) would be required to fully address
            // this case (Option B from the issue's suggested approaches).
            if (existingJobId is not null)
            {
                var run = _facade.GetRun(existingJobId);
                if (run is not null)
                    _facade.AddRun(run);
            }

            // TODO: [WARNING] The log interpolation below uses entry.ActiveJobId, which is read
            // outside lock(entry.SyncRoot). existingJobId was captured inside the lock and should
            // be used here instead to avoid a benign TOCTOU: a concurrent disconnect handler
            // could clear entry.ActiveJobId to null between the lock release and this log statement,
            // causing the log to print null when existingJobId is non-null.
            _logger.Warning(
                "Agent {AgentId} re-registered without active job but orchestrator has {JobId} assigned (crash recovery). " +
                "ReconciliationService will time out the run if agent does not resume.",
                agentId, entry.ActiveJobId);
        }
        else
        {
            _logger.Information(
                "Agent {AgentId} registered with active job {ActiveJobId} (status={Status})",
                agentId, entry.ActiveJobId, entry.Status);
        }
    }
}
