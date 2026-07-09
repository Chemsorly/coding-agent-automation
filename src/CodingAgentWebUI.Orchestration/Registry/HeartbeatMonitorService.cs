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
    private readonly IRunLifecycleManager? _lifecycleManager;
    private readonly ILogger _logger;

    public HeartbeatMonitorService(
        IAgentRegistryService registry,
        IOrchestratorRunService runService,
        IPipelineRunHistoryService historyService,
        JobDispatcherService dispatcher,
        ILabelSwapper labelSwapper,
        IConfigurationStore configStore,
        ILogger logger,
        IConsolidationService? consolidationService = null,
        IRunLifecycleManager? lifecycleManager = null)
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
                        if (orphanedJobId is not null)
                        {
                            if (_lifecycleManager is not null)
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
                            else
                            {
                                // Legacy path: use RemoveRun as atomic claim (same pattern as Phase 1.6)
                                var run = _runService.RemoveRun(orphanedJobId);
                                if (run is not null)
                                {
                                    run.FailureReason = "Agent did not resume orphaned job within grace period";
                                    run.MarkCompleted();
                                    run.CurrentStep = PipelineStep.Failed;

                                    _historyService.AddRunToHistory(run);
                                    _dispatcher.MarkIssueComplete(run.IssueIdentifier, run.IssueProviderConfigId);

                                    await TrySwapLabelToErrorAsync(run, ct);
                                }

                                // Return agent to Idle so it can accept new jobs
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

                                if (_lifecycleManager is not null)
                                {
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
                                else
                                {
                                    // Use RemoveRun as atomic claim — if another thread (ReportJobCompleted)
                                    // already removed it, we get null and skip duplicate processing.
                                    var claimedRun = _runService.RemoveRun(agent.ActiveJobId);
                                    if (claimedRun is not null)
                                    {
                                        claimedRun.FailureReason = failureReason;
                                        claimedRun.MarkCompleted();
                                        claimedRun.CurrentStep = PipelineStep.Failed;

                                        _historyService.AddRunToHistory(claimedRun);
                                        _dispatcher.MarkIssueComplete(claimedRun.IssueIdentifier, claimedRun.IssueProviderConfigId);

                                        await TrySwapLabelToErrorAsync(claimedRun, ct);

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
                if (_lifecycleManager is not null)
                {
                    // NOTE: FailRunAsync internally calls ClearAgentState which transitions the agent
                    // to Idle before we Deregister below. This creates a sub-millisecond window where
                    // the agent is Idle in the registry. This is acceptable: the dispatch loop won't
                    // pick up a Disconnected-then-Idle agent in that window because Deregister follows
                    // immediately and the dispatcher checks agent.Status == Idle && connected.
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

                    _logger.Warning(
                        "Agent {AgentId} disconnected with active job {JobId} past grace period ({GracePeriod}), marking run as Failed",
                        agent.AgentId, agent.ActiveJobId, gracePeriod);
                }
                else
                {
                    // Legacy path: use RemoveRun as atomic claim to prevent duplicate processing
                    var run = _runService.RemoveRun(agent.ActiveJobId);
                    if (run is not null)
                    {
                        run.FailureReason = "Agent disconnected";
                        run.MarkCompleted();
                        run.CurrentStep = PipelineStep.Failed;

                        // Persist to history
                        _historyService.AddRunToHistory(run);

                        // Mark issue as no longer processing in the dispatcher
                        _dispatcher.MarkIssueComplete(run.IssueIdentifier, run.IssueProviderConfigId);

                        // Swap label to agent:error via issue provider
                        await TrySwapLabelToErrorAsync(run, ct);

                        _logger.Warning(
                            "Agent {AgentId} disconnected with active job {JobId} past grace period ({GracePeriod}), marking run as Failed",
                            agent.AgentId, agent.ActiveJobId, gracePeriod);
                    }

                    // Clear the active job
                    lock (agent.SyncRoot)
                    {
                        agent.ActiveJobId = null;
                    }
                }
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
            if (_lifecycleManager is not null)
            {
                await _lifecycleManager.FailRunAsync(run.RunId, "Agent deregistered (orphaned run)", ct);
            }
            else
            {
                run.FailureReason = "Agent deregistered (orphaned run)";
                run.MarkCompleted();
                run.CurrentStep = PipelineStep.Failed;

                _historyService.AddRunToHistory(run);
                _runService.RemoveRun(run.RunId);
                _dispatcher.MarkIssueComplete(run.IssueIdentifier, run.IssueProviderConfigId);

                await TrySwapLabelToErrorAsync(run, ct);
            }

            _logger.Warning(
                "Orphaned run {RunId} for issue {IssueIdentifier} — agent {AgentId} no longer in registry, marking Failed",
                run.RunId, run.IssueIdentifier, run.AgentId);
        }
    }

    /// <summary>
    /// Attempts to swap the label to <see cref="AgentLabels.Error"/> via the correct provider.
    /// Routes to repo provider for PR review runs, issue provider for all others.
    /// Failures are logged but do not propagate — label swap is best-effort during cleanup.
    /// </summary>
    private async Task TrySwapLabelToErrorAsync(PipelineRun run, CancellationToken ct)
    {
        var targetKind = run.RunType == PipelineRunType.Review
            ? LabelTargetKind.PullRequest
            : LabelTargetKind.Issue;

        var providerConfigId = targetKind == LabelTargetKind.PullRequest
            ? run.RepoProviderConfigId
            : run.IssueProviderConfigId;

        _logger.Warning(
            "HeartbeatMonitor swapping label to agent:error for run {RunId} (issue={IssueIdentifier}, agent={AgentId}, reason={FailureReason})",
            run.RunId, run.IssueIdentifier, run.AgentId, run.FailureReason);

        try
        {
            // TODO: Pass expectedCurrentLabel: AgentLabels.InProgress for state machine validation (#1046)
            await _labelSwapper.SwapLabelAsync(providerConfigId, run.IssueIdentifier, AgentLabels.Error, targetKind, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Propagate cancellation — caller (SweepAsync) handles shutdown
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex,
                "HeartbeatMonitor failed to swap label to agent:error for run {RunId} (issue={IssueIdentifier}) — label may be stale",
                run.RunId, run.IssueIdentifier);
        }
    }
}
