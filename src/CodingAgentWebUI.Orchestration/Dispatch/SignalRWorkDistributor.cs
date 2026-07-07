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
public sealed class SignalRWorkDistributor : IWorkDistributor
{
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly IAgentCommunication _agentComm;
    private readonly WorkItemTransitionService _transitionService;
    private readonly ISignalRWorkDistributorAgentResolver _agentResolver;
    private readonly IOrchestratorRunService _runService;
    private readonly IProjectStore _projectStore;
    private readonly ILabelSwapper _labelSwapper;
    private readonly IRunLifecycleManager? _lifecycleManager;
    private readonly IAgentCancellationSender? _cancellationSender;
    private readonly ILogger<SignalRWorkDistributor> _logger;

    /// <summary>Non-terminal statuses used for dedup queries.</summary>
    private static readonly WorkItemStatus[] ActiveStatuses =
    [
        WorkItemStatus.Pending,
        WorkItemStatus.Dispatched,
        WorkItemStatus.Running
    ];

    public SignalRWorkDistributor(
        IDbContextFactory<PipelineDbContext> dbFactory,
        IAgentCommunication agentComm,
        WorkItemTransitionService transitionService,
        ISignalRWorkDistributorAgentResolver agentResolver,
        IOrchestratorRunService runService,
        IProjectStore projectStore,
        ILabelSwapper labelSwapper,
        ILogger<SignalRWorkDistributor> logger,
        IRunLifecycleManager? lifecycleManager = null,
        IAgentCancellationSender? cancellationSender = null)
    {
        _dbFactory = dbFactory;
        _agentComm = agentComm;
        _transitionService = transitionService;
        _agentResolver = agentResolver;
        _runService = runService;
        _projectStore = projectStore;
        _labelSwapper = labelSwapper;
        _logger = logger;
        _lifecycleManager = lifecycleManager;
        _cancellationSender = cancellationSender;
    }

    /// <inheritdoc />
    public async Task<DistributionResult> DistributeAsync(JobDistributionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Consolidation items always insert as Pending and let PendingWorkItemDrainService handle dispatch.
        // This avoids a circular DI dependency (SignalRWorkDistributor → IConsolidationDispatcher → IWorkDistributor).
        // The drain service picks it up within 5 seconds (acceptable for low-priority background work).
        if (request.TaskType == WorkItemTaskType.Consolidation)
        {
            return await InsertConsolidationAsPendingAsync(request, ct);
        }

        // Use orchestration-assigned RunId if available; otherwise generate a new ID.
        // This ensures the agent's jobId matches the in-memory PipelineRun.RunId for hub routing.
        var workItemId = !string.IsNullOrEmpty(request.RunId) && Guid.TryParse(request.RunId, out var parsedId)
            ? parsedId
            : Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Serialize the request to JSONB payload
        var payloadJson = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        // Insert WorkItem row with Status=Dispatched
        var entity = new WorkItemEntity
        {
            Id = workItemId,
            TaskType = request.TaskType,
            IssueIdentifier = request.IssueIdentifier,
            IssueProviderConfigId = request.IssueProviderConfigId,
            Status = WorkItemStatus.Dispatched,
            Payload = payloadJson,
            AgentSelector = request.AgentSelector,
            CreatedAt = now,
            DispatchedAt = now,
            TimeoutSeconds = request.TimeoutSeconds,
            ProjectId = request.ProjectId
        };

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.WorkItems.Add(entity);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to insert WorkItem for issue {IssueIdentifier}", request.IssueIdentifier);
            return new DistributionResult(false, null, $"DB insert failed: {ex.Message}");
        }

        // Detach entity (no longer needed in change tracker after insert)
        db.Entry(entity).State = EntityState.Detached;

        _logger.LogInformation(
            "WorkItem {WorkItemId} created (Dispatched) for issue {IssueIdentifier}",
            workItemId, request.IssueIdentifier);

