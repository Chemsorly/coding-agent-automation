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
    private readonly JobDispatcherService _dispatcher;
    private readonly AgentRegistryService _registry;
    private readonly IJobDispatcher _jobDispatcher;
    private readonly ConsolidationQueueService _consolidationQueue;
    private readonly IConsolidationService _consolidationService;
    private readonly IConsolidationDispatcher _consolidationDispatcher;
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
        ConsolidationQueueService consolidationQueue,
        IConsolidationService consolidationService,
        IConsolidationDispatcher consolidationDispatcher,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(jobDispatcher);
        ArgumentNullException.ThrowIfNull(consolidationQueue);
        ArgumentNullException.ThrowIfNull(consolidationService);
        ArgumentNullException.ThrowIfNull(consolidationDispatcher);
        ArgumentNullException.ThrowIfNull(logger);

        _dispatcher = dispatcher;
        _registry = registry;
        _jobDispatcher = jobDispatcher;
        _consolidationQueue = consolidationQueue;
        _consolidationService = consolidationService;
        _consolidationDispatcher = consolidationDispatcher;
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
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("DrainCycle");

        try
        {
            // Phase 1: Drain pipeline jobs (higher priority)
            var pipelineDispatched = await DrainPipelineJobsAsync(ct);

            // Phase 2: Drain consolidation jobs (lower priority — only if idle agents remain)
            var consolidationDispatched = await DrainConsolidationJobsAsync(ct);

            activity?.SetTag("jobs_dispatched", pipelineDispatched + consolidationDispatched);
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

        var dispatched_count = 0;

        foreach (var agent in idleAgents)
        {
            if (ct.IsCancellationRequested)
                break;

            var pendingJob = _dispatcher.DequeueForAgent(agent);
            if (pendingJob is null)
                continue;

            PipelineTelemetry.QueueWaitTime.Record(
                (DateTimeOffset.UtcNow - pendingJob.EnqueuedAt).TotalSeconds);

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
                        new Pipeline.Models.ReviewDispatchRequest
                        {
                            PrIdentifier = pendingJob.IssueIdentifier,
                            PrBranchName = pendingJob.PrBranchName!,
                            PrTitle = pendingJob.IssueTitle ?? $"PR #{pendingJob.IssueIdentifier}",
                            PrDescription = pendingJob.PrDescription ?? string.Empty,
                            PrAuthor = pendingJob.PrAuthor,
                            PrUrl = pendingJob.PrUrl ?? string.Empty,
                            PrTargetBranch = pendingJob.PrTargetBranch ?? "main",
                            IssueProviderId = pendingJob.IssueProviderId,
                            RepoProviderId = pendingJob.RepoProviderId,
                            BrainProviderId = pendingJob.BrainProviderId,
                            InitiatedBy = pendingJob.InitiatedBy
                        },
                        ct,
                        project: pendingJob.Project);
                }
                else if (pendingJob.RunType is Pipeline.Models.PipelineRunType.DecompositionAnalysis
                         or Pipeline.Models.PipelineRunType.Decomposition)
                {
                    dispatched = await _jobDispatcher.TryDispatchDecompositionAsync(
                        pendingJob.IssueIdentifier,
                        pendingJob.IssueTitle ?? $"Epic #{pendingJob.IssueIdentifier}",
                        pendingJob.RunType,
                        pendingJob.IssueProviderId,
                        pendingJob.RepoProviderId,
                        pendingJob.BrainProviderId,
                        pendingJob.InitiatedBy,
                        ct,
                        decompositionSource: pendingJob.DecompositionSource,
                        project: pendingJob.Project);
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
                        issueTitle: pendingJob.IssueTitle,
                        project: pendingJob.Project);
                }

                if (dispatched)
                {
                    dispatched_count++;
                }
                else
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

        return dispatched_count;
    }

    private async Task<int> DrainConsolidationJobsAsync(CancellationToken ct)
    {
        var queueLength = _consolidationQueue.QueueLength;
        if (queueLength == 0)
            return 0;

        var idleAgents = _registry.GetIdleAgents();
        if (idleAgents.Count == 0)
            return 0;

        _logger.Debug(
            "Drain cycle: {QueueLength} queued consolidation job(s), {IdleAgents} idle agent(s)",
            queueLength, idleAgents.Count);

        var dispatched_count = 0;

        foreach (var agent in idleAgents)
        {
            if (ct.IsCancellationRequested)
                break;

            var job = _consolidationQueue.DequeueForAgent(agent);
            if (job is null)
                continue;

            // Cancel-during-dispatch race check
            if (_consolidationQueue.IsRunCancelled(job.RunId))
            {
                _logger.Information(
                    "Drain: consolidation job {RunId} was cancelled, skipping", job.RunId);
                continue;
            }

            _logger.Information(
                "Drain: dequeued consolidation job {RunId} → agent {AgentId}",
                job.RunId, agent.AgentId);

            try
            {
                var dispatched = await _consolidationDispatcher.TryDispatchToAgentAsync(
                    job.RunId, job.Type, job.TemplateId, job.WorkspacePath, agent.AgentId, ct);

                if (dispatched)
                {
                    dispatched_count++;
                    // Transition run from Queued to Running
                    await _consolidationService.TransitionToRunningAsync(job.RunId, ct);
                }
                else
                {
                    job.RetryCount++;
                    if (job.RetryCount >= ConsolidationQueueService.MaxRetryCount)
                    {
                        _logger.Warning(
                            "Drain: consolidation job {RunId} exceeded max retries ({MaxRetries}), marking as Failed",
                            job.RunId, ConsolidationQueueService.MaxRetryCount);

                        await _consolidationService.UpdateRunAsync(
                            job.RunId,
                            Pipeline.Models.ConsolidationRunStatus.Failed,
                            $"Dispatch failed after {ConsolidationQueueService.MaxRetryCount} attempts",
                            ct);
                    }
                    else
                    {
                        _logger.Warning(
                            "Drain: failed to dispatch consolidation job {RunId} (attempt {Attempt}/{Max}), re-enqueuing",
                            job.RunId, job.RetryCount, ConsolidationQueueService.MaxRetryCount);
                        _consolidationQueue.ReEnqueue(job);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex,
                    "Drain: exception dispatching consolidation job {RunId} to agent {AgentId}",
                    job.RunId, agent.AgentId);

                job.RetryCount++;
                if (job.RetryCount >= ConsolidationQueueService.MaxRetryCount)
                {
                    await _consolidationService.UpdateRunAsync(
                        job.RunId,
                        Pipeline.Models.ConsolidationRunStatus.Failed,
                        $"Dispatch failed after {ConsolidationQueueService.MaxRetryCount} attempts",
                        ct);
                }
                else
                {
                    _consolidationQueue.ReEnqueue(job);
                }
            }
        }

        return dispatched_count;
    }
}
