using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;
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
    private readonly JobDispatcherService _dispatcher;
    private readonly AgentRegistryService _registry;
    private readonly IJobDispatcher _jobDispatcher;
    private readonly ConsolidationQueueService? _consolidationQueue;
    private readonly IConsolidationDispatcher? _consolidationDispatcher;
    private readonly IConsolidationService? _consolidationService;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _wakeSignal = new(0, int.MaxValue);

    /// <summary>
    /// Default interval between periodic sweeps when no explicit signal arrives.
    /// </summary>
    internal static readonly TimeSpan DefaultDrainInterval = TimeSpan.FromSeconds(10);

    public JobQueueDrainService(
        JobDispatcherService dispatcher,
        AgentRegistryService registry,
        IJobDispatcher jobDispatcher,
        ILogger logger,
        ConsolidationQueueService? consolidationQueue = null,
        IConsolidationDispatcher? consolidationDispatcher = null,
        IConsolidationService? consolidationService = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(jobDispatcher);
        ArgumentNullException.ThrowIfNull(logger);

        _dispatcher = dispatcher;
        _registry = registry;
        _jobDispatcher = jobDispatcher;
        _logger = logger;
        _consolidationQueue = consolidationQueue;
        _consolidationDispatcher = consolidationDispatcher;
        _consolidationService = consolidationService;
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
        var pipelineQueueLength = _dispatcher.QueueLength;
        var consolidationQueueLength = _consolidationQueue?.QueueLength ?? 0;

        if (pipelineQueueLength == 0 && consolidationQueueLength == 0)
            return;

        var idleAgents = _registry.GetIdleAgents();
        if (idleAgents.Count == 0)
            return;

        _logger.Debug(
            "Drain cycle: {PipelineQueue} pipeline job(s), {ConsolidationQueue} consolidation job(s), {IdleAgents} idle agent(s)",
            pipelineQueueLength, consolidationQueueLength, idleAgents.Count);

        foreach (var agent in idleAgents)
        {
            if (ct.IsCancellationRequested)
                break;

            // Priority 1: Pipeline jobs
            if (_dispatcher.QueueLength > 0)
            {
                var pendingJob = _dispatcher.DequeueForAgent(agent);
                if (pendingJob is not null)
                {
                    await DispatchPipelineJobAsync(pendingJob, agent, ct);
                    continue;
                }
            }

            // Priority 2: Consolidation jobs
            if (_consolidationQueue is not null && _consolidationDispatcher is not null && _consolidationQueue.QueueLength > 0)
            {
                var consolidationJob = _consolidationQueue.DequeueForAgent(agent);
                if (consolidationJob is not null)
                {
                    await DispatchConsolidationJobAsync(consolidationJob, agent, ct);
                }
            }
        }
    }

    private async Task DispatchPipelineJobAsync(PendingJob pendingJob, AgentEntry agent, CancellationToken ct)
    {
        _logger.Information(
            "Drain: dequeued job for issue {IssueIdentifier} → agent {AgentId}",
            pendingJob.IssueIdentifier, agent.AgentId);

        try
        {
            // Clear the dedup entry so TryDispatchAsync doesn't reject it
            _dispatcher.MarkIssueComplete(pendingJob.IssueIdentifier);

            bool dispatched;
            if (pendingJob.RunType == Pipeline.Models.PipelineRunType.Review)
            {
                dispatched = await _jobDispatcher.TryDispatchReviewAsync(
                    pendingJob.IssueIdentifier,
                    pendingJob.PrBranchName!,
                    pendingJob.IssueTitle ?? $"PR #{pendingJob.IssueIdentifier}",
                    pendingJob.PrDescription ?? string.Empty,
                    pendingJob.PrUrl ?? string.Empty,
                    pendingJob.PrTargetBranch ?? "main",
                    pendingJob.IssueProviderId,
                    pendingJob.RepoProviderId,
                    pendingJob.BrainProviderId,
                    pendingJob.InitiatedBy,
                    ct);
            }
            else
            {
                dispatched = await _jobDispatcher.TryDispatchAsync(
                    pendingJob.IssueIdentifier,
                    pendingJob.IssueProviderId,
                    pendingJob.RepoProviderId,
                    pendingJob.BrainProviderId,
                    pendingJob.PipelineProviderId,
                    pendingJob.InitiatedBy,
                    ct,
                    issueTitle: pendingJob.IssueTitle);
            }

            if (!dispatched)
            {
                _logger.Warning(
                    "Drain: failed to dispatch job for issue {IssueIdentifier}, re-enqueuing",
                    pendingJob.IssueIdentifier);
                _dispatcher.EnqueueJob(pendingJob);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex,
                "Drain: exception dispatching job for issue {IssueIdentifier} to agent {AgentId}, re-enqueuing",
                pendingJob.IssueIdentifier, agent.AgentId);
            _dispatcher.EnqueueJob(pendingJob);
        }
    }

    private async Task DispatchConsolidationJobAsync(PendingConsolidationJob job, AgentEntry agent, CancellationToken ct)
    {
        _logger.Information(
            "Drain: dequeued consolidation job {RunId} → agent {AgentId}",
            job.RunId, agent.AgentId);

        // Cancel-during-dispatch race guard
        if (_consolidationQueue!.IsCancelled(job.RunId))
        {
            _logger.Information("Consolidation job {RunId} was cancelled, skipping dispatch", job.RunId);
            return;
        }

        try
        {
            var dispatched = await _consolidationDispatcher!.TryDispatchToAgentAsync(job, agent.AgentId, ct);

            if (dispatched)
            {
                // Transition run from Queued to Running
                if (_consolidationService is not null)
                {
                    await _consolidationService.TransitionToRunningAsync(job.RunId, ct);
                }
            }
            else
            {
                // TODO: RetryCount is incremented for transient agent-unavailability (agent became busy between
                // selection and dispatch). Only actual dispatch failures (token vending, communication errors)
                // should count toward max retries. Check IsCancelled before incrementing, and distinguish
                // transient unavailability from permanent failures.
                job.RetryCount++;
                if (job.RetryCount >= PendingConsolidationJob.MaxRetries)
                {
                    _logger.Warning(
                        "Consolidation job {RunId} exceeded max retries ({MaxRetries}), marking as Failed",
                        job.RunId, PendingConsolidationJob.MaxRetries);

                    if (_consolidationService is not null)
                    {
                        await _consolidationService.UpdateRunAsync(
                            job.RunId,
                            ConsolidationRunStatus.Failed,
                            $"Dispatch failed after {PendingConsolidationJob.MaxRetries} attempts",
                            ct);
                    }
                }
                else
                {
                    // TODO: Check IsCancelled before re-enqueuing — a cancelled job can bounce through the queue
                    // unnecessarily if cancelled between DequeueForAgent and this point.
                    _logger.Warning(
                        "Drain: failed to dispatch consolidation job {RunId} (attempt {Attempt}/{Max}), re-enqueuing",
                        job.RunId, job.RetryCount, PendingConsolidationJob.MaxRetries);
                    _consolidationQueue.EnqueueJob(job);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex,
                "Drain: exception dispatching consolidation job {RunId} to agent {AgentId}",
                job.RunId, agent.AgentId);

            job.RetryCount++;
            if (job.RetryCount >= PendingConsolidationJob.MaxRetries)
            {
                if (_consolidationService is not null)
                {
                    // TODO: ex.Message may contain sensitive info (partial tokens, connection strings) from
                    // token vending or provider communication. Redact or truncate before persisting as user-visible summary.
                    await _consolidationService.UpdateRunAsync(
                        job.RunId,
                        ConsolidationRunStatus.Failed,
                        $"Dispatch failed after {PendingConsolidationJob.MaxRetries} attempts: {ex.Message}",
                        ct);
                }
            }
            else
            {
                _consolidationQueue.EnqueueJob(job);
            }
        }
    }
}
