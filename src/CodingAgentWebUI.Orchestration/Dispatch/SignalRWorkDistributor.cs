using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// DB + SignalR work distributor. Inserts a WorkItem row (Status=Dispatched) and pushes
/// <see cref="JobAssignmentMessage"/> to the connected agent via SignalR.
/// On SignalR delivery failure, updates the row to Failed.
/// Used in docker-compose+DB mode where agents are pre-connected via SignalR.
/// </summary>
/// <remarks>
/// Inherits shared DB operations (RunId resolution, cancel, status, dedup) from <see cref="DbWorkDistributorBase"/>.
/// Overrides <see cref="CancelJobAsync"/> for lifecycle-aware cancellation and
/// <see cref="ReconcileStuckItemsAsync"/> for stuck-item detection.
/// </remarks>
public sealed class SignalRWorkDistributor : DbWorkDistributorBase
{
    private readonly IAgentCommunication _agentComm;
    private readonly ISignalRWorkDistributorAgentResolver _agentResolver;
    private readonly IOrchestratorRunService _runService;
    private readonly IProjectStore _projectStore;
    private readonly ILabelService _labelService;
    private readonly IRunLifecycleManager? _lifecycleManager;
    private readonly IAgentCancellationSender? _cancellationSender;

    public SignalRWorkDistributor(
        IDbContextFactory<PipelineDbContext> dbFactory,
        IAgentCommunication agentComm,
        WorkItemTransitionService transitionService,
        ISignalRWorkDistributorAgentResolver agentResolver,
        IOrchestratorRunService runService,
        IProjectStore projectStore,
        ILabelService labelService,
        ILogger<SignalRWorkDistributor> logger,
        IRunLifecycleManager? lifecycleManager = null,
        IAgentCancellationSender? cancellationSender = null)
        : base(dbFactory, transitionService, logger)
    {
        _agentComm = agentComm;
        _agentResolver = agentResolver;
        _runService = runService;
        _projectStore = projectStore;
        _labelService = labelService;
        _lifecycleManager = lifecycleManager;
        _cancellationSender = cancellationSender;
    }

    /// <inheritdoc />
    public override async Task<DistributionResult> DistributeAsync(JobDistributionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Consolidation items always insert as Pending and let PendingWorkItemDrainService handle dispatch.
        // This avoids a circular DI dependency (SignalRWorkDistributor -> IConsolidationDispatcher -> IWorkDistributor).
        // The drain service picks it up within 5 seconds (acceptable for low-priority background work).
        if (request.TaskType == WorkItemTaskType.Consolidation)
        {
            return await InsertConsolidationAsPendingAsync(request, ct);
        }

        // Insert WorkItem row with Status=Dispatched
        var insertResult = await InsertWorkItemAsync(request, WorkItemStatus.Dispatched, ct);
        if (!insertResult.Success)
            return insertResult;

        var workItemId = Guid.Parse(insertResult.WorkItemId!);

        // Resolve agent connection and push via SignalR
        var resolveResult = _agentResolver.ResolveAgent(request.AgentSelector);
        if (resolveResult is null)
        {
            // No idle agent available — revert to Pending for drain service pickup.
            await using var pendingDb = await DbFactory.CreateDbContextAsync(ct);
            var pendingItem = await pendingDb.WorkItems.FindAsync([workItemId], ct);
            if (pendingItem is not null)
            {
                pendingItem.Status = WorkItemStatus.Pending;
                pendingItem.DispatchedAt = null;
                await pendingDb.SaveChangesAsync(ct);
            }

            Logger.LogInformation(
                "WorkItem {WorkItemId} for issue {IssueIdentifier} queued as Pending (no idle agent)",
                workItemId, request.IssueIdentifier);

            // Clear the in-memory PipelineRun's AgentId so HeartbeatMonitor Phase 3
            // doesn't orphan it (it checks GetByAgentId which returns null for "pending").
            if (!string.IsNullOrEmpty(request.RunId))
            {
                var run = _runService.GetRun(request.RunId);
                if (run is not null)
                    run.AgentId = null;
            }

            return new DistributionResult(true, workItemId.ToString(), "Queued — no idle agent available", Queued: true);
        }

        var connectionId = resolveResult.ConnectionId;
        var resolvedAgentId = resolveResult.AgentId;

        try
        {
            // Update the in-memory PipelineRun with the resolved agent ID
            if (!string.IsNullOrEmpty(request.RunId))
            {
                var run = _runService.GetRun(request.RunId);
                if (run is not null)
                    run.AgentId = resolvedAgentId;
            }

            var message = BuildJobAssignmentMessage(workItemId, request);

            // Inject project secrets at delivery time (not serialized in WorkItem payload for security)
            if (!string.IsNullOrEmpty(request.ProjectId))
            {
                var project = await _projectStore.GetProjectByIdAsync(request.ProjectId, ct);
                if (project?.Secrets is { Count: > 0 })
                    message = message with { ProjectSecrets = project.Secrets };
            }

            await _agentComm.AssignJobAsync(connectionId, message, ct);

            // Signal the lifecycle manager that an agent accepted this run.
            if (_lifecycleManager is not null)
            {
                await _lifecycleManager.AgentAcceptedRunAsync(
                    request.RunId ?? workItemId.ToString(), resolvedAgentId,
                    request.IssueIdentifier, request.IssueProviderConfigId,
                    request.RepoProviderConfigId, request.RunType, ct);
            }
            else
            {
                _agentResolver.AssignJob(resolvedAgentId, workItemId.ToString());
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "SignalR delivery failed for WorkItem {WorkItemId}, transitioning to Failed",
                workItemId);

            _agentResolver.ReleaseAgent(resolvedAgentId);
            await TransitionToFailedAsync(workItemId, $"SignalR delivery failure: {ex.Message}", ct);
            return new DistributionResult(false, workItemId.ToString(), $"SignalR delivery failed: {ex.Message}");
        }

        // Post-delivery: update WorkItem with resolved agent ID for UI display.
        try
        {
            await using var updateDb = await DbFactory.CreateDbContextAsync(ct);
            var workItem = await updateDb.WorkItems.FindAsync([workItemId], ct);
            if (workItem is not null)
            {
                workItem.AssignedAgentId = resolvedAgentId;
                await updateDb.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex,
                "Failed to update AssignedAgentId on WorkItem {WorkItemId} (cosmetic, agent already has the job)",
                workItemId);
        }

        Logger.LogInformation(
            "WorkItem {WorkItemId} pushed via SignalR to connection {ConnectionId}",
            workItemId, connectionId);

        return new DistributionResult(true, workItemId.ToString(), null);
    }

