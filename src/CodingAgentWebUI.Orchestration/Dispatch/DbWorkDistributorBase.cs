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
/// Base class for DB-backed work distributors (SignalR + K8s modes).
/// Consolidates shared logic: RunId resolution, WorkItem entity construction, DB persistence,
/// cancel/status/dedup queries, and status mapping.
/// </summary>
/// <remarks>
/// Subclasses override:
/// <list type="bullet">
///   <item><see cref="DistributeAsync"/> — mode-specific dispatch logic (SignalR push vs K8s enqueue)</item>
///   <item><see cref="CancelJobAsync"/> — optional override for lifecycle-aware cancellation</item>
///   <item><see cref="ReconcileStuckItemsAsync"/> — optional override for stuck-item detection</item>
/// </list>
/// </remarks>
public abstract class DbWorkDistributorBase : IWorkDistributor
{
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly WorkItemTransitionService _transitionService;
    private readonly ILogger _logger;

    /// <summary>Non-terminal statuses used for dedup queries.</summary>
    protected static readonly WorkItemStatus[] ActiveStatuses =
    [
        WorkItemStatus.Pending,
        WorkItemStatus.Dispatched,
        WorkItemStatus.Running
    ];

    protected DbWorkDistributorBase(
        IDbContextFactory<PipelineDbContext> dbFactory,
        WorkItemTransitionService transitionService,
        ILogger logger)
    {
        _dbFactory = dbFactory;
        _transitionService = transitionService;
        _logger = logger;
    }

    /// <inheritdoc />
    public virtual bool RequiresConnectedAgents => false;

    /// <summary>Exposed for subclass DB operations.</summary>
    protected IDbContextFactory<PipelineDbContext> DbFactory => _dbFactory;

    /// <summary>Exposed for subclass transition operations.</summary>
    protected WorkItemTransitionService TransitionService => _transitionService;

    /// <summary>Exposed for subclass logging.</summary>
    protected ILogger Logger => _logger;

    // ── Shared: RunId Resolution ──────────────────────────────────────────

    /// <summary>
    /// Resolves the WorkItem ID from <see cref="JobDistributionRequest.RunId"/>.
    /// Uses the pre-assigned RunId if valid; otherwise generates a new GUID.
    /// This ensures the agent's jobId matches the in-memory PipelineRun.RunId for hub routing.
    /// </summary>
    protected static Guid ResolveWorkItemId(JobDistributionRequest request)
    {
        return !string.IsNullOrEmpty(request.RunId) && Guid.TryParse(request.RunId, out var parsedId)
            ? parsedId
            : Guid.NewGuid();
    }

    // ── Shared: WorkItem Insertion ────────────────────────────────────────

