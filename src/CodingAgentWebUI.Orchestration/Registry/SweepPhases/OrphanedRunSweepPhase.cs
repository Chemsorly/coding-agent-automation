using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Registry.SweepPhases;

/// <summary>
/// Phase 3: Orphaned-run cleanup (post-agent-loop phase).
/// Iterates all active runs and fails any whose agent is no longer registered.
/// This phase operates on the run set rather than per-agent, so it is not an
/// <see cref="ISweepPhase"/> — it is invoked directly after the per-agent loop.
/// Does not require <see cref="PipelineConfiguration"/>.
/// </summary>
internal sealed class OrphanedRunSweepPhase
{
    private readonly IAgentRegistryService _registry;
    private readonly IOrchestratorRunService _runService;
    private readonly IRunLifecycleManager _lifecycleManager;
    private readonly ILogger _logger;

    public OrphanedRunSweepPhase(
        IAgentRegistryService registry,
        IOrchestratorRunService runService,
        IRunLifecycleManager lifecycleManager,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runService);
        ArgumentNullException.ThrowIfNull(lifecycleManager);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _runService = runService;
        _lifecycleManager = lifecycleManager;
        _logger = logger;
    }

    public async Task ExecuteAsync(DateTimeOffset now, CancellationToken ct)
    {
        var activeRuns = _runService.GetActiveRuns();
        foreach (var run in activeRuns)
        {
            if (string.IsNullOrEmpty(run.AgentId))
                continue;

            if (_registry.GetByAgentId(run.AgentId) is not null)
                continue;

            // Agent gone from registry entirely — orphaned run
            await _lifecycleManager.FailRunAsync(run.RunId, "Agent deregistered (orphaned run)", ct, FailureReason.InfrastructureFailure);

            _logger.Warning(
                "Orphaned run {RunId} for issue {IssueIdentifier} — agent {AgentId} no longer in registry, marking Failed",
                run.RunId, run.IssueIdentifier, run.AgentId);
        }
    }
}
