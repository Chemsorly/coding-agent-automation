using CodingAgentWebUI.Orchestration.Dispatch;
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
    private readonly AgentRegistryService _registry;
    private readonly OrchestratorRunService _runService;
    private readonly IPipelineRunHistoryService _historyService;
    private readonly JobDispatcherService _dispatcher;
    private readonly IIssueProviderLabelSwapper _labelSwapper;
    private readonly IConfigurationStore _configStore;
    private readonly ILogger _logger;

    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(90);

    public HeartbeatMonitorService(
        AgentRegistryService registry,
        OrchestratorRunService runService,
        IPipelineRunHistoryService historyService,
        JobDispatcherService dispatcher,
        IIssueProviderLabelSwapper labelSwapper,
        IConfigurationStore configStore,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runService);
        ArgumentNullException.ThrowIfNull(historyService);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(labelSwapper);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _runService = runService;
        _historyService = historyService;
        _dispatcher = dispatcher;
        _labelSwapper = labelSwapper;
        _configStore = configStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information("HeartbeatMonitorService started, sweep interval: {Interval}s", SweepInterval.TotalSeconds);

        using var timer = new PeriodicTimer(SweepInterval);

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
    /// Performs a single sweep: detects stale heartbeats and handles grace period expiry.
    /// Exposed as internal for testing.
    /// </summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var agents = _registry.GetAllAgents();
        var pipelineConfig = await _configStore.LoadPipelineConfigAsync(ct);
        var gracePeriod = pipelineConfig.AgentDisconnectGracePeriod;

        foreach (var agent in agents)
        {
            // Phase 1: Detect stale heartbeats for non-disconnected agents
            if (agent.Status != AgentStatus.Disconnected)
            {
                var heartbeatAge = now - agent.LastHeartbeatAt;
                if (heartbeatAge > HeartbeatTimeout)
                {
                    _logger.Warning(
                        "Agent {AgentId} heartbeat stale ({Age:F0}s), transitioning to Disconnected",
                        agent.AgentId, heartbeatAge.TotalSeconds);

                    _registry.TransitionStatus(agent.AgentId, AgentStatus.Disconnected);
                }

                continue;
            }

            // Phase 2: Handle disconnected agents past grace period
            if (agent.DisconnectedAt is null)
                continue;

            var disconnectedDuration = now - agent.DisconnectedAt.Value;
            if (disconnectedDuration <= gracePeriod)
                continue;

            // Grace period expired
            if (agent.ActiveJobId is not null)
            {
                // Agent had an active job — mark the run as failed
                var run = _runService.GetRun(agent.ActiveJobId);
                if (run is not null)
                {
                    run.FailureReason = "Agent disconnected";
                    run.CompletedAt = DateTime.UtcNow;
                    run.CurrentStep = PipelineStep.Failed;

                    // Persist to history and remove from active runs
                    _historyService.AddRunToHistory(run);
                    _runService.RemoveRun(agent.ActiveJobId);

                    // Mark issue as no longer processing in the dispatcher
                    _dispatcher.MarkIssueComplete(run.IssueIdentifier);

                    // Swap label to agent:error via issue provider
                    await TrySwapLabelToErrorAsync(run, ct);

                    _logger.Warning(
                        "Agent {AgentId} disconnected with active job {JobId} past grace period ({GracePeriod}), marking run as Failed",
                        agent.AgentId, agent.ActiveJobId, gracePeriod);
                }

                // Clear the active job and deregister
                agent.ActiveJobId = null;
            }
            else
            {
                _logger.Information(
                    "Agent {AgentId} disconnected without active job past grace period, deregistering",
                    agent.AgentId);
            }

            _registry.Deregister(agent.AgentId);
        }
    }

    /// <summary>
    /// Attempts to swap the issue label to <see cref="AgentLabels.Error"/> via the issue provider.
    /// Failures are logged but do not propagate — label swap is best-effort during cleanup.
    /// </summary>
    private Task TrySwapLabelToErrorAsync(PipelineRun run, CancellationToken ct)
    {
        return _labelSwapper.SwapLabelAsync(run.IssueProviderConfigId, run.IssueIdentifier, AgentLabels.Error, ct);
    }
}
