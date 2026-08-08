using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Registry.SweepPhases;

/// <summary>
/// Phase 1.5: Orphaned-restored-job detection.
/// Applies only to Busy agents that have <see cref="AgentEntry.OrphanRestoredAt"/> set,
/// meaning an orphaned run was re-assigned to this agent during re-registration.
/// <para>
/// Returns <c>true</c> (consume agent) on BOTH the within-grace path (no action) and
/// the past-grace path (run failed). This preserves the original <c>if/else if</c>
/// mutual exclusion with <see cref="ProgressTimeoutSweepPhase"/>: if an agent has
/// <see cref="AgentEntry.OrphanRestoredAt"/> set, progress timeout must not also run.
/// </para>
/// </summary>
internal sealed class OrphanRestoredJobSweepPhase : ISweepPhase
{
    private readonly IAgentRegistryService _registry;
    private readonly IRunLifecycleManager _lifecycleManager;
    private readonly ILogger _logger;

    public OrphanRestoredJobSweepPhase(
        IAgentRegistryService registry,
        IRunLifecycleManager lifecycleManager,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(lifecycleManager);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _lifecycleManager = lifecycleManager;
        _logger = logger;
    }

    public async Task<bool> ExecuteAsync(AgentEntry agent, DateTimeOffset now, PipelineConfiguration config, CancellationToken ct)
    {
        if (agent.Status != AgentStatus.Busy)
            return false;

        // Read OrphanRestoredAt under lock — DateTimeOffset? (17 bytes) is not
        // guaranteed atomic on all platforms (e.g. ARM64).
        DateTimeOffset? orphanRestoredAt;
        lock (agent.SyncRoot) { orphanRestoredAt = agent.OrphanRestoredAt; }

        if (orphanRestoredAt is null)
            return false;

        var gracePeriod = config.AgentDisconnectGracePeriod;
        var orphanAge = now - orphanRestoredAt.Value;

        if (orphanAge > gracePeriod)
        {
            // TODO: [WARNING] TOCTOU: agent.ActiveJobId is read here without a lock, while OrphanRestoredAt
            // was read under lock above. Another thread could null out ActiveJobId between the two reads
            // (e.g. ReportJobCompleted). This is preserved pre-existing behaviour from the original code
            // (the original SweepOrphanedRestoredJobs had the same pattern) and not a regression, but
            // the unsynchronized read is now more visible across the extraction boundary. Consider
            // capturing ActiveJobId under the same lock as OrphanRestoredAt.
            var orphanedJobId = agent.ActiveJobId;
            _logger.Warning(
                "Agent {AgentId} has not resumed orphaned job {JobId} within grace period ({GracePeriod}, elapsed={OrphanAge:F0}s). " +
                "Marking run as Failed and returning agent to Idle.",
                agent.AgentId, orphanedJobId, gracePeriod, orphanAge.TotalSeconds);

            // Fail the orphaned run directly
            if (orphanedJobId is not null)
            {
                var result = await _lifecycleManager.FailRunAsync(orphanedJobId,
                    "Agent did not resume orphaned job within grace period", ct, FailureReason.InfrastructureFailure);
                if (result is null)
                {
                    // Race lost — another path (e.g., ReportJobCompleted) already processed the run.
                    // Clear agent state defensively in case the other path didn't.
                    lock (agent.SyncRoot)
                    {
                        agent.ActiveJobId = null;
                        agent.OrphanRestoredAt = null;
                    }
                    _registry.TransitionStatus(agent.AgentId, AgentStatus.Idle);
                }
            }
        }

        // Return true on BOTH paths — within grace (no action) and past grace (action taken).
        // This prevents ProgressTimeoutSweepPhase from also running when OrphanRestoredAt is set.
        return true;
    }
}
