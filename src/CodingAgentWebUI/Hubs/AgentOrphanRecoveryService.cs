using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Hubs;

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
    public async Task RecoverOrphanedStateAsync(AgentRegistrationMessage message, string agentId)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(agentId);

        // Re-track active job from agent state (handles orchestrator restart scenario)
        if (message.ActiveJob is not null)
        {
            await RestoreActiveJobAsync(message, agentId);
        }

        // Detect orphaned runs: if the orchestrator tracks active runs for this agent
        // but the agent registered without an active job, restore the ActiveJobId on the
        // registry entry so the HeartbeatMonitor grace period logic can handle cleanup.
        // This avoids immediately failing runs when an agent has a brief network blip.
        var entry = _facade.GetByAgentId(agentId);
        if (entry is { ActiveJobId: null })
        {
            DetectAndRestoreOrphans(message, agentId, entry);
        }
        else if (entry is { ActiveJobId: not null })
        {
            HandleCrashRecovery(message, agentId, entry);
        }
    }

    private async Task RestoreActiveJobAsync(AgentRegistrationMessage message, string agentId)
    {
        var activeJob = message.ActiveJob!;
        var existingRun = _facade.GetRun(activeJob.RunId);

        if (existingRun is null)
        {
            await RestoreRunFromAgentStateAsync(message, agentId, activeJob);
        }
        else
        {
            LinkAgentToExistingRun(existingRun, message, agentId, activeJob);
        }
    }

    private async Task RestoreRunFromAgentStateAsync(
        AgentRegistrationMessage message, string agentId, ActiveJobState activeJob)
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
            // Skip restoration for consolidation runs — they have their own
            // completion path (ReportConsolidationComplete) and should not
            // enter pipeline run tracking or history.
            if (activeJob.IssueProviderConfigId == ConsolidationConstants.ProviderConfigId)
            {
                _logger.Information(
                    "Agent {AgentId} reported active consolidation job {RunId} — skipping pipeline run restoration (handled by ReportConsolidationComplete)",
                    agentId, activeJob.RunId);

                // Still mark agent as busy with this job so it's tracked correctly
                var consolEntry = _facade.GetByAgentId(agentId);
                if (consolEntry is not null)
                {
                    consolEntry.ActiveJobId = activeJob.RunId;
                    _facade.TransitionStatus(agentId, AgentStatus.Busy);
                }

                _changeNotifier.NotifyChange();
            }
            else
            {
                var restoredRun = PipelineRun.Create(
                    runId: activeJob.RunId,
                    issueIdentifier: activeJob.IssueIdentifier,
                    issueTitle: activeJob.IssueTitle,
                    issueProviderConfigId: activeJob.IssueProviderConfigId,
                    repoProviderConfigId: activeJob.RepoProviderConfigId,
                    runType: activeJob.RunType,
                    startedAt: activeJob.StartedAt,
                    initiatedBy: activeJob.InitiatedBy,
                    agentId: agentId,
                    agentProviderConfigId: activeJob.AgentProviderConfigId,
                    brainProviderConfigId: activeJob.BrainProviderConfigId);
                restoredRun.CurrentStep = activeJob.CurrentStep;
                restoredRun.PipelineProviderConfigId = activeJob.PipelineProviderConfigId;
                restoredRun.ResolvedProfileId = activeJob.ResolvedProfileId;
                restoredRun.ProjectId = activeJob.ProjectId;
                restoredRun.ProjectName = activeJob.ProjectName;
                restoredRun.RepositoryName = activeJob.RepositoryName;
                restoredRun.ModelName = activeJob.ModelName;

                _facade.AddRun(restoredRun);

                // Set agent as busy with this job
                var restoredEntry = _facade.GetByAgentId(agentId);
                if (restoredEntry is not null)
                {
                    restoredEntry.ActiveJobId = activeJob.RunId;
                    _facade.TransitionStatus(agentId, AgentStatus.Busy);
                }

                _logger.Information(
                    "Restored active run {RunId} for agent {AgentId} (issue {IssueIdentifier}, step {Step}) — orchestrator state recovery",
                    activeJob.RunId, agentId, activeJob.IssueIdentifier, activeJob.CurrentStep);

                _changeNotifier.NotifyChange();
            }
        }
        else
        {
            _logger.Information(
                "Agent {AgentId} reported active job {RunId} but it's already in history — ignoring stale state",
                agentId, activeJob.RunId);
        }
    }

    private void LinkAgentToExistingRun(
        PipelineRun existingRun, AgentRegistrationMessage message, string agentId, ActiveJobState activeJob)
    {
        // Run already exists in-memory (e.g., created by K8s DispatchService with AgentId=null).
        // Ensure the agent is linked to it and transitioned to Busy.
        // Guard: only link if the run is unowned OR already owned by this agent (idempotent re-registration).
        if (existingRun.AgentId is null || existingRun.AgentId == agentId)
        {
            if (existingRun.AgentId is null)
                existingRun.AgentId = agentId;

            var trackedEntry = _facade.GetByAgentId(agentId);
            if (trackedEntry is not null)
            {
                lock (trackedEntry.SyncRoot)
                {
                    if (trackedEntry.ActiveJobId is null)
                    {
                        trackedEntry.ActiveJobId = activeJob.RunId;
                    }
                }
                if (trackedEntry.ActiveJobId == activeJob.RunId)
                    _facade.TransitionStatus(agentId, AgentStatus.Busy);
            }
        }

        _logger.Debug("Agent {AgentId} active job {RunId} already tracked — linked agent to run",
            agentId, activeJob.RunId);
    }

    private void DetectAndRestoreOrphans(AgentRegistrationMessage message, string agentId, AgentEntry entry)
    {
        var orphanedRuns = _facade.GetActiveRunsByAgent(agentId);
        if (orphanedRuns.Count > 0)
        {
            // Restore the most recent orphaned run as the active job so the
            // disconnect grace period timer applies. If the agent truly lost the job,
            // the HeartbeatMonitor will fail it after the grace period expires.
            var mostRecent = orphanedRuns[^1];
            lock (entry.SyncRoot)
            {
                // Atomic check-and-set under lock: if DrainService assigned a job
                // between GetActiveRunsByAgent and this lock acquisition, don't overwrite.
                if (entry.ActiveJobId is not null)
                {
                    _logger.Information(
                        "Agent {AgentId} acquired job {ActiveJobId} between registration and orphan check, skipping orphan restoration",
                        agentId, entry.ActiveJobId);
                }
                else
                {
                    entry.ActiveJobId = mostRecent.RunId;
                    entry.OrphanRestoredAt = DateTimeOffset.UtcNow;
                }
            }

            if (entry.ActiveJobId == mostRecent.RunId)
            {
                _facade.TransitionStatus(agentId, AgentStatus.Busy);

                _logger.Warning(
                    "Agent {AgentId} re-registered without active job but orchestrator tracks {OrphanCount} orphaned run(s). " +
                    "Restoring run {RunId} (issue {IssueIdentifier}) as active — HeartbeatMonitor will clean up if agent does not resume.",
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

    private void HandleCrashRecovery(AgentRegistrationMessage message, string agentId, AgentEntry entry)
    {
        // Crash recovery detection: agent registered without an active job but the
        // registry already restored ActiveJobId (from its own prior state in the update factory).
        // This means the agent lost its in-memory state (container restart) while the orchestrator
        // still thinks it's working. Set OrphanRestoredAt so HeartbeatMonitor Phase 1.5 will
        // fail the run after the grace period if the agent doesn't report progress.
        if (message.ActiveJob is null && entry.OrphanRestoredAt is null)
        {
            lock (entry.SyncRoot)
            {
                entry.OrphanRestoredAt = DateTimeOffset.UtcNow;
            }
            _logger.Warning(
                "Agent {AgentId} re-registered without active job but orchestrator has {JobId} assigned (crash recovery). " +
                "Setting OrphanRestoredAt — HeartbeatMonitor will fail run after grace period if agent does not resume.",
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