        // Resolve agent connection and push via SignalR
        var resolveResult = _agentResolver.ResolveAgent(request.AgentSelector);
        if (resolveResult is null)
        {
            // No idle agent available — keep WorkItem as Pending for drain service pickup.
            // The drain service will assign it when an agent becomes idle.
            await using var pendingDb = await _dbFactory.CreateDbContextAsync(ct);
            var pendingItem = await pendingDb.WorkItems.FindAsync([workItemId], ct);
            if (pendingItem is not null)
            {
                pendingItem.Status = WorkItemStatus.Pending;
                pendingItem.DispatchedAt = null;
                await pendingDb.SaveChangesAsync(ct);
            }

            _logger.LogInformation(
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
            // so HeartbeatMonitor doesn't orphan it (it was created with agentId="pending")
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
            // This atomically: sets AgentId on run, sets ActiveJobId on agent, swaps label to in-progress.
            if (_lifecycleManager is not null)
            {
                await _lifecycleManager.AgentAcceptedRunAsync(
                    request.RunId ?? workItemId.ToString(), resolvedAgentId,
                    request.IssueIdentifier, request.IssueProviderConfigId,
                    request.RepoProviderConfigId, request.RunType, ct);
            }
            else
            {
                // TODO: (#997 review) Legacy fallback only sets ActiveJobId via AssignJob but does NOT
                //       transition the agent to Busy status. If IRunLifecycleManager registration is ever
                //       missing, HeartbeatMonitor may treat the agent as Idle and assign a second job.
                //       Consider adding _agentResolver.TransitionStatus(agentId, Busy) here as safety net.
                // Legacy fallback (no lifecycle manager in tests without it)
                _agentResolver.AssignJob(resolvedAgentId, workItemId.ToString());
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "SignalR delivery failed for WorkItem {WorkItemId}, transitioning to Failed",
                workItemId);

            // Revert agent Busy status — agent never received the job.
            // Thread-safe: uses explicit agent ID from the local resolveResult, not shared state.
            _agentResolver.ReleaseAgent(resolvedAgentId);

            await TransitionToFailed(workItemId, $"SignalR delivery failure: {ex.Message}", ct);
            return new DistributionResult(false, workItemId.ToString(), $"SignalR delivery failed: {ex.Message}");
        }

        // Post-delivery: update WorkItem with resolved agent ID for UI display.
        // This is cosmetic — failure here must NOT release the agent or fail the WorkItem
        // since the agent already received and is working on the job.
        try
        {
            await using var updateDb = await _dbFactory.CreateDbContextAsync(ct);
            var workItem = await updateDb.WorkItems.FindAsync([workItemId], ct);
            if (workItem is not null)
            {
                workItem.AssignedAgentId = resolvedAgentId;
                await updateDb.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to update AssignedAgentId on WorkItem {WorkItemId} (cosmetic, agent already has the job)",
                workItemId);
        }

        _logger.LogInformation(
            "WorkItem {WorkItemId} pushed via SignalR to connection {ConnectionId}",
            workItemId, connectionId);

        return new DistributionResult(true, workItemId.ToString(), null);
    }

    /// <summary>
    /// Inserts a consolidation WorkItem directly as Pending (no agent resolution).
    /// PendingWorkItemDrainService handles actual dispatch with token vending at drain time.
    /// </summary>
    private async Task<DistributionResult> InsertConsolidationAsPendingAsync(JobDistributionRequest request, CancellationToken ct)
    {
        var workItemId = !string.IsNullOrEmpty(request.RunId) && Guid.TryParse(request.RunId, out var parsedId)
            ? parsedId
            : Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var payloadJson = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        var entity = new WorkItemEntity
        {
            Id = workItemId,
            TaskType = WorkItemTaskType.Consolidation,
            IssueIdentifier = request.IssueIdentifier,
            IssueProviderConfigId = request.IssueProviderConfigId,
            Status = WorkItemStatus.Pending,
            Payload = payloadJson,
            AgentSelector = request.AgentSelector,
            CreatedAt = now,
            TimeoutSeconds = request.TimeoutSeconds,
            ProjectId = request.ProjectId
        };

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.WorkItems.Add(entity);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to insert consolidation WorkItem for run {RunId}", request.IssueIdentifier);
            // TODO: Avoid leaking internal DB schema details in error message returned to callers.
            // Use a generic message here and keep details in the log only. (#1084 follow-up)
            return new DistributionResult(false, null, $"DB insert failed: {ex.Message}");
        }

