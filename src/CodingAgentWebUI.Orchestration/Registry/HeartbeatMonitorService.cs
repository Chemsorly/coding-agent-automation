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
    private readonly ILabelSwapper _labelSwapper;
    private readonly IConfigurationStore _configStore;
    private readonly ILogger _logger;

    public HeartbeatMonitorService(
        AgentRegistryService registry,
        OrchestratorRunService runService,
        IPipelineRunHistoryService historyService,
        JobDispatcherService dispatcher,
        ILabelSwapper labelSwapper,
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
                            var run = _runService.GetRun(orphanedJobId);
                            if (run is not null)
                            {
                                run.FailureReason = "Agent did not resume orphaned job within grace period";
                                run.MarkCompleted();
                                run.CurrentStep = PipelineStep.Failed;

                                _historyService.AddRunToHistory(run);
                                _runService.RemoveRun(orphanedJobId);
                                _dispatcher.MarkIssueComplete(run.IssueIdentifier, run.IssueProviderConfigId);

                                await TrySwapLabelToErrorAsync(run, ct);
                            }
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
                // Phase 1.6: Detect agents stuck in Busy without pipeline progress.
                // If ReportJobCompleted failed (SignalR blip) and the agent locally transitioned
                // to idle, the orchestrator still sees the agent as Busy. Detect via progress timeout.
                else if (agent is { Status: AgentStatus.Busy, OrphanRestoredAt: null } && agent.ActiveJobId is not null)
                {
                    var run = _runService.GetRun(agent.ActiveJobId);
                    // TODO: When LastStepChangeAt == default the progress check is silently skipped.
                    // Consider logging a warning so operators can detect runs missing this timestamp.
                    if (run is not null && run.LastStepChangeAt != default)
                    {
                        var progressTimeout = pipelineConfig.AgentBusyProgressTimeout;
                        var elapsed = now - run.LastStepChangeAt;
                        if (elapsed > progressTimeout)
                        {
                            // Use RemoveRun as atomic claim — if another thread (ReportJobCompleted)
                            // already removed it, we get null and skip duplicate processing.
                            var claimedRun = _runService.RemoveRun(agent.ActiveJobId);
                            if (claimedRun is not null)
                            {
                                _logger.Warning(
                                    "Agent {AgentId} stuck in Busy: job {JobId} has not progressed for {Elapsed:F0}s (timeout={Timeout}). " +
                                    "Marking run as Failed and returning agent to Idle.",
                                    agent.AgentId, agent.ActiveJobId, elapsed.TotalSeconds, progressTimeout);

                                claimedRun.FailureReason = $"Agent busy without progress for {elapsed.TotalMinutes:F0} minutes (progress timeout)";
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
                    else if (run is null)
                    {
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
                // Agent had an active job — mark the run as failed
                var run = _runService.GetRun(agent.ActiveJobId);
                if (run is not null)
                {
                    run.FailureReason = "Agent disconnected";
                    run.MarkCompleted();
                    run.CurrentStep = PipelineStep.Failed;

                    // Persist to history and remove from active runs
                    _historyService.AddRunToHistory(run);
                    _runService.RemoveRun(agent.ActiveJobId);

                    // Mark issue as no longer processing in the dispatcher
                    _dispatcher.MarkIssueComplete(run.IssueIdentifier, run.IssueProviderConfigId);

                    // Swap label to agent:error via issue provider
                    await TrySwapLabelToErrorAsync(run, ct);

                    _logger.Warning(
                        "Agent {AgentId} disconnected with active job {JobId} past grace period ({GracePeriod}), marking run as Failed",
                        agent.AgentId, agent.ActiveJobId, gracePeriod);
                }

                // Clear the active job and deregister
                lock (agent.SyncRoot)
                {
                    agent.ActiveJobId = null;
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
            run.FailureReason = "Agent deregistered (orphaned run)";
            run.MarkCompleted();
            run.CurrentStep = PipelineStep.Failed;

            _historyService.AddRunToHistory(run);
            _runService.RemoveRun(run.RunId);
            _dispatcher.MarkIssueComplete(run.IssueIdentifier, run.IssueProviderConfigId);

            await TrySwapLabelToErrorAsync(run, ct);

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
    private Task TrySwapLabelToErrorAsync(PipelineRun run, CancellationToken ct)
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

        return _labelSwapper.SwapLabelAsync(providerConfigId, run.IssueIdentifier, AgentLabels.Error, targetKind, ct);
    }
}
