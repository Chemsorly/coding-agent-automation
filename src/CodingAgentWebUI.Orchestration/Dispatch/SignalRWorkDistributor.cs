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
        ILogger<SignalRWorkDistributor> logger)
    {
        _dbFactory = dbFactory;
        _agentComm = agentComm;
        _transitionService = transitionService;
        _agentResolver = agentResolver;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DistributionResult> DistributeAsync(JobDistributionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var workItemId = Guid.NewGuid();
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
        try
        {
            var connectionId = _agentResolver.ResolveConnectionId(request.AgentSelector);
            if (connectionId is null)
            {
                // No connected agent — mark as Failed
                await TransitionToFailed(workItemId, "No connected agent found for selector: " + request.AgentSelector, ct);
                return new DistributionResult(false, workItemId.ToString(), "No connected agent available");
            }

            var message = BuildJobAssignmentMessage(workItemId, request);
            await _agentComm.AssignJobAsync(connectionId, message, ct);

            _logger.LogInformation(
                "WorkItem {WorkItemId} pushed via SignalR to connection {ConnectionId}",
                workItemId, connectionId);

            return new DistributionResult(true, workItemId.ToString(), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "SignalR delivery failed for WorkItem {WorkItemId}, transitioning to Failed",
                workItemId);

            // Revert agent Busy status — agent never received the job
            _agentResolver.ReleaseLastResolvedAgent();

            await TransitionToFailed(workItemId, $"SignalR delivery failure: {ex.Message}", ct);
            return new DistributionResult(false, workItemId.ToString(), $"SignalR delivery failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<bool> CancelJobAsync(string jobId, CancellationToken ct)
    {
        if (!Guid.TryParse(jobId, out var workItemId))
            return false;

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

    private static JobAssignmentMessage BuildJobAssignmentMessage(Guid workItemId, JobDistributionRequest request)
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
            // In SignalR mode, secrets are delivered at DistributeAsync time from Project.Secrets.
            // TODO: Load project secrets from DB and inject here.
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
