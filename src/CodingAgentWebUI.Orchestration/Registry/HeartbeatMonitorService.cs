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
    private readonly IAgentRegistryService _registry;
    private readonly IOrchestratorRunService _runService;
    private readonly IPipelineRunHistoryService _historyService;
    private readonly JobDispatcherService _dispatcher;
    private readonly ILabelSwapper _labelSwapper;
    private readonly IConfigurationStore _configStore;
    private readonly IConsolidationService? _consolidationService;
    private readonly IRunLifecycleManager _lifecycleManager;
    private readonly ILogger _logger;

    public HeartbeatMonitorService(
        IAgentRegistryService registry,
        IOrchestratorRunService runService,
        IPipelineRunHistoryService historyService,
        JobDispatcherService dispatcher,
        ILabelSwapper labelSwapper,
        IConfigurationStore configStore,
        ILogger logger,
        IRunLifecycleManager lifecycleManager,
        IConsolidationService? consolidationService = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runService);
        ArgumentNullException.ThrowIfNull(historyService);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(labelSwapper);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(lifecycleManager);

        _registry = registry;
        _runService = runService;
        _historyService = historyService;
        _dispatcher = dispatcher;
        _labelSwapper = labelSwapper;
        _configStore = configStore;
        _consolidationService = consolidationService;
        _lifecycleManager = lifecycleManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var config = await _configStore.LoadPipelineConfigAsync(stoppingToken);
        // TODO: Add tests for clamping logic with HeartbeatSweepIntervalSeconds set to 0, -1, and 4.
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
    /// Performs a single sweep: detects stale heartbeats and handles grace period expiry.
    /// Exposed as internal for testing.
    /// </summary>
    internal async Task SweepAsync(CancellationToken ct)
    {
        // TODO: `now` is captured once per sweep. If iterating many agents takes significant time,
        // elapsed calculations may be slightly stale. Acceptable with 60-min default timeout.
        var now = DateTimeOffset.UtcNow;
        var agents = _registry.GetAllAgents();
        var pipelineConfig = await _configStore.LoadPipelineConfigAsync(ct);
        var gracePeriod = pipelineConfig.AgentDisconnectGracePeriod;
        var heartbeatTimeout = TimeSpan.FromSeconds(pipelineConfig.HeartbeatTimeoutSeconds);

        foreach (var agent in agents)
        {
            // Phase 1: Detect stale heartbeats for non-disconnected agents
            if (agent.Status != AgentStatus.Disconnected)
            {
                var heartbeatAge = now - agent.LastHeartbeatAt;
                if (heartbeatAge > heartbeatTimeout)
                {
                    _logger.Warning(
                        "Agent {AgentId} heartbeat stale ({Age:F0}s), transitioning to Disconnected",
                        agent.AgentId, heartbeatAge.TotalSeconds);

                    _registry.TransitionStatus(agent.AgentId, AgentStatus.Disconnected);
                }
                // Phase 1.5: Detect orphaned runs that the agent never resumed.
                // If the agent is Busy with a restored orphan but hasn't reported progress
                // within the grace period, the agent doesn't actually have this job.
                // Fail the run directly — no need for a second grace period since we already waited.
                else if (agent is { Status: AgentStatus.Busy, OrphanRestoredAt: not null })
                {
                    var orphanAge = now - agent.OrphanRestoredAt.Value;
                    if (orphanAge > gracePeriod)
                    {
                        var orphanedJobId = agent.ActiveJobId;
                        _logger.Warning(
                            "Agent {AgentId} has not resumed orphaned job {JobId} within grace period ({GracePeriod}, elapsed={OrphanAge:F0}s). " +
                            "Marking run as Failed and returning agent to Idle.",
                            agent.AgentId, orphanedJobId, gracePeriod, orphanAge.TotalSeconds);

                        // Fail the orphaned run directly
                        // TODO: Pass FailureReason.InfrastructureFailure as the enum parameter.
                        // Agent disconnection/orphan is an infrastructure event, not an agent logic error.
                        if (orphanedJobId is not null)
                        {
                            var result = await _lifecycleManager.FailRunAsync(orphanedJobId,
                                "Agent did not resume orphaned job within grace period", ct);
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
                }
                // Phase 1.6: Detect agents stuck in Busy without pipeline progress.
                // If ReportJobCompleted failed (SignalR blip) and the agent locally transitioned
                // to idle, the orchestrator still sees the agent as Busy. Detect via progress timeout.
                else if (agent is { Status: AgentStatus.Busy, OrphanRestoredAt: null } && agent.ActiveJobId is not null)
                {
                    var run = _runService.GetRun(agent.ActiveJobId);
                    if (run is not null)
                    {
                        var referenceTime = run.LastStepChangeAt != default
                            ? run.LastStepChangeAt
                            : run.StartedAtOffset != default
                                ? run.StartedAtOffset
                                : default;

                        if (referenceTime == default)
                        {
                            _logger.Warning(
                                "Run {RunId} has no valid timestamp for progress check " +
                                "(LastStepChangeAt and StartedAtOffset both default) — skipping stall detection",
                                run.RunId);
                        }
                        else
                        {
                            if (run.LastStepChangeAt == default)
                            {
                                _logger.Warning(
                                    "Run {RunId} has no LastStepChangeAt — using StartedAtOffset as fallback for progress check",
                                    run.RunId);
                            }

                            var progressTimeout = pipelineConfig.AgentBusyProgressTimeout;
                            var elapsed = now - referenceTime;
                            if (elapsed > progressTimeout)
                            {
                                _logger.Warning(
                                    "Agent {AgentId} stuck in Busy: job {JobId} has not progressed for {Elapsed:F0}s (timeout={Timeout}). " +
                                    "Marking run as Failed and returning agent to Idle.",
                                    agent.AgentId, agent.ActiveJobId, elapsed.TotalSeconds, progressTimeout);

                                var failureReason = $"Agent busy without progress for {elapsed.TotalMinutes:F0} minutes (progress timeout)";

                                // TODO: Pass FailureReason.Timeout as the enum parameter to match
                                // ReconciliationService's timeout path which explicitly uses FailureReason.Timeout.
                                var result = await _lifecycleManager.FailRunAsync(agent.ActiveJobId, failureReason, ct);
                                if (result is null)
                                {
                                    // Race lost — another path already processed the run.
                                    // Clear agent state defensively.
                                    lock (agent.SyncRoot)
                                    {
                                        agent.ActiveJobId = null;
                                        agent.OrphanRestoredAt = null;
                                    }
                                    _registry.TransitionStatus(agent.AgentId, AgentStatus.Idle);
                                }
                            }
                        }
                    }
                    else if (run is null)
                    {
                        // Check if this is a consolidation run — those are tracked separately
                        if (_consolidationService?.IsRunActive(agent.ActiveJobId) == true)
                        {
                            // Phase 1.7: Detect stuck consolidation runs exceeding progress timeout.
                            // Consolidation runs don't have PipelineRun entries in _runService,
                            // so the standard progress timeout (Phase 1.6) doesn't apply.
                            // Use the consolidation run's StartedAtUtc instead.
                            var consolidationStartedAt = _consolidationService.GetActiveRunStartedAt(agent.ActiveJobId);
                            if (consolidationStartedAt.HasValue)
                            {
                                var consolidationElapsed = now - consolidationStartedAt.Value;
                                var consolidationTimeout = pipelineConfig.AgentBusyProgressTimeout;
                                if (consolidationElapsed > consolidationTimeout)
                                {
                                    _logger.Warning(
                                        "Agent {AgentId} consolidation run {RunId} stuck for {ElapsedMin:F0} minutes (progress timeout: {TimeoutMin:F0} min) — failing run",
                                        agent.AgentId, agent.ActiveJobId, consolidationElapsed.TotalMinutes, consolidationTimeout.TotalMinutes);

                                    var failReason = $"Consolidation run exceeded progress timeout ({consolidationElapsed.TotalMinutes:F0} minutes > {consolidationTimeout.TotalMinutes:F0} minute limit)";
                                    await _consolidationService.UpdateRunAsync(
                                        agent.ActiveJobId, ConsolidationRunStatus.Failed, failReason, ct);

                                    lock (agent.SyncRoot)
                                    {
                                        agent.ActiveJobId = null;
                                        agent.OrphanRestoredAt = null;
                                    }
                                    _registry.TransitionStatus(agent.AgentId, AgentStatus.Idle);
                                }
                            }

                            continue;
                        }

                        // Grace period: skip reset if agent became Busy very recently.
                        // The drain service has a window between ResolveAgent (sets Busy) and
                        // AssignJob/run registration — avoid resetting during this window (BUG-03).
                        // TODO: BusySince is read without holding agent.SyncRoot. DateTimeOffset? is not
                        // guaranteed atomic on all platforms. This is consistent with existing reads of
                        // DisconnectedAt/OrphanRestoredAt in this loop, but consider reading under lock
                        // for correctness on non-x86-64 targets.
                        var busySince = agent.BusySince;
                        if (busySince.HasValue && (now - busySince.Value) < TimeSpan.FromSeconds(10))
                        {
                            continue;
                        }

                        _logger.Warning(
                            "Agent {AgentId} is Busy with ActiveJobId {JobId} but run not found — resetting to Idle",
                            agent.AgentId, agent.ActiveJobId);
                        lock (agent.SyncRoot)
                        {
                            agent.ActiveJobId = null;
                            agent.OrphanRestoredAt = null;
                        }

                        _registry.TransitionStatus(agent.AgentId, AgentStatus.Idle);
                    }
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
                // NOTE: FailRunAsync internally calls ClearAgentState which transitions the agent
                // to Idle before we Deregister below. This creates a sub-millisecond window where
                // the agent is Idle in the registry. This is acceptable: the dispatch loop won't
                // pick up a Disconnected-then-Idle agent in that window because Deregister follows
                // immediately and the dispatcher checks agent.Status == Idle && connected.
                // TODO: Pass FailureReason.InfrastructureFailure as the enum parameter.
                // Agent disconnection is an infrastructure event, not an agent logic error.
                var result = await _lifecycleManager.FailRunAsync(agent.ActiveJobId, "Agent disconnected", ct);
                if (result is null)
                {
                    // Race lost — run already processed by another path.
                    // Agent will still be deregistered below.
                    lock (agent.SyncRoot)
                    {
                        agent.ActiveJobId = null;
                    }
                }

                // TODO: agent.ActiveJobId is null here because FailRunAsync/ClearAgentState already cleared it.
                // Capture the job ID in a local variable before calling FailRunAsync to log correctly.
                _logger.Warning(
                    "Agent {AgentId} disconnected with active job {JobId} past grace period ({GracePeriod}), marking run as Failed",
                    agent.AgentId, agent.ActiveJobId, gracePeriod);
            }
            else
            {
                _logger.Information(
                    "Agent {AgentId} disconnected without active job past grace period, deregistering",
                    agent.AgentId);
            }

            _registry.Deregister(agent.AgentId);
        }

        // Phase 3: Detect orphaned runs (agent fully deregistered)
        var activeRuns = _runService.GetActiveRuns();
        foreach (var run in activeRuns)
        {
            if (string.IsNullOrEmpty(run.AgentId))
                continue;

            if (_registry.GetByAgentId(run.AgentId) is not null)
                continue;

            // Agent gone from registry entirely — orphaned run
            await _lifecycleManager.FailRunAsync(run.RunId, "Agent deregistered (orphaned run)", ct);

            _logger.Warning(
                "Orphaned run {RunId} for issue {IssueIdentifier} — agent {AgentId} no longer in registry, marking Failed",
                run.RunId, run.IssueIdentifier, run.AgentId);
        }
    }
}