        _logger.LogInformation(
            "Consolidation WorkItem {WorkItemId} created (Pending) for run {RunId}",
            workItemId, request.IssueIdentifier);

        return new DistributionResult(true, workItemId.ToString(), "Queued — consolidation item pending drain", Queued: true);
    }

    /// <inheritdoc />
    public async Task<bool> CancelJobAsync(string jobId, CancellationToken ct)
    {
        if (!Guid.TryParse(jobId, out var workItemId))
            return false;

        // Use lifecycle manager for full cleanup (in-memory + DB + label + agent state)
        if (_lifecycleManager is not null)
        {
            var cancelledRun = await _lifecycleManager.CancelRunAsync(jobId, ct);
            if (cancelledRun is not null)
            {
                // Send cancel signal to the agent (best-effort)
                if (!string.IsNullOrEmpty(cancelledRun.AgentId) && _cancellationSender is not null)
                    await _cancellationSender.SendCancelJobAsync(cancelledRun.AgentId, jobId, ct);
                return true;
            }

            // Run not found in memory — fall through to DB-only transition
        }

        // Fallback: DB-only transition (run not in memory, or lifecycle manager not available)
        return await _transitionService.TransitionAsync(
            workItemId,
            WorkItemStatus.Cancelled,
            item => item.CompletedAt = DateTimeOffset.UtcNow,
            ct);
    }

    /// <inheritdoc />
    public async Task<JobDistributionStatus> GetJobStatusAsync(string jobId, CancellationToken ct)
    {
        if (!Guid.TryParse(jobId, out var workItemId))
            return JobDistributionStatus.Unknown;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var status = await db.WorkItems
            .Where(w => w.Id == workItemId)
            .Select(w => (WorkItemStatus?)w.Status)
            .FirstOrDefaultAsync(ct);

        if (status is null)
            return JobDistributionStatus.Unknown;

        return MapStatus(status.Value);
    }

    /// <inheritdoc />
    public async Task<bool> IsIssueDistributedAsync(string issueIdentifier, string issueProviderConfigId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.WorkItems
            .AsNoTracking()
            .AnyAsync(w =>
                w.IssueIdentifier == issueIdentifier &&
                w.IssueProviderConfigId == issueProviderConfigId &&
                ActiveStatuses.Contains(w.Status),
                ct);
    }

    /// <inheritdoc />
    public async Task<HashSet<(string IssueIdentifier, string IssueProviderConfigId)>> GetActiveIssueIdentifiersAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var pairs = await db.WorkItems
            .AsNoTracking()
            .Where(w => ActiveStatuses.Contains(w.Status))
            .Select(w => new { w.IssueIdentifier, w.IssueProviderConfigId })
            .ToListAsync(ct);

        return pairs
            .Select(p => (p.IssueIdentifier, p.IssueProviderConfigId))
            .ToHashSet();
    }

    /// <summary>
    /// Detects work items stuck in Dispatched status for longer than the specified threshold.
    /// Called from PipelineLoopService at each cycle start in DB+SignalR mode.
    /// Logs a Warning per stuck item to surface silent SignalR delivery failures.
    /// </summary>
    /// <summary>
    /// Detects work items stuck in Dispatched status beyond the threshold (default 5 minutes)
    /// and transitions them to Failed. This catches silent SignalR delivery failures
    /// where the message was sent but the agent never processed it.
    /// </summary>
    /// <param name="stuckThreshold">Time after which a Dispatched item is considered stuck (default: 5 minutes).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of stuck items detected and transitioned to Failed.</returns>
    public async Task<int> DetectStuckDispatchedItemsAsync(TimeSpan? stuckThreshold = null, CancellationToken ct = default)
    {
        var threshold = stuckThreshold ?? TimeSpan.FromMinutes(5);
        var cutoff = DateTimeOffset.UtcNow - threshold;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var stuckItems = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Status == WorkItemStatus.Dispatched && w.DispatchedAt < cutoff)
            .Select(w => new { w.Id, w.IssueIdentifier })
            .ToListAsync(ct);

        foreach (var item in stuckItems)
        {
            _logger.LogWarning(
                "WorkItem {WorkItemId} for issue {IssueIdentifier} stuck in Dispatched status since before {Cutoff}. " +
                "Transitioning to Failed (silent SignalR delivery failure).",
                item.Id, item.IssueIdentifier, cutoff);

            await _transitionService.TransitionAsync(
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

    /// <inheritdoc />
    public Task<int> ReconcileStuckItemsAsync(CancellationToken ct)
        => DetectStuckDispatchedItemsAsync(ct: ct);

    private async Task TransitionToFailed(Guid workItemId, string errorMessage, CancellationToken ct)
    {
        await _transitionService.TransitionAsync(
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

    internal static JobAssignmentMessage BuildJobAssignmentMessage(Guid workItemId, JobDistributionRequest request)
    {
        return new JobAssignmentMessage
        {
            JobId = workItemId.ToString(),
            IssueIdentifier = request.IssueIdentifier,
            IssueDetail = request.IssueDetail ?? new IssueDetail
            {
                Identifier = request.IssueIdentifier,
                Title = string.Empty,
                Description = string.Empty,
                Labels = []
            },
            ParsedIssue = request.ParsedIssue ?? new ParsedIssue
            {
                AcceptanceCriteria = [],
                RequirementsSection = string.Empty
            },
            IssueComments = request.IssueComments ?? [],
            ExistingAnalysis = request.ExistingAnalysis,
            ForceRefreshAnalysis = request.ForceRefreshAnalysis,
            LinkedPullRequest = request.LinkedPullRequest,
            LinkedIssueContexts = request.LinkedIssueContexts,
            RepoProviderConfigId = request.RepoProviderConfigId,
            AgentProviderConfigId = request.AgentProviderConfigId ?? request.RepoProviderConfigId,
            BrainProviderConfigId = request.BrainProviderConfigId,
            PipelineProviderConfigId = request.PipelineProviderConfigId,
            ProviderConfigs = request.ProviderConfigs ?? [],
            PipelineConfiguration = request.PipelineConfiguration ?? new PipelineConfiguration(),
            InitiatedBy = request.InitiatedBy,
            ResolvedProfileId = request.ResolvedProfileId,
            QualityGateConfigs = request.QualityGateConfigs ?? [],
            McpServers = request.McpServers ?? [],
            ReviewerConfigs = request.ReviewerConfigs ?? [],
            RunType = request.RunType,
            ReviewPrTargetBranch = request.ReviewPrTargetBranch,
            ReviewPrDescription = request.ReviewPrDescription,
            ReviewPrAuthor = request.ReviewPrAuthor,
            ProjectContext = request.ProjectContext,
            ProjectId = request.ProjectId,
            ProjectName = request.ProjectName,
            ProjectSteeringContent = request.ProjectSteeringContent,
            RepoSteeringContent = request.RepoSteeringContent,
            TraceContext = request.TraceContext,
            IssueProviderConfigId = request.IssueProviderConfigId
            // NOTE: ProjectSecrets are NOT serialized to WorkItem payload (security).
            // In SignalR mode, secrets are injected at DistributeAsync/DrainPendingItemsAsync time
            // from IProjectStore after the message is built.
        };
    }

    private static JobDistributionStatus MapStatus(WorkItemStatus status) => status switch
    {
        WorkItemStatus.Pending => JobDistributionStatus.Pending,
        WorkItemStatus.Dispatched => JobDistributionStatus.Dispatched,
        WorkItemStatus.Running => JobDistributionStatus.Running,
        WorkItemStatus.Succeeded => JobDistributionStatus.Succeeded,
        WorkItemStatus.Failed => JobDistributionStatus.Failed,
        WorkItemStatus.Cancelled => JobDistributionStatus.Cancelled,
        _ => JobDistributionStatus.Unknown
    };
}
