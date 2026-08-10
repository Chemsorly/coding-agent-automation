using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Registry.SweepPhases;

/// <summary>
/// Phase 1.6 / 1.7: Progress-timeout detection.
/// Applies only to Busy agents where <see cref="AgentEntry.OrphanRestoredAt"/> is null
/// AND <see cref="AgentEntry.ActiveJobId"/> is not null.
/// <para>
/// Contains three mutually-exclusive sub-phases (as private helpers):
/// <list type="bullet">
///   <item>Run found, elapsed &gt; timeout → fail run (Phase 1.6)</item>
///   <item>Run not found, consolidation run → check consolidation timeout (Phase 1.7)</item>
///   <item>Run not found, not consolidation → check BusySince grace period</item>
/// </list>
/// </para>
/// </summary>
internal sealed class ProgressTimeoutSweepPhase : ISweepPhase
{
    /// <summary>
    /// Grace period for the ResolveAgent → AssignJob/run-registration window.
    /// Must exceed the worst-case drain operation duration under load (DB pool
    /// exhaustion, SignalR backpressure). 30 s is ~6× typical drain duration (~5 s)
    /// and still well below the progress timeout (60 min default).
    /// </summary>
    private static readonly TimeSpan BusySinceGracePeriod = TimeSpan.FromSeconds(30);

    private readonly IAgentRegistryService _registry;
    private readonly IOrchestratorRunService _runService;
    private readonly IRunLifecycleManager _lifecycleManager;
    private readonly IConsolidationService? _consolidationService;
    private readonly ILogger _logger;

