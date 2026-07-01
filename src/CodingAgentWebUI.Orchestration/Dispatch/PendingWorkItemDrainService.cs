using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Background service that drains Pending WorkItems from the DB by assigning them
/// to idle agents via SignalR. Wakes on signal (agent became idle) or periodic sweep.
/// Only active in DB+SignalR mode.
/// </summary>
public sealed class PendingWorkItemDrainService : BackgroundService
{
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly ISignalRWorkDistributorAgentResolver _agentResolver;
    private readonly IAgentCommunication _agentComm;
    private readonly IOrchestratorRunService _runService;
    private readonly WorkItemTransitionService _transitionService;
    private readonly IPendingWorkQuery _pendingWorkQuery;
    private readonly ILogger<PendingWorkItemDrainService> _logger;

    private readonly SemaphoreSlim _wakeSignal = new(0, int.MaxValue);

    internal static readonly TimeSpan DefaultDrainInterval = TimeSpan.FromSeconds(5);

    public PendingWorkItemDrainService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        ISignalRWorkDistributorAgentResolver agentResolver,
        IAgentCommunication agentComm,
        IOrchestratorRunService runService,
        WorkItemTransitionService transitionService,
        IPendingWorkQuery pendingWorkQuery,
        ILogger<PendingWorkItemDrainService> logger)
    {
        _dbFactory = dbFactory;
        _agentResolver = agentResolver;
        _agentComm = agentComm;
        _runService = runService;
        _transitionService = transitionService;
        _pendingWorkQuery = pendingWorkQuery;
        _logger = logger;
    }

    /// <summary>
    /// Wakes the drain loop immediately (e.g., when an agent becomes idle).
    /// </summary>
    public void Signal()
    {
        try { _wakeSignal.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PendingWorkItemDrainService started, sweep interval: {Interval}s",
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
                await DrainPendingItemsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PendingWorkItemDrainService: unexpected error during drain cycle");
            }
        }
    }

    private async Task DrainPendingItemsAsync(CancellationToken ct)
    {
        // Refresh the cached PendingCount for telemetry gauges (keeps metric fresh even without UI)
        await _pendingWorkQuery.GetPendingJobsAsync(ct);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var pendingItems = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Status == WorkItemStatus.Pending)
            .OrderBy(w => w.CreatedAt)
            .Take(20) // Batch limit per cycle
            .ToListAsync(ct);

        if (pendingItems.Count == 0)
            return;

        _logger.LogDebug("PendingWorkItemDrainService: {Count} pending item(s) to drain", pendingItems.Count);

        foreach (var item in pendingItems)
        {
            if (ct.IsCancellationRequested) break;

            var resolveResult = _agentResolver.ResolveAgent(item.AgentSelector ?? "");
            if (resolveResult is null)
            {
                // No idle agent for this selector. If the selector is empty (matches any agent),
                // then no agents are idle at all — stop draining entirely.
                if (string.IsNullOrWhiteSpace(item.AgentSelector))
                {
                    _logger.LogDebug("PendingWorkItemDrainService: no idle agents at all, stopping drain");
                    break;
                }
                // Label-constrained item with no matching agent — skip, try the next item
                _logger.LogDebug(
                    "PendingWorkItemDrainService: no agent for selector '{Selector}', skipping WorkItem {WorkItemId}",
                    item.AgentSelector, item.Id);
                continue;
            }

            // Deserialize the original request from payload
            JobDistributionRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<JobDistributionRequest>(item.Payload ?? "", PipelineJsonOptions.Default);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PendingWorkItemDrainService: failed to deserialize payload for WorkItem {WorkItemId}", item.Id);
                _agentResolver.ReleaseAgent(resolveResult.AgentId);
                continue;
            }

            if (request is null)
            {
                _logger.LogError("PendingWorkItemDrainService: null payload for WorkItem {WorkItemId}", item.Id);
                _agentResolver.ReleaseAgent(resolveResult.AgentId);
                continue;
            }

            var connectionId = resolveResult.ConnectionId;
            var agentId = resolveResult.AgentId;

            try
            {
                // Update in-memory PipelineRun with agent ID
                if (!string.IsNullOrEmpty(request.RunId))
                {
                    var run = _runService.GetRun(request.RunId);
                    if (run is not null)
                        run.AgentId = agentId;
                }

                var message = SignalRWorkDistributor.BuildJobAssignmentMessage(item.Id, request);
                await _agentComm.AssignJobAsync(connectionId, message, ct);

                _agentResolver.AssignJob(agentId, item.Id.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "PendingWorkItemDrainService: SignalR delivery failed for WorkItem {WorkItemId}",
                    item.Id);
                _agentResolver.ReleaseAgent(agentId);
                continue;
            }

            // Transition to Dispatched in DB
            await _transitionService.TransitionAsync(
                item.Id,
                WorkItemStatus.Dispatched,
                entity =>
                {
                    entity.DispatchedAt = DateTimeOffset.UtcNow;
                    entity.AssignedAgentId = agentId;
                },
                ct);

            _logger.LogInformation(
                "PendingWorkItemDrainService: assigned WorkItem {WorkItemId} (issue {IssueIdentifier}) to agent {AgentId}",
                item.Id, item.IssueIdentifier, agentId);
        }
    }
}
