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
/// </summary>
public sealed class JobQueueDrainService : BackgroundService
{
    private readonly JobDispatcherService _dispatcher;
    private readonly AgentRegistryService _registry;
    private readonly IJobDispatcher _jobDispatcher;
    private readonly ConsolidationQueueService _consolidationQueue;
    private readonly IConsolidationDispatcher _consolidationDispatcher;
    private readonly IConsolidationService _consolidationService;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _wakeSignal = new(0, int.MaxValue);

    /// <summary>
    /// Maximum dispatch retry attempts before marking a consolidation run as Failed.
    /// </summary>
    internal const int MaxConsolidationRetries = 5;

    /// <summary>
    /// Default interval between periodic sweeps when no explicit signal arrives.
    /// </summary>
    internal static readonly TimeSpan DefaultDrainInterval = TimeSpan.FromSeconds(10);

    public JobQueueDrainService(
        JobDispatcherService dispatcher,
        AgentRegistryService registry,
        IJobDispatcher jobDispatcher,
        ConsolidationQueueService consolidationQueue,
        IConsolidationDispatcher consolidationDispatcher,
        IConsolidationService consolidationService,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(jobDispatcher);
        ArgumentNullException.ThrowIfNull(consolidationQueue);
        ArgumentNullException.ThrowIfNull(consolidationDispatcher);
        ArgumentNullException.ThrowIfNull(consolidationService);
        ArgumentNullException.ThrowIfNull(logger);

        _dispatcher = dispatcher;
        _registry = registry;
        _jobDispatcher = jobDispatcher;
        _consolidationQueue = consolidationQueue;
        _consolidationDispatcher = consolidationDispatcher;
        _consolidationService = consolidationService;
        _logger = logger;
    }

    /// <summary>
    /// Wakes the drain loop immediately so it can attempt dispatch without
    /// waiting for the next periodic tick. Safe to call from any thread.
    /// </summary>
    public void Signal()
    {
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
    /// a compatible job and dispatch it. Pipeline jobs have priority over consolidation jobs.
    /// Exposed as internal for testing.
    /// </summary>
    internal async Task DrainAsync(CancellationToken ct)
    {
        // --- Pipeline jobs (priority) ---
        await DrainPipelineJobsAsync(ct);

        // --- Consolidation jobs (after pipeline) ---
        await DrainConsolidationJobsAsync(ct);
    }

    private async Task DrainPipelineJobsAsync(CancellationToken ct)
    {
        var queueLength = _dispatcher.QueueLength;
        if (queueLength == 0)
            return;

        var idleAgents = _registry.GetIdleAgents();
        if (idleAgents.Count == 0)
            return;

        _logger.Debug(
            "Drain cycle: {QueueLength} queued pipeline job(s), {IdleAgents} idle agent(s)",
            queueLength, idleAgents.Count);

        foreach (var agent in idleAgents)
        {
            if (ct.IsCancellationRequested)
                break;

            var pendingJob = _dispatcher.DequeueForAgent(agent);
            if (pendingJob is null)
                continue;

            _logger.Information(
                "Drain: dequeued job for issue {IssueIdentifier} → agent {AgentId}",
                pendingJob.IssueIdentifier, agent.AgentId);

            try
            {
                _dispatcher.MarkIssueComplete(pendingJob.IssueIdentifier);

                bool dispatched;
                if (pendingJob.RunType == PipelineRunType.Review)
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
    }

    private async Task DrainConsolidationJobsAsync(CancellationToken ct)
    {
        if (_consolidationQueue.QueueLength == 0)
            return;

        var idleAgents = _registry.GetIdleAgents();
        if (idleAgents.Count == 0)
            return;

        _logger.Debug(
            "Drain cycle: {QueueLength} queued consolidation job(s), {IdleAgents} idle agent(s)",
            _consolidationQueue.QueueLength, idleAgents.Count);

        foreach (var agent in idleAgents)
        {
            if (ct.IsCancellationRequested)
                break;

            var job = _consolidationQueue.DequeueForAgent(agent);
            if (job is null)
                continue;

            // Check cancel-during-dispatch race
            if (_consolidationQueue.IsCancelled(job.RunId))
            {
                // TODO: If CancelQueuedRunAsync hasn't run yet (TOCTOU between DequeueForAgent and MarkCancelled),
                // the run could remain Queued on disk with no queue entry. Consider calling UpdateRunAsync here.
                _logger.Information("Consolidation job {RunId} cancelled before dispatch, skipping", job.RunId);
                continue;
            }

            _logger.Information(
                "Drain: dequeued consolidation job {RunId} → agent {AgentId}",
                job.RunId, agent.AgentId);

            try
            {
                var dispatched = await _consolidationDispatcher.TryDispatchToAgentAsync(job, agent.AgentId, ct);

                if (dispatched)
                {
                    await _consolidationService.TransitionToRunningAsync(job.RunId, ct);
                }
                else
                {
                    // TODO: Distinguish cancelled-dispatch (IsCancelled returned false due to TOCTOU) from genuine failure
                    // to avoid incrementing retry count or overwriting Cancelled status on disk.
                    job.RetryCount++;
                    if (job.RetryCount >= MaxConsolidationRetries)
                    {
                        _logger.Warning(
                            "Consolidation job {RunId} exceeded max retries ({Max}), marking as Failed",
                            job.RunId, MaxConsolidationRetries);
                        await _consolidationService.UpdateRunAsync(
                            job.RunId,
                            ConsolidationRunStatus.Failed,
                            $"Dispatch failed after {MaxConsolidationRetries} attempts",
                            ct);
                    }
                    else
                    {
                        _logger.Warning(
                            "Drain: failed to dispatch consolidation job {RunId} (attempt {Attempt}/{Max}), re-enqueuing",
                            job.RunId, job.RetryCount, MaxConsolidationRetries);
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
                if (job.RetryCount >= MaxConsolidationRetries)
                {
                    await _consolidationService.UpdateRunAsync(
                        job.RunId,
                        ConsolidationRunStatus.Failed,
                        $"Dispatch failed after {MaxConsolidationRetries} attempts: {ex.Message}",
                        ct);
                }
                else
                {
                    _consolidationQueue.EnqueueJob(job);
                }
            }
        }
    }
}
