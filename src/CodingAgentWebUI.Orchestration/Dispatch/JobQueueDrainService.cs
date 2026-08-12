using System.Diagnostics;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
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
    /// Maximum number of times the drain service will attempt to dispatch a consolidation job
    /// before giving up and transitioning the run to Failed. Matches the limit from the old
    /// <c>DrainConsolidationJobsAsync</c> path that was refactored into this unified drain.
    /// Non-consolidation jobs are not subject to this limit.
    /// </summary>
    /// <remarks>
    /// TODO: This is currently a <c>const</c>. If runtime configurability is needed in the future
    /// (e.g., via appsettings or <see cref="PipelineConfiguration"/>), promote to an
    /// <c>IAppSettings</c>-backed property or a constructor-injected configuration object.
    /// </remarks>
    internal const int MaxConsolidationDispatchRetries = 5;

    internal JobQueueDrainService(
        JobQueueDrainDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentNullException.ThrowIfNull(deps.Dispatcher);
        ArgumentNullException.ThrowIfNull(deps.Registry);
        ArgumentNullException.ThrowIfNull(deps.JobDispatcher);
        ArgumentNullException.ThrowIfNull(deps.ConfigStore);
        ArgumentNullException.ThrowIfNull(deps.ConsolidationDispatcher);
        ArgumentNullException.ThrowIfNull(deps.ShutdownSignal);
        ArgumentNullException.ThrowIfNull(deps.Logger);

        _dispatcher = deps.Dispatcher;
        _registry = deps.Registry;
        _jobDispatcher = deps.JobDispatcher;
        _configStore = deps.ConfigStore;
        _consolidationDispatcher = deps.ConsolidationDispatcher;
        _consolidationRunStore = deps.ConsolidationRunStore;
        _shutdownSignal = deps.ShutdownSignal;
        _logger = deps.Logger;
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

            var dispatched = pendingJob.IsConsolidation
                ? await TryDispatchConsolidationJobAsync(pendingJob, agent, ct)
                : await TryDispatchPipelineJobAsync(pendingJob, agent, ct);

            if (dispatched)
                dispatchedCount++;
        }

        return dispatchedCount;
    }

    /// <summary>
    /// Attempts to dispatch a consolidation job. Handles the run-cancellation race guard,
    /// success (MarkIssueComplete), and failure (HandleConsolidationDispatchFailureAsync)
    /// internally. Returns true if the job was dispatched successfully.
    /// </summary>
    private async Task<bool> TryDispatchConsolidationJobAsync(
        PendingJob pendingJob,
        AgentEntry agent,
        CancellationToken ct)
    {
        try
        {
            // Cancel-during-dispatch race guard
            if (_consolidationRunStore is not null)
            {
                var run = await _consolidationRunStore.GetByIdAsync((RunId)pendingJob.IssueIdentifier.Value, ct);
                if (run is null ||
                    run.Status == Pipeline.Models.ConsolidationRunStatus.Cancelled ||
                    run.Status == Pipeline.Models.ConsolidationRunStatus.Failed)
                {
                    _logger.Information(
                        "Drain: consolidation job {RunId} is cancelled/failed, discarding",
                        pendingJob.IssueIdentifier);
                    _dispatcher.MarkIssueComplete(pendingJob.IssueIdentifier, pendingJob.IssueProviderId);
                    return false;
                }
            }

            var consolidationDispatched = await _consolidationDispatcher.TryDispatchToAgentAsync(
                pendingJob.IssueIdentifier,
                pendingJob.ConsolidationRunType!.Value,
                string.IsNullOrEmpty(pendingJob.ConsolidationTemplateId) ? (TemplateId?)null : (TemplateId)pendingJob.ConsolidationTemplateId,
                pendingJob.ConsolidationWorkspacePath ?? "",
                agent.AgentId,
                ct);

            if (consolidationDispatched)
            {
                _dispatcher.MarkIssueComplete(pendingJob.IssueIdentifier, pendingJob.IssueProviderId);
                return true;
            }

            await HandleConsolidationDispatchFailureAsync(pendingJob, ct);
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error(ex,
                "Drain: exception dispatching consolidation job for issue {IssueIdentifier} to agent {AgentId}",
                pendingJob.IssueIdentifier, agent.AgentId);
            await HandleConsolidationDispatchFailureAsync(pendingJob, ct);
            return false;
        }
    }

    /// <summary>
    /// Attempts to dispatch a pipeline job. Handles success (MarkIssueComplete) and
    /// failure (ReEnqueue) internally. Returns true if the job was dispatched successfully.
    /// </summary>
    private async Task<bool> TryDispatchPipelineJobAsync(
        PendingJob pendingJob,
        AgentEntry agent,
        CancellationToken ct)
    {
        try
        {
            var requiredLabels = await ResolveRequiredLabelsAsync(pendingJob, ct);
            var dispatched = await _jobDispatcher.DispatchToAgentDirectAsync(agent, pendingJob, requiredLabels, ct);

            if (dispatched)
            {
                // Release the dedup entry after successful dispatch.
                // NOTE: There is a narrow race window between this call and the next poll cycle —
                // the run is already registered in OrchestratorRunService (via CreateDispatchedRunAsync),
                // so IsIssueBeingProcessed at the loop level guards against re-enqueue.
                _dispatcher.MarkIssueComplete(pendingJob.IssueIdentifier, pendingJob.IssueProviderId);
                return true;
            }

            _logger.Warning(
                "Drain: failed to dispatch job for issue {IssueIdentifier}, re-enqueuing",
                pendingJob.IssueIdentifier);
            _dispatcher.ReEnqueue(pendingJob);
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error(ex,
                "Drain: exception dispatching pipeline job for issue {IssueIdentifier} to agent {AgentId}",
                pendingJob.IssueIdentifier, agent.AgentId);
            // Pipeline jobs: re-enqueue unconditionally (no retry limit)
            _dispatcher.ReEnqueue(pendingJob);
            return false;
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
            job.RepoProviderId.Value, Pipeline.Models.ProviderKind.Repository, ct);
        return JobDeduplicationGuardService.ResolveRequiredLabels(repoConfig, pipelineConfig);
    }

    /// <summary>
    /// Handles a failed consolidation job dispatch attempt by incrementing the retry counter
    /// and either re-enqueuing the job (if under the limit) or discarding it and transitioning
    /// the <see cref="ConsolidationRun"/> to Failed (if retries are exhausted).
    /// </summary>
    /// <remarks>
    /// Consolidation jobs enqueued via the legacy path use <c>IssueProviderId = "consolidation"</c>
    /// (= <see cref="Pipeline.Models.ConsolidationConstants.ProviderConfigId"/>). The
    /// <see cref="JobDeduplicationGuardService.MarkIssueComplete"/> call relies on this value
    /// to remove the correct dedup entry. If future code changes the <c>IssueProviderId</c> for
    /// consolidation jobs, the dedup removal would silently fail.
    /// </remarks>
    private async Task HandleConsolidationDispatchFailureAsync(Pipeline.Models.PendingJob pendingJob, CancellationToken ct)
    {
        var nextAttempt = pendingJob.ConsolidationDispatchAttempt + 1;

        if (nextAttempt >= MaxConsolidationDispatchRetries)
        {
            _logger.Warning(
                "Drain: consolidation job {RunId} failed dispatch {Attempt}/{Max} times, discarding and marking as Failed",
                pendingJob.IssueIdentifier, nextAttempt, MaxConsolidationDispatchRetries);

            _dispatcher.MarkIssueComplete(pendingJob.IssueIdentifier, pendingJob.IssueProviderId);
            await TryUpdateRunStatusAsync(pendingJob.IssueIdentifier, Pipeline.Models.ConsolidationRunStatus.Failed, ct);
            return;
        }

        _logger.Warning(
            "Drain: failed to dispatch consolidation job {RunId} (attempt {Attempt}/{Max}), re-enqueuing",
            pendingJob.IssueIdentifier, nextAttempt, MaxConsolidationDispatchRetries);

        pendingJob = pendingJob with { ConsolidationDispatchAttempt = nextAttempt };
        _dispatcher.ReEnqueue(pendingJob);
    }

    /// <summary>
    /// Attempts to update the consolidation run status via <see cref="IConsolidationRunStore"/>.
    /// If the store is not available (<c>null</c>), skips the update and logs a warning.
    /// If the store throws, logs the error but does not propagate — the job is already discarded
    /// from the in-memory queue, and a storage failure should not re-enqueue it indefinitely.
    /// </summary>
    /// <remarks>
    /// TODO: This method performs a read-modify-write (GetById → mutate → SaveRun) without
    /// optimistic concurrency control. A concurrent cancellation handler or admin cancel could
    /// modify <c>ConsolidationRun.Status</c> between the read and save, and this write would
    /// silently overwrite that change with <c>Failed</c>. Consider a CAS pattern or compare
    /// the status before saving and skip the write if it already changed.
    /// </remarks>
    private async Task TryUpdateRunStatusAsync(
        string runId,
        Pipeline.Models.ConsolidationRunStatus newStatus,
        CancellationToken ct)
    {
        if (_consolidationRunStore is null)
        {
            _logger.Warning(
                "Drain: cannot update consolidation run {RunId} to {Status} — no IConsolidationRunStore available",
                runId, newStatus);
            return;
        }

        try
        {
            var run = await _consolidationRunStore.GetByIdAsync(runId, ct);
            if (run is null)
            {
                _logger.Warning(
                    "Drain: consolidation run {RunId} not found, cannot update to {Status}",
                    runId, newStatus);
                return;
            }

            run.Status = newStatus;
            run.CompletedAtUtc = DateTimeOffset.UtcNow;
            await _consolidationRunStore.SaveRunAsync(run, ct);

            _logger.Information(
                "Drain: consolidation run {RunId} transitioned to {Status} after exhausting dispatch retries",
                runId, newStatus);
        }
        catch (Exception ex)
        {
            _logger.Error(ex,
                "Drain: failed to update consolidation run {RunId} status to {Status} — job already discarded from queue",
                runId, newStatus);
        }
    }

}
