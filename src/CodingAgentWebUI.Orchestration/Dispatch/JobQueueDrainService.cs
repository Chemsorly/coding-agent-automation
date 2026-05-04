using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
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
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(jobDispatcher);
        ArgumentNullException.ThrowIfNull(logger);

        _dispatcher = dispatcher;
        _registry = registry;
        _jobDispatcher = jobDispatcher;
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
    /// a compatible job and dispatch it. Exposed as internal for testing.
    /// </summary>
    internal async Task DrainAsync(CancellationToken ct)
    {
        var queueLength = _dispatcher.QueueLength;
        if (queueLength == 0)
            return;

        var idleAgents = _registry.GetIdleAgents();
        if (idleAgents.Count == 0)
            return;

        _logger.Debug(
            "Drain cycle: {QueueLength} queued job(s), {IdleAgents} idle agent(s)",
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
                // Clear the dedup entry so TryDispatchAsync doesn't reject it
                _dispatcher.MarkIssueComplete(pendingJob.IssueIdentifier);

                var dispatched = await _jobDispatcher.TryDispatchAsync(
                    pendingJob.IssueIdentifier,
                    pendingJob.IssueProviderId,
                    pendingJob.RepoProviderId,
                    pendingJob.BrainProviderId,
                    pendingJob.PipelineProviderId,
                    pendingJob.InitiatedBy,
                    ct);

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
}
