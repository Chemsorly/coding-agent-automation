using CodingAgentWebUI.Orchestration.Registry.SweepPhases;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Registry;

/// <summary>
/// Background service that periodically sweeps the agent registry to detect
/// unresponsive agents and handle disconnection grace periods.
/// Runs every 60 seconds. Registered as a hosted service in DI.
/// </summary>
public sealed class HeartbeatMonitorService : BackgroundService
{
    private readonly IAgentRegistryService _registry;
    private readonly IConfigurationStore _configStore;
    private readonly ILogger _logger;

    /// <summary>Phases run for agents that are NOT Disconnected (in order).</summary>
    private readonly IReadOnlyList<ISweepPhase> _connectedAgentPhases;

    /// <summary>Phases run for agents that ARE Disconnected (in order).</summary>
    private readonly IReadOnlyList<ISweepPhase> _disconnectedAgentPhases;

    private readonly OrphanedRunSweepPhase _orphanedRunPhase;

    public HeartbeatMonitorService(
        HeartbeatMonitorDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentNullException.ThrowIfNull(deps.Registry);
        ArgumentNullException.ThrowIfNull(deps.RunService);
        ArgumentNullException.ThrowIfNull(deps.HistoryService);
        ArgumentNullException.ThrowIfNull(deps.ConfigStore);
        ArgumentNullException.ThrowIfNull(deps.Logger);
        ArgumentNullException.ThrowIfNull(deps.LifecycleManager);

        _registry = deps.Registry;
        _configStore = deps.ConfigStore;
        _logger = deps.Logger;

        _connectedAgentPhases = new ISweepPhase[]
        {
            new ChatAgentSweepPhase(deps.Logger),
            new StaleHeartbeatSweepPhase(deps.Registry, deps.Logger),
            new OrphanRestoredJobSweepPhase(deps.Registry, deps.LifecycleManager, deps.Logger),
            // TODO: [WARNING] ProgressTimeoutSweepPhase is intentionally the terminal phase for connected
            // agents — it always returns false from ExecuteAsync so the phase-break logic in SweepAsync
            // never fires for it. If a new phase is ever appended after this one, be aware that agents
            // already acted upon by ProgressTimeoutSweepPhase (run failed, agent reset to Idle) will
            // still be passed to the new phase. Either change ProgressTimeoutSweepPhase to return true
            // when it mutates state, or verify the new phase gracefully handles already-reset agents.
            new ProgressTimeoutSweepPhase(deps.Registry, deps.RunService, deps.LifecycleManager, deps.ConsolidationService, deps.Logger),
        };

        _disconnectedAgentPhases = new ISweepPhase[]
        {
            new DisconnectedAgentSweepPhase(deps.Registry, deps.LifecycleManager, deps.Logger),
        };

        _orphanedRunPhase = new OrphanedRunSweepPhase(deps.Registry, deps.RunService, deps.LifecycleManager, deps.Logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = await _configStore.LoadPipelineConfigAsync(stoppingToken);
        const int MinSweepIntervalSeconds = 5;
        var intervalSeconds = config.HeartbeatSweepIntervalSeconds;
        if (intervalSeconds < MinSweepIntervalSeconds)
        {
            _logger.Warning("HeartbeatSweepIntervalSeconds ({Configured}) is below minimum, clamping to {Min}s",
                intervalSeconds, MinSweepIntervalSeconds);
            intervalSeconds = MinSweepIntervalSeconds;
        }

        var sweepInterval = TimeSpan.FromSeconds(intervalSeconds);
        _logger.Information("HeartbeatMonitorService started, sweep interval: {Interval}s", sweepInterval.TotalSeconds);

        using var timer = new PeriodicTimer(sweepInterval);

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "HeartbeatMonitorService sweep failed");
            }
        }

        _logger.Information("HeartbeatMonitorService stopped");
    }

    /// <summary>
    /// Performs a single sweep: routes each agent through the appropriate phase list,
    /// then runs the orphaned-run phase. Exposed as internal for testing.
    /// </summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        // TODO: `now` is captured once per sweep. If iterating many agents takes significant time,
        // elapsed calculations may be slightly stale. Acceptable with 60-min default timeout.
        var now = DateTimeOffset.UtcNow;
        var agents = _registry.GetAllAgents();
        var pipelineConfig = await _configStore.LoadPipelineConfigAsync(ct);

        foreach (var agent in agents)
        {
            var phases = agent.Status != AgentStatus.Disconnected
                ? _connectedAgentPhases
                : _disconnectedAgentPhases;

            foreach (var phase in phases)
            {
                if (await phase.ExecuteAsync(agent, now, pipelineConfig, ct))
                    break; // phase consumed agent — skip remaining phases for this agent
            }
        }

        // TODO: No unit tests cover HeartbeatMonitorService invoking _orphanedRunPhase.ExecuteAsync.
        // If the call site or sweep phase signature changes, add tests at the HeartbeatMonitorService layer to catch regressions.
        await _orphanedRunPhase.ExecuteAsync(ct);
    }
}