    /// <summary>
    /// Inserts a WorkItem entity into the database with the specified initial status.
    /// Handles serialization, entity construction, and DB error handling.
    /// Carries forward OriginalEnqueuedAt from any prior WorkItem for the same issue
    /// to preserve the true "time in queue" across re-dispatches.
    /// </summary>
    /// <returns>A <see cref="DistributionResult"/> indicating success or failure.</returns>
    protected async Task<DistributionResult> InsertWorkItemAsync(
        JobDistributionRequest request,
        WorkItemStatus initialStatus,
        CancellationToken ct,
        bool queued = false,
        string? successMessage = null)
    {
        var workItemId = ResolveWorkItemId(request);
        var now = DateTimeOffset.UtcNow;

        var payloadJson = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        // Look up the original enqueue time from prior WorkItems for this issue.
        // This preserves the true "time in queue" across re-dispatches.
        DateTimeOffset? originalEnqueuedAt = null;
        if (!string.IsNullOrEmpty(request.IssueIdentifier) && !string.IsNullOrEmpty(request.IssueProviderConfigId))
        {
            await using var lookupDb = await _dbFactory.CreateDbContextAsync(ct);
            originalEnqueuedAt = await lookupDb.WorkItems
                .AsNoTracking()
                .Where(w => w.IssueIdentifier == request.IssueIdentifier
                         && w.IssueProviderConfigId == request.IssueProviderConfigId)
                .OrderBy(w => w.CreatedAt)
                .Select(w => w.OriginalEnqueuedAt ?? w.CreatedAt)
                .FirstOrDefaultAsync(ct);

            // FirstOrDefaultAsync returns default(DateTimeOffset) = DateTimeOffset.MinValue when no rows found
            if (originalEnqueuedAt == default(DateTimeOffset))
                originalEnqueuedAt = null;
        }

        var entity = new WorkItemEntity
        {
            Id = workItemId,
            TaskType = request.TaskType,
            IssueIdentifier = request.IssueIdentifier,
            IssueProviderConfigId = request.IssueProviderConfigId,
            Status = initialStatus,
            Payload = payloadJson,
            AgentSelector = request.AgentSelector,
            CreatedAt = now,
            OriginalEnqueuedAt = originalEnqueuedAt ?? now,
            DispatchedAt = initialStatus == WorkItemStatus.Dispatched ? now : null,
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

        db.Entry(entity).State = EntityState.Detached;

        _logger.LogInformation(
            "WorkItem {WorkItemId} created ({Status}) for issue {IssueIdentifier}",
            workItemId, initialStatus, request.IssueIdentifier);

        return new DistributionResult(true, workItemId.ToString(), successMessage, Queued: queued);
    }

    // ── Shared: Cancel ────────────────────────────────────────────────────

    /// <inheritdoc />
    public virtual async Task<bool> CancelJobAsync(string jobId, CancellationToken ct)
    {
        if (!Guid.TryParse(jobId, out var workItemId))
            return false;

        return await _transitionService.TransitionAsync(
            workItemId,
            WorkItemStatus.Cancelled,
            item => item.CompletedAt = DateTimeOffset.UtcNow,
            ct);
    }

    // ── Shared: Status Query ──────────────────────────────────────────────

    /// <inheritdoc />
    public virtual async Task<JobDistributionStatus> GetJobStatusAsync(string jobId, CancellationToken ct)
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

    // ── Shared: Dedup ─────────────────────────────────────────────────────

    /// <inheritdoc />
    public virtual async Task<bool> IsIssueDistributedAsync(string issueIdentifier, ProviderConfigId issueProviderConfigId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var issueProviderConfigIdValue = issueProviderConfigId.Value;

        // Check for active (non-terminal) WorkItems
        var hasActive = await db.WorkItems
            .AsNoTracking()
            .AnyAsync(w =>
                w.IssueIdentifier == issueIdentifier &&
                w.IssueProviderConfigId == issueProviderConfigIdValue &&
                ActiveStatuses.Contains(w.Status),
                ct);

        if (hasActive)
            return true;

        // Also check for recently-terminated WorkItems within the restart dedup cooldown.
        // This prevents re-dispatch of issues whose WorkItems were failed/cancelled during
        // pod restart (HeartbeatMonitor or ReconciliationService transitioning them to terminal).
        // TODO: This also blocks re-dispatch of Succeeded items within cooldown — consider excluding Succeeded status if intentional re-runs within 5min should be allowed (see BUG-10 review findings)
        var recentTerminalCutoff = DateTimeOffset.UtcNow - PipelineConstants.DefaultRestartDedupCooldown;
        return await db.WorkItems
            .AsNoTracking()
            .AnyAsync(w =>
                w.IssueIdentifier == issueIdentifier &&
                w.IssueProviderConfigId == issueProviderConfigIdValue &&
                !ActiveStatuses.Contains(w.Status) &&
                w.CompletedAt != null &&
                w.CompletedAt >= recentTerminalCutoff,
                ct);
    }

    /// <inheritdoc />
    public virtual async Task<HashSet<(string IssueIdentifier, ProviderConfigId IssueProviderConfigId)>> GetActiveIssueIdentifiersAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Load issues with active (non-terminal) WorkItems
        var activePairs = await db.WorkItems
            .AsNoTracking()
            .Where(w => ActiveStatuses.Contains(w.Status))
            .Select(w => new { w.IssueIdentifier, w.IssueProviderConfigId })
            .ToListAsync(ct);

        // Also load issues with recently-terminated WorkItems (within restart dedup cooldown).
        // This prevents re-dispatch of issues whose WorkItems were transitioned to terminal
        // states during a pod restart window.
        var recentTerminalCutoff = DateTimeOffset.UtcNow - PipelineConstants.DefaultRestartDedupCooldown;
        var recentTerminalPairs = await db.WorkItems
            .AsNoTracking()
            .Where(w => !ActiveStatuses.Contains(w.Status) &&
                        w.CompletedAt != null &&
                        w.CompletedAt >= recentTerminalCutoff)
            .Select(w => new { w.IssueIdentifier, w.IssueProviderConfigId })
            .ToListAsync(ct);

        var result = activePairs
            .Concat(recentTerminalPairs)
            .Select(p => (p.IssueIdentifier, (ProviderConfigId)p.IssueProviderConfigId))
            .ToHashSet();

        return result;
    }

    // ── Shared: Reconciliation (default no-op, overridden by SignalR) ─────

    /// <inheritdoc />
    public virtual Task<int> ReconcileStuckItemsAsync(CancellationToken ct) => Task.FromResult(0);

    // ── Shared: Status Mapping ────────────────────────────────────────────

    protected static JobDistributionStatus MapStatus(WorkItemStatus status) => status switch
    {
        WorkItemStatus.Pending => JobDistributionStatus.Pending,
        WorkItemStatus.Dispatched => JobDistributionStatus.Dispatched,
        WorkItemStatus.Running => JobDistributionStatus.Running,
        WorkItemStatus.Succeeded => JobDistributionStatus.Succeeded,
        WorkItemStatus.Failed => JobDistributionStatus.Failed,
        WorkItemStatus.Cancelled => JobDistributionStatus.Cancelled,
        _ => JobDistributionStatus.Unknown
    };

    // ── Shared: Job Assignment Mapping ────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="JobAssignmentMessage"/> from a <see cref="JobDistributionRequest"/>.
    /// Used by both SignalR distributor (direct push) and WorkItem HTTP endpoints (K8s assignment).
    /// </summary>
    public static JobAssignmentMessage BuildJobAssignmentMessage(Guid workItemId, JobDistributionRequest request)
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
            IssueProviderConfigId = request.IssueProviderConfigId,
            TaskType = request.TaskType,
            ConsolidationRunType = request.ConsolidationRunType,
            ConsolidationTemplateId = request.ConsolidationTemplateId,
            ConsolidationWorkspacePath = request.ConsolidationWorkspacePath,
            AutoDispatch = request.AutoDispatch,
            StalenessSignal = request.StalenessSignal,
            AnalysisRefreshCount = request.AnalysisRefreshCount
            // NOTE: ProjectSecrets are NOT serialized to WorkItem payload (security).
            // Injected at delivery time from IProjectStore.
        };
    }

    // ── Abstract: Distribution ────────────────────────────────────────────

    /// <inheritdoc />
    public abstract Task<DistributionResult> DistributeAsync(JobDistributionRequest request, CancellationToken ct);
}