    public ProgressTimeoutSweepPhase(
        IAgentRegistryService registry,
        IOrchestratorRunService runService,
        IRunLifecycleManager lifecycleManager,
        IConsolidationService? consolidationService,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runService);
        ArgumentNullException.ThrowIfNull(lifecycleManager);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _runService = runService;
        _lifecycleManager = lifecycleManager;
        _consolidationService = consolidationService;
        _logger = logger;
    }

    public async Task<bool> ExecuteAsync(AgentEntry agent, DateTimeOffset now, PipelineConfiguration config, CancellationToken ct)
    {
        if (agent.Status != AgentStatus.Busy)
            return false;

        // Guard: only process when ActiveJobId is set. This was enforced by the original
        // coordinator's else if (agent.ActiveJobId is not null) — make it explicit here.
        if (agent.ActiveJobId is null)
            return false;

        await SweepProgressTimeoutsAsync(agent, now, config, ct);
        // TODO: [WARNING] This phase always returns false even when it mutates agent state (fails a run,
        // resets to Idle). The ISweepPhase contract: returning true means "consumed — skip remaining phases".
        // Currently safe because ProgressTimeoutSweepPhase is intentionally the terminal phase in
        // _connectedAgentPhases, but a maintainer appending a new phase after this one would not realize
        // that an agent already reset to Idle by this phase will still be processed. Consider returning
        // true when agent state is mutated, or enforce "terminal" position with a comment at the call-site.
        return false;
    }

    private async Task SweepProgressTimeoutsAsync(AgentEntry agent, DateTimeOffset now,
        PipelineConfiguration pipelineConfig, CancellationToken ct)
    {
        // Capture ActiveJobId once into a local variable. agent.ActiveJobId is a mutable property
        // without synchronization on reads — another thread (e.g. ReportJobCompleted) can null it
        // out at any time. All subsequent uses of the job ID in this method and its callees use
        // the captured local, eliminating the TOCTOU window.
        var jobId = agent.ActiveJobId;
        if (jobId is null) return; // defense-in-depth after ExecuteAsync guard

        var run = _runService.GetRun(jobId);
        if (run is not null)
        {
            var referenceTime = GetProgressReferenceTime(run);

            if (referenceTime == default)
            {
                _logger.Warning(
                    "Run {RunId} has no valid timestamp for progress check " +
                    "(LastStepChangeAt and StartedAtOffset both default) — skipping stall detection",
                    run.RunId);
                return;
            }

            var progressTimeout = pipelineConfig.AgentBusyProgressTimeout;
            var elapsed = now - referenceTime;
            if (elapsed > progressTimeout)
            {
                _logger.Warning(
                    "Agent {AgentId} stuck in Busy: job {JobId} has not progressed for {Elapsed:F0}s (timeout={Timeout}). " +
                    "Marking run as Failed and returning agent to Idle.",
                    agent.AgentId, jobId, elapsed.TotalSeconds, progressTimeout);

                var failureReason = $"Agent busy without progress for {elapsed.TotalMinutes:F0} minutes (progress timeout)";

                // TODO: Pass FailureReason.Timeout as the enum parameter to match
                // ReconciliationService's timeout path which explicitly uses FailureReason.Timeout.
                await FailStuckProgressRunAsync(agent, jobId, failureReason, ct);
            }
        }
        else
        {
            // Run not found — check consolidation or BusySince grace period
            if (await SweepStuckConsolidationRunsAsync(agent, jobId, now, pipelineConfig, ct))
            {
                return;
            }

            SweepBusySinceGracePeriod(agent, jobId, now);
        }
    }

    private DateTimeOffset GetProgressReferenceTime(PipelineRun run)
    {
        if (run.LastStepChangeAt != default)
            return run.LastStepChangeAt;

        if (run.StartedAtOffset != default)
        {
            _logger.Warning(
                "Run {RunId} has no LastStepChangeAt — using StartedAtOffset as fallback for progress check",
                run.RunId);
            return run.StartedAtOffset;
        }

        return default;
    }

    private async Task FailStuckProgressRunAsync(AgentEntry agent, string jobId, string failureReason, CancellationToken ct)
    {
        var result = await _lifecycleManager.FailRunAsync(jobId, failureReason, ct);
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

    /// <summary>
    /// Phase 1.7: Detect stuck consolidation runs exceeding progress timeout.
    /// Consolidation runs don't have PipelineRun entries in the run service,
    /// so the standard progress timeout (Phase 1.6) doesn't apply.
    /// Returns true if the agent's ActiveJobId is a consolidation run (handled or not).
    /// </summary>
    private async Task<bool> SweepStuckConsolidationRunsAsync(AgentEntry agent, string jobId, DateTimeOffset now,
        PipelineConfiguration pipelineConfig, CancellationToken ct)
    {
        // Check if this is a consolidation run — those are tracked separately
        if (_consolidationService?.IsRunActive(jobId) != true)
        {
            return false;
        }

        // Use the consolidation run's StartedAtUtc instead.
        var consolidationStartedAt = _consolidationService.GetActiveRunStartedAt(jobId);
        if (consolidationStartedAt.HasValue)
        {
            var consolidationElapsed = now - consolidationStartedAt.Value;
            var consolidationTimeout = pipelineConfig.AgentBusyProgressTimeout;
            if (consolidationElapsed > consolidationTimeout)
            {
                _logger.Warning(
                    "Agent {AgentId} consolidation run {RunId} stuck for {ElapsedMin:F0} minutes (progress timeout: {TimeoutMin:F0} min) — failing run",
                    agent.AgentId, jobId, consolidationElapsed.TotalMinutes, consolidationTimeout.TotalMinutes);

                var failReason = $"Consolidation run exceeded progress timeout ({consolidationElapsed.TotalMinutes:F0} minutes > {consolidationTimeout.TotalMinutes:F0} minute limit)";
                await _consolidationService.UpdateRunAsync(
                    jobId, ConsolidationRunStatus.Failed, failReason, ct);

                lock (agent.SyncRoot)
                {
                    agent.ActiveJobId = null;
                    agent.OrphanRestoredAt = null;
                }
                _registry.TransitionStatus(agent.AgentId, AgentStatus.Idle);
            }
        }

        return true;
    }

    /// <summary>
    /// BusySince grace period: skip reset if agent became Busy very recently.
    /// The drain service has a window between ResolveAgent (sets Busy) and
    /// AssignJob/run registration — avoid resetting during this window (BUG-03).
    /// If past the grace period, resets the agent to Idle.
    /// </summary>
    private void SweepBusySinceGracePeriod(AgentEntry agent, string jobId, DateTimeOffset now)
    {
        DateTimeOffset? busySince;
        lock (agent.SyncRoot) { busySince = agent.BusySince; }
        if (busySince.HasValue && (now - busySince.Value) < BusySinceGracePeriod)
        {
            return;
        }

        _logger.Warning(
            "Agent {AgentId} is Busy with ActiveJobId {JobId} but run not found — resetting to Idle",
            agent.AgentId, jobId);
        lock (agent.SyncRoot)
        {
            agent.ActiveJobId = null;
            agent.OrphanRestoredAt = null;
        }

        _registry.TransitionStatus(agent.AgentId, AgentStatus.Idle);
    }
}
