using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Registry.SweepPhases;

/// <summary>
/// Phase 1: Stale-heartbeat detection.
/// If a non-disconnected agent's last heartbeat exceeds <see cref="PipelineConfiguration.HeartbeatTimeoutSeconds"/>,
/// transitions the agent to <see cref="AgentStatus.Disconnected"/> and returns <c>true</c>
/// (so the agent is not processed by further phases in this sweep iteration).
/// </summary>
internal sealed class StaleHeartbeatSweepPhase : ISweepPhase
{
    private readonly IAgentRegistryService _registry;
    private readonly ILogger _logger;

    public StaleHeartbeatSweepPhase(IAgentRegistryService registry, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _logger = logger;
    }

    public Task<bool> ExecuteAsync(AgentEntry agent, DateTimeOffset now, PipelineConfiguration config, CancellationToken ct)
    {
        var heartbeatTimeout = TimeSpan.FromSeconds(config.HeartbeatTimeoutSeconds);
        var heartbeatAge = now - agent.LastHeartbeatAt;
        if (heartbeatAge > heartbeatTimeout)
        {
            _logger.Warning(
                "Agent {AgentId} heartbeat stale ({Age:F0}s), transitioning to Disconnected",
                agent.AgentId, heartbeatAge.TotalSeconds);

            _registry.TransitionStatus(agent.AgentId, AgentStatus.Disconnected);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }
}
