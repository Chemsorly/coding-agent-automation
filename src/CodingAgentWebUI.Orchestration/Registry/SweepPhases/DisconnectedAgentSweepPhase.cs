using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Registry.SweepPhases;

/// <summary>
/// Phase 2: Disconnected-agent cleanup.
/// Applies only to agents with <see cref="AgentStatus.Disconnected"/> status.
/// Deregisters agents whose disconnection grace period has expired, optionally
/// failing any active run first.
/// </summary>
internal sealed class DisconnectedAgentSweepPhase : ISweepPhase
{
    private readonly IAgentRegistryService _registry;
    private readonly IRunLifecycleManager _lifecycleManager;
    private readonly ILogger _logger;

    public DisconnectedAgentSweepPhase(
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
        if (agent.Status != AgentStatus.Disconnected)
            return false;

        DateTimeOffset? disconnectedAt;
        lock (agent.SyncRoot) { disconnectedAt = agent.DisconnectedAt; }

        if (disconnectedAt is null)
            return true; // consumed — nothing to do

        var gracePeriod = config.AgentDisconnectGracePeriod;
        var disconnectedDuration = now - disconnectedAt.Value;
        if (disconnectedDuration <= gracePeriod)
            return true; // consumed — still within grace

        // Grace period expired
        if (agent.ActiveJobId is not null)
        {
            var jobId = agent.ActiveJobId;

            // NOTE: FailRunAsync internally calls ClearAgentState which transitions the agent
            // to Idle before we Deregister below. This creates a sub-millisecond window where
            // the agent is Idle in the registry. This is acceptable: the dispatch loop won't
            // pick up a Disconnected-then-Idle agent in that window because Deregister follows
            // immediately and the dispatcher checks agent.Status == Idle && connected.
            var result = await _lifecycleManager.FailRunAsync(jobId, "Agent disconnected", ct, FailureReason.InfrastructureFailure);
            if (result is null)
            {
                // Race lost — run already processed by another path.
                // Agent will still be deregistered below.
                lock (agent.SyncRoot)
                {
                    agent.ActiveJobId = null;
                }
            }

            _logger.Warning(
                "Agent {AgentId} disconnected with active job {JobId} past grace period ({GracePeriod}), marking run as Failed",
                agent.AgentId, jobId, gracePeriod);
        }
        else
        {
            _logger.Information(
                "Agent {AgentId} disconnected without active job past grace period, deregistering",
                agent.AgentId);
        }

        _registry.Deregister(agent.AgentId);
        return true;
    }
}
