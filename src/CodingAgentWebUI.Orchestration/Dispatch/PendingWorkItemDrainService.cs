using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Telemetry;
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
    private readonly DispatchAttemptService _dispatchAttemptService;
    private readonly IPendingWorkQuery _pendingWorkQuery;
    private readonly ILabelSwapService _labelSwapper;
    private readonly DispatchRevertService _revertHandler;
    private readonly IProjectStore? _projectStore;
    private readonly IConsolidationDrainDispatcher? _consolidationDrainDispatcher;
    private readonly ILogger<PendingWorkItemDrainService> _logger;

    private readonly SemaphoreSlim _wakeSignal = new(0, int.MaxValue);

    internal static readonly TimeSpan DefaultDrainInterval = TimeSpan.FromSeconds(5);

    public PendingWorkItemDrainService(
        DrainServiceDependencies deps,
        IProjectStore? projectStore = null,
        IConsolidationDrainDispatcher? consolidationDrainDispatcher = null)
    {
        // TODO: Add ArgumentNullException.ThrowIfNull (or null-checks) for the mandatory
        // DrainServiceDependencies fields used unconditionally on the hot path:
        //   - deps.LabelSwapper → _labelSwapper (used in ProcessPendingItemAsync)
        //   - deps.RevertHandler → _revertHandler (used in DispatchPipelineItemAsync)
        // A null _labelSwapper (e.g. DrainServiceDependencies constructed with default null) produces
        // an NRE at ~line 170 on the first drain cycle rather than at construction time.
        _dbFactory = deps.DbFactory;
        _agentResolver = deps.AgentResolver;
        _agentComm = deps.AgentComm;
        // TODO: DispatchAttemptService is constructed inline here rather than injected, which creates a
        // second distinct instance alongside the one injected into ConsolidationDrainDispatcher via DI.
        // Both instances wrap the same singleton services (WorkItemTransitionService, DispatchRevertService),
        // so there is no correctness issue while the class is stateless. However, if DispatchAttemptService
        // ever acquires state (e.g., metrics counters), the two instances would diverge silently.
        // Consider registering DispatchAttemptService as a singleton in DI and accepting it as a constructor
        // parameter here for consistency with the consolidation path and to improve testability.
        _dispatchAttemptService = new DispatchAttemptService(deps.TransitionService, deps.RevertHandler);
        _pendingWorkQuery = deps.PendingWorkQuery;
        _labelSwapper = deps.LabelSwapper;
        _revertHandler = deps.RevertHandler;
        _logger = deps.Logger;
        _projectStore = projectStore;
        _consolidationDrainDispatcher = consolidationDrainDispatcher;
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
        await _pendingWorkQuery.GetPendingJobsAsync(ct); // Refresh cached PendingCount for telemetry gauges

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var pendingItems = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Status == WorkItemStatus.Pending)
            .OrderBy(w => w.TaskType == WorkItemTaskType.Consolidation ? 1 : 0)
            .ThenBy(w => w.CreatedAt)
            .Take(20) // Batch limit per cycle
            .ToListAsync(ct);
        WorkDistributionTelemetry.RecordLastPollEpoch();

        if (pendingItems.Count == 0) { WorkDistributionTelemetry.DispatcherPollCount.Add(1); return; }

        _logger.LogDebug("PendingWorkItemDrainService: {Count} pending item(s) to drain", pendingItems.Count);

        foreach (var item in pendingItems)
        {
            if (ct.IsCancellationRequested) break;
            if (!await ProcessPendingItemAsync(item, ct)) break;
        }

        WorkDistributionTelemetry.DispatcherPollCount.Add(1); // TODO: placed at end — see comment in original for metric inconsistency risk
    }

    /// <summary>
    /// Processes a single pending work item: resolves an agent, deserializes payload, and dispatches.
    /// Returns <c>false</c> when no idle agents are available at all and the drain loop should stop.
    /// Returns <c>true</c> to continue processing the next item (including the case where this item
    /// was skipped because no agent matched the selector).
    /// </summary>
    private async Task<bool> ProcessPendingItemAsync(WorkItemEntity item, CancellationToken ct)
    {
        if (!TryResolveAgentForItem(item, out var agentId, out var connectionId))
        {
            // Break on "no idle agents at all", continue on "no agent for selector"
            return !string.IsNullOrWhiteSpace(item.AgentSelector);
        }

        if (!TryDeserializePayload(item, agentId, out var request))
            return true;

        // --- Consolidation items: dispatch via IConsolidationDrainDispatcher (token vending at drain time) ---
        if (item.TaskType == WorkItemTaskType.Consolidation)
        {
            await DispatchConsolidationItemAsync(item, request!, agentId, ct);
            return true;
        }

        // --- Pipeline items ---
        // TODO: If the same agent is the only available agent and its ConnectionId is persistently null
        // (e.g., registered but SignalR connection never established), this guard will resolve/release
        // that agent O(n) times per drain cycle for n pending items, flooding logs and wasting CPU every
        // poll interval. Consider treating null ConnectionId as "no usable agent" (return false to stop
        // the drain), or tracking the broken agent ID to skip it for subsequent items in the same batch.
        // TODO: A persistently-null ConnectionId is indistinguishable from a transient one here — no
        // RetryCount increment or log-level escalation occurs. The work item loops forever at Pending
        // without any observable signal beyond a LogWarning per cycle. Consider incrementing RetryCount
        // or escalating to LogError after repeated null-ConnectionId encounters for the same work item.
        if (connectionId is null)
        {
            _logger.LogWarning(
                "PendingWorkItemDrainService: resolved agent {AgentId} has null ConnectionId for WorkItem {WorkItemId} — releasing agent and skipping",
                agentId, item.Id);
            _agentResolver.ReleaseAgent(agentId);
            return true; // continue to next item — this is a per-item transient condition, not "no agents"
        }

        if (!await DispatchPipelineItemAsync(item, request!, agentId, connectionId, ct)) return true;

        // Determine provider and target kind from run type, then delegate to shared label swap service.
        // The run-type selection logic (what to label) stays here; how to label (retry, reconciliation)
        // is encapsulated in ILabelSwapService. (#1868)
        var providerForLabel = request!.RunType == PipelineRunType.Review
            ? request.RepoProviderConfigId
            : request.IssueProviderConfigId;
        var targetKind = request.RunType == PipelineRunType.Review
            ? LabelTargetKind.PullRequest
            : LabelTargetKind.Issue;
        await _labelSwapper.SwapLabelWithRetryAsync(item.Id, providerForLabel, (IssueIdentifier)request.IssueIdentifier, targetKind, ct);
        _logger.LogInformation("PendingWorkItemDrainService: assigned WorkItem {WorkItemId} (issue {IssueIdentifier}) to agent {AgentId}",
            item.Id, item.IssueIdentifier, agentId);
        return true;
    }

    /// <summary>
    /// Attempts to resolve an idle agent for the given work item.
    /// Sets <paramref name="agentId"/> and <paramref name="connectionId"/> on success.
    /// Returns false when no agent is available; the caller is responsible for deciding
    /// whether to <c>break</c> (no idle agents at all) or <c>continue</c> (no agent for selector).
    /// </summary>
    private bool TryResolveAgentForItem(
        WorkItemEntity item,
        out AgentId agentId,
        out string? connectionId)
    {
        agentId = default;
        connectionId = null;

        var resolveResult = _agentResolver.ResolveAgent(item.AgentSelector ?? "");
        if (resolveResult is null)
        {
            if (string.IsNullOrWhiteSpace(item.AgentSelector))
                _logger.LogDebug("PendingWorkItemDrainService: no idle agents at all, stopping drain");
            else
                _logger.LogDebug(
                    "PendingWorkItemDrainService: no agent for selector '{Selector}', skipping WorkItem {WorkItemId}",
                    item.AgentSelector, item.Id);
            return false;
        }

        agentId = resolveResult.AgentId;
        connectionId = resolveResult.ConnectionId;
        return true;
    }

    /// <summary>
    /// Attempts to deserialize the work item's JSON payload into a <see cref="JobDistributionRequest"/>.
    /// Releases the reserved agent and logs an error on failure. Returns false when deserialization fails
    /// or the payload is null (caller should <c>continue</c> to the next item).
    /// </summary>
    private bool TryDeserializePayload(
        WorkItemEntity item,
        AgentId agentId,
        out JobDistributionRequest? request)
    {
        request = null;
        try
        {
            request = JsonSerializer.Deserialize<JobDistributionRequest>(item.Payload ?? "", PipelineJsonOptions.Default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PendingWorkItemDrainService: failed to deserialize payload for WorkItem {WorkItemId}", item.Id);
            _agentResolver.ReleaseAgent(agentId);
            return false;
        }

        if (request is null)
        {
            _logger.LogError("PendingWorkItemDrainService: null payload for WorkItem {WorkItemId}", item.Id);
            _agentResolver.ReleaseAgent(agentId);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Delegates consolidation dispatch to <see cref="IConsolidationDrainDispatcher"/>.
    /// Returns <c>true</c> if the item was successfully dispatched to an agent, <c>false</c> in all other cases
    /// (null dispatcher, cancelled run, dispatch failure, or exception). The caller should always <c>continue</c>
    /// to the next item after this call regardless of the return value.
    /// </summary>
    private async Task<bool> DispatchConsolidationItemAsync(
        WorkItemEntity item, JobDistributionRequest request,
        AgentId agentId,
        CancellationToken ct)
    {
        if (_consolidationDrainDispatcher is null)
        {
            _logger.LogError("PendingWorkItemDrainService: consolidation dispatcher not available for WorkItem {WorkItemId}", item.Id);
            _agentResolver.ReleaseAgent(agentId);
            return false;
        }
        return await _consolidationDrainDispatcher.TryDispatchAsync(item, request, agentId, ct);
    }

    private async Task<bool> DispatchPipelineItemAsync(
        WorkItemEntity item, JobDistributionRequest request,
        AgentId agentId, string connectionId,
        CancellationToken ct)
    {
        var dispatchedSuccessfully = false;
        try
        {
            // DB transition first: in-memory state only reflects confirmed DB state.
            // If TransitionToDispatchedAsync fails, no in-memory cleanup is needed.
            // This also ensures the agent's JobAccepted → Running transition is valid
            // (Dispatched → Running, not Pending → Running which is rejected).
            var dispatchTime = DateTimeOffset.UtcNow;
            await _dispatchAttemptService.TransitionToDispatchedAsync(item.Id, agentId, ct);

            dispatchedSuccessfully = true;

            _revertHandler.EnsureInMemoryRunRegistered(request, agentId.Value, dispatchTime, item);

            var message = DbWorkDistributorBase.BuildJobAssignmentMessage(item.Id, request);

            // Inject project secrets at delivery time (not serialized in WorkItem payload for security)
            if (_projectStore is not null && !string.IsNullOrEmpty(request.ProjectId))
            {
                var project = await _projectStore.GetProjectByIdAsync(request.ProjectId, ct);
                if (project?.Secrets is { Count: > 0 })
                    message = message with { ProjectSecrets = project.Secrets };
            }

            await _agentComm.AssignJobAsync(connectionId, message, ct);

            _agentResolver.AssignJob(agentId, item.Id.ToString());

            WorkDistributionTelemetry.RecordDispatchLatency(DateTimeOffset.UtcNow, item.OriginalEnqueuedAt, item.CreatedAt, item.AgentSelector);

            return true;
        }
        catch (Exception ex)
        {
            await _revertHandler.HandlePipelineDispatchFailureAsync(item, request, agentId, dispatchedSuccessfully, ex);
            return false;
        }
    }

}
