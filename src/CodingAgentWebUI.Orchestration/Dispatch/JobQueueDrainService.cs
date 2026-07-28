using System.Diagnostics;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Telemetry;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Background service that periodically drains the job queue by matching
/// queued jobs to idle agents. Centralises all dispatch decisions into a
/// single serialised loop, eliminating race conditions that arise when
/// multiple agents signal readiness concurrently from SignalR hub methods.
///
/// <para>
/// The service wakes on two triggers:
/// <list type="bullet">
///   <item>A periodic sweep (default every 10 seconds) as a safety net.</item>
///   <item>An explicit signal via <see cref="Signal"/> when an agent becomes
///         idle or a new job is enqueued, providing near-instant dispatch.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Priority:</b> Pipeline jobs are drained before consolidation jobs (intentional).
/// </para>
/// </summary>
public sealed class JobQueueDrainService : BackgroundService
{
    private readonly JobDeduplicationGuardService _dispatcher;
    private readonly IAgentRegistryService _registry;
    private readonly IJobDispatcher _jobDispatcher;
    private readonly IConfigurationStore _configStore;
    private readonly IConsolidationDispatchService _consolidationDispatcher;
    private readonly IConsolidationRunStore? _consolidationRunStore;
    private readonly IShutdownSignal _shutdownSignal;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _wakeSignal = new(0, int.MaxValue);

    /// <summary>
    /// Default interval between periodic sweeps when no explicit signal arrives.
    /// </summary>
    internal static readonly TimeSpan DefaultDrainInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum number of failed dispatch attempts for consolidation jobs before
    /// transitioning to Failed status. Non-consolidation jobs (implementation, review,
    /// decomposition) are not subject to this limit.
    /// </summary>
    internal const int MaxConsolidationRetries = 5;

    internal JobQueueDrainService(
        JobDeduplicationGuardService dispatcher,
        IAgentRegistryService registry,
        IJobDispatcher jobDispatcher,
        IConfigurationStore configStore,
        IConsolidationDispatchService consolidationDispatcher,
        IShutdownSignal shutdownSignal,
        ILogger logger,
        IConsolidationRunStore? consolidationRunStore = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(jobDispatcher);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(consolidationDispatcher);
        ArgumentNullException.ThrowIfNull(shutdownSignal);
        ArgumentNullException.ThrowIfNull(logger);

        _dispatcher = dispatcher;
        _registry = registry;
        _jobDispatcher = jobDispatcher;
        _configStore = configStore;
        _consolidationDispatcher = consolidationDispatcher;
        _consolidationRunStore = consolidationRunStore;
        _shutdownSignal = shutdownSignal;
        _logger = logger;
    }