    // ── Consolidation insertion (always Pending, drained by PendingWorkItemDrainService) ──

    private async Task<DistributionResult> InsertConsolidationAsPendingAsync(JobDistributionRequest request, CancellationToken ct)
    {
        return await InsertWorkItemAsync(
            request, WorkItemStatus.Pending, ct,
            queued: true, successMessage: "Queued — consolidation item pending drain");
    }

    // ── Override: Lifecycle-aware cancellation ────────────────────────────

    /// <inheritdoc />
    public override async Task<bool> CancelJobAsync(string jobId, CancellationToken ct)
    {
        if (!Guid.TryParse(jobId, out _))
            return false;

        // Use lifecycle manager for full cleanup (in-memory + DB + label + agent state)
        if (_lifecycleManager is not null)
        {
            var cancelledRun = await _lifecycleManager.CancelRunAsync(jobId, ct);
            if (cancelledRun is not null)
            {
                // Send cancel signal to the agent (best-effort)
                // TODO: cancelledRun.AgentId is string? — implicit conversion to AgentId wraps null
                // without warning. The IsNullOrEmpty guard above makes this safe at runtime, but
                // consider adding nullable annotation to AgentId's implicit operator or explicit cast.
                if (!string.IsNullOrEmpty(cancelledRun.AgentId) && _cancellationSender is not null)
                    await _cancellationSender.SendCancelJobAsync(cancelledRun.AgentId, jobId, ct);
                return true;
            }

            // Run not found in memory — fall through to DB-only transition
        }

        // Fallback: DB-only transition (run not in memory, or lifecycle manager not available)
        return await base.CancelJobAsync(jobId, ct);
    }

    // ── Override: Stuck-item detection ────────────────────────────────────

    /// <inheritdoc />
    public override Task<int> ReconcileStuckItemsAsync(CancellationToken ct)
        => DetectStuckDispatchedItemsAsync(ct: ct);

    /// <summary>
    /// Detects work items stuck in Dispatched status beyond the threshold (default 5 minutes)
    /// and transitions them to Failed. This catches silent SignalR delivery failures.
    /// </summary>
    public async Task<int> DetectStuckDispatchedItemsAsync(TimeSpan? stuckThreshold = null, CancellationToken ct = default)
    {
        var threshold = stuckThreshold ?? TimeSpan.FromMinutes(5);
        var cutoff = DateTimeOffset.UtcNow - threshold;

        await using var db = await DbFactory.CreateDbContextAsync(ct);
        var stuckItems = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Status == WorkItemStatus.Dispatched && w.DispatchedAt < cutoff)
            .Select(w => new { w.Id, w.IssueIdentifier })
            .ToListAsync(ct);

        foreach (var item in stuckItems)
        {
            Logger.LogWarning(
                "WorkItem {WorkItemId} for issue {IssueIdentifier} stuck in Dispatched status since before {Cutoff}. " +
                "Transitioning to Failed (silent SignalR delivery failure).",
                item.Id, item.IssueIdentifier, cutoff);

            await TransitionService.TransitionAsync(
                item.Id, WorkItemStatus.Failed,
                entity =>
                {
                    entity.CompletedAt = DateTimeOffset.UtcNow;
                    entity.FailureReason = FailureReason.InfrastructureFailure;
                    entity.ErrorMessage = "Stuck in Dispatched status — likely silent SignalR delivery failure";
                }, ct);
        }

        return stuckItems.Count;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task TransitionToFailedAsync(Guid workItemId, string errorMessage, CancellationToken ct)
    {
        await TransitionService.TransitionAsync(
            workItemId,
            WorkItemStatus.Failed,
            item =>
            {
                item.ErrorMessage = errorMessage;
                item.FailureReason = FailureReason.InfrastructureFailure;
                item.CompletedAt = DateTimeOffset.UtcNow;
            },
            ct);
    }
}
