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
    private readonly IConsolidationDispatcher _consolidationDispatcher;
    private readonly IConsolidationRunStore? _consolidationRunStore;
    private readonly IShutdownSignal _shutdownSignal;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _wakeSignal = new(0, int.MaxValue);

    /// <summary>
    /// Default interval between periodic sweeps when no explicit signal arrives.
    /// </summary>
    internal static readonly TimeSpan DefaultDrainInterval = TimeSpan.FromSeconds(10);

    internal JobQueueDrainService(
        JobDeduplicationGuardService dispatcher,
        IAgentRegistryService registry,
        IJobDispatcher jobDispatcher,
        IConfigurationStore configStore,
        IConsolidationDispatcher consolidationDispatcher,
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
                // Consolidation jobs: dispatch via IConsolidationDispatcher
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
                        // TODO: Add retry limit for consolidation jobs (old DrainConsolidationJobsAsync had MaxRetryCount=5).
                        // Without a limit, a persistently-failing job will be re-enqueued indefinitely. (#1084 follow-up)
                        _logger.Warning(
                            "Drain: failed to dispatch consolidation job {RunId}, re-enqueuing",
                            pendingJob.IssueIdentifier);
                        _dispatcher.ReEnqueue(pendingJob);
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
                _logger.Error(ex,
                    "Drain: exception dispatching job for issue {IssueIdentifier} to agent {AgentId}, re-enqueuing",
                    pendingJob.IssueIdentifier, agent.AgentId);
                _dispatcher.ReEnqueue(pendingJob);
            }
        }

        return dispatchedCount;
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