    /// <summary>
    /// Wakes the drain loop immediately so it can attempt dispatch without
    /// waiting for the next periodic tick. Safe to call from any thread.
    /// </summary>
    public void Signal()
    {
        // Release is a no-op if the semaphore is already at max, so this is
        // safe to call multiple times between drain cycles.
        try { _wakeSignal.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information(
            "JobQueueDrainService started, sweep interval: {Interval}s",
            DefaultDrainInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for either a signal or the periodic timeout
                await _wakeSignal.WaitAsync(DefaultDrainInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            try
            {
                await DrainAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "JobQueueDrainService drain cycle failed");
            }
        }

        _logger.Information("JobQueueDrainService stopped");
    }

    /// <summary>
    /// Performs a single drain cycle: for each idle agent, attempts to dequeue
    /// a compatible job and dispatch it. Pipeline jobs are prioritized over
    /// consolidation jobs (intentional design decision).
    /// Exposed as internal for testing.
    /// </summary>
    internal async Task DrainAsync(CancellationToken ct)
    {
        if (_shutdownSignal.IsShuttingDown)
            return;

        using var activity = PipelineTelemetry.ActivitySource.StartActivity("DrainCycle");

        try
        {
            // Drain pipeline and consolidation jobs from the unified in-memory queue.
            // Pipeline jobs are dispatched first (from DequeueForAgent label matching).
            // Consolidation jobs (detected via PendingJob.IsConsolidation) use TryDispatchToAgentAsync.
            var dispatched = await DrainPipelineJobsAsync(ct);

            activity?.SetTag("jobs_dispatched", dispatched);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            throw;
        }
    }

    private async Task<int> DrainPipelineJobsAsync(CancellationToken ct)
    {
        var queueLength = _dispatcher.QueueLength;
        if (queueLength == 0)
            return 0;

        var idleAgents = _registry.GetIdleAgents();
        if (idleAgents.Count == 0)
            return 0;

        _logger.Debug(
            "Drain cycle: {QueueLength} queued pipeline job(s), {IdleAgents} idle agent(s)",
            queueLength, idleAgents.Count);

        var dispatchedCount = 0;

        foreach (var agent in idleAgents)
        {
            if (ct.IsCancellationRequested || _shutdownSignal.IsShuttingDown)
                break;

            var pendingJob = _dispatcher.DequeueForAgent(agent);
            if (pendingJob is null)
                continue;

            PipelineTelemetry.QueueWaitTime.Record(
                (DateTimeOffset.UtcNow - pendingJob.EnqueuedAt).TotalSeconds);

            _logger.Information(
                "Drain: dequeued job for issue {IssueIdentifier} → agent {AgentId}",
                pendingJob.IssueIdentifier, agent.AgentId);

            // Re-check after dequeue — if shutdown was signalled while we were selecting,
            // put the job back rather than dispatching into a cancellation.
            if (_shutdownSignal.IsShuttingDown)
            {
                _logger.Information(
                    "Drain: shutdown signalled, re-enqueuing job for issue {IssueIdentifier}",
                    pendingJob.IssueIdentifier);
                _dispatcher.ReEnqueue(pendingJob);
                break;
            }

            try
            {
                // Consolidation jobs: dispatch via IConsolidationDispatchService
                if (pendingJob.IsConsolidation)
                {
                    // Cancel-during-dispatch race guard
                    if (_consolidationRunStore is not null)
                    {
                        var run = await _consolidationRunStore.GetByIdAsync(pendingJob.IssueIdentifier, ct);
                        if (run is null ||
                            run.Status == Pipeline.Models.ConsolidationRunStatus.Cancelled ||
                            run.Status == Pipeline.Models.ConsolidationRunStatus.Failed)
                        {
                            _logger.Information(
                                "Drain: consolidation job {RunId} is cancelled/failed, discarding",
                                pendingJob.IssueIdentifier);
                            _dispatcher.MarkIssueComplete(pendingJob.IssueIdentifier, pendingJob.IssueProviderId);
                            continue;
                        }
                    }

                    var consolidationDispatched = await _consolidationDispatcher.TryDispatchToAgentAsync(
                        pendingJob.IssueIdentifier,
                        pendingJob.ConsolidationRunType!.Value,
                        pendingJob.ConsolidationTemplateId,
                        pendingJob.ConsolidationWorkspacePath ?? "",
                        agent.AgentId,
                        ct);

                    if (consolidationDispatched)
                    {
                        dispatchedCount++;
                        _dispatcher.MarkIssueComplete(pendingJob.IssueIdentifier, pendingJob.IssueProviderId);
                    }
                    else
                    {
                        pendingJob.RetryCount++;
                        if (pendingJob.RetryCount >= MaxConsolidationRetries)
                        {
                            _logger.Error(
                                "Drain: consolidation job {RunId} failed dispatch {AttemptCount} times, marking as Failed",
                                pendingJob.IssueIdentifier, pendingJob.RetryCount);
await FailConsolidationAsync(pendingJob, CancellationToken.None);
                    }
                    else
                    {
                        _logger.Warning(
                            "Drain: failed to dispatch consolidation job {RunId} (attempt {Attempt}/{Max}), re-enqueuing",
                                pendingJob.IssueIdentifier, pendingJob.RetryCount, MaxConsolidationRetries);
                            _dispatcher.ReEnqueue(pendingJob);
                        }
                    }
                }
                else
                {
                    // Pipeline jobs: existing dispatch path
                    var requiredLabels = await ResolveRequiredLabelsAsync(pendingJob, ct);

                    var dispatched = await _jobDispatcher.DispatchToAgentDirectAsync(
                        agent, pendingJob, requiredLabels, ct);

                    if (dispatched)
                    {
                        dispatchedCount++;
                        // Release the dedup entry after successful dispatch.
                        // NOTE: There is a narrow race window between this call and the next poll cycle —
                        // the run is already registered in OrchestratorRunService (via CreateDispatchedRunAsync),
                        // so IsIssueBeingProcessed at the loop level guards against re-enqueue.
                        _dispatcher.MarkIssueComplete(pendingJob.IssueIdentifier, pendingJob.IssueProviderId);
                    }
                    else
                    {
                        _logger.Warning(
                            "Drain: failed to dispatch job for issue {IssueIdentifier}, re-enqueuing",
                            pendingJob.IssueIdentifier);
                        _dispatcher.ReEnqueue(pendingJob);
                    }
                }
            }
            catch (Exception ex)
            {
                if (pendingJob.IsConsolidation)
                {
                    pendingJob.RetryCount++;
                    if (pendingJob.RetryCount >= MaxConsolidationRetries)
                    {
                        _logger.Error(ex,
                            "Drain: consolidation job {RunId} failed dispatch {AttemptCount} times (exception), marking as Failed",
                            pendingJob.IssueIdentifier, pendingJob.RetryCount);
                        await FailConsolidationAsync(pendingJob, CancellationToken.None);
                    }
                    else
                    {
                        _logger.Error(ex,
                            "Drain: exception dispatching consolidation job {RunId} (attempt {Attempt}/{Max}), re-enqueuing",
                            pendingJob.IssueIdentifier, pendingJob.RetryCount, MaxConsolidationRetries);
                        _dispatcher.ReEnqueue(pendingJob);
                    }
                }
                else
                {
                    _logger.Error(ex,
                        "Drain: exception dispatching job for issue {IssueIdentifier} to agent {AgentId}, re-enqueuing",
                        pendingJob.IssueIdentifier, agent.AgentId);
                    _dispatcher.ReEnqueue(pendingJob);
                }
            }
        }

        return dispatchedCount;
    }

    private async Task FailConsolidationAsync(Pipeline.Models.PendingJob job, CancellationToken ct)
    {
        // Callers pass CancellationToken.None to ensure the terminal transition
        // completes even if the original drain cycle was cancelled. A cancelled token
        // would cause SaveRunAsync to throw, leaving the run in a non-terminal state.

        // Release dedup entry unconditionally — this prevents the job from being
        // re-enqueued again even if the store persist step silently fails.
        _dispatcher.MarkIssueComplete(job.IssueIdentifier, job.IssueProviderId);

        if (_consolidationRunStore is not null)
        {
            var run = await _consolidationRunStore.GetByIdAsync(job.IssueIdentifier, ct);
            if (run is not null)
            {
                // Guard: don't overwrite already-terminal runs (Succeeded / Failed / Cancelled).
                // This prevents the retry-driven failure from stomping on a concurrent success
                // from HeartbeatMonitor or manual UI cancellation.
                // TODO: Add test verifying this guard works when HeartbeatMonitor concurrently
                // marks the run as terminal before the 5th retry's FailConsolidationAsync call.
                if (run.Status is Pipeline.Models.ConsolidationRunStatus.Succeeded
                    or Pipeline.Models.ConsolidationRunStatus.Failed
                    or Pipeline.Models.ConsolidationRunStatus.Cancelled)
                {
                    _logger.Debug(
                        "Skipping failure transition for consolidation run {RunId}: already terminal ({Status})",
                        job.IssueIdentifier, run.Status);
                    return;
                }

                run.Status = Pipeline.Models.ConsolidationRunStatus.Failed;
                run.Summary = $"Max dispatch retries exhausted ({job.RetryCount} attempts)";
                run.CompletedAtUtc = DateTimeOffset.UtcNow;
                await _consolidationRunStore.SaveRunAsync(run, ct);
                _logger.Information(
                    "Consolidation run {RunId} transitioned to Failed: max retries exhausted",
                    job.IssueIdentifier);
            }
            else
            {
                _logger.Warning("Cannot fail consolidation run {RunId}: not found in store", job.IssueIdentifier);
            }
        }
    }

    private async Task<IReadOnlyList<string>> ResolveRequiredLabelsAsync(Pipeline.Models.PendingJob job, CancellationToken ct)
    {
        // Use the job's pre-resolved labels if available
        if (job.RequiredLabels.Count > 0)
            return job.RequiredLabels;

        // Fall back to resolving from config
        var pipelineConfig = await _configStore.LoadPipelineConfigAsync(ct);
        var repoConfig = await _configStore.GetProviderConfigByIdAsync(
            job.RepoProviderId, Pipeline.Models.ProviderKind.Repository, ct);
        return JobDeduplicationGuardService.ResolveRequiredLabels(repoConfig, pipelineConfig);
    }

}
