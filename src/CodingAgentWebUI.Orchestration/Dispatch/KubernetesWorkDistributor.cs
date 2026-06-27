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
/// Kubernetes work distributor. Inserts a WorkItem row with Status=Pending into the database.
/// Pod spawning is handled separately by <see cref="DispatchService"/>, which polls for Pending items
/// and creates K8s Jobs.
/// </summary>
public sealed class KubernetesWorkDistributor : IWorkDistributor
{
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly WorkItemTransitionService _transitionService;
    private readonly ILogger<KubernetesWorkDistributor> _logger;

    /// <summary>Non-terminal statuses used for dedup queries.</summary>
    private static readonly WorkItemStatus[] ActiveStatuses =
    [
        WorkItemStatus.Pending,
        WorkItemStatus.Dispatched,
        WorkItemStatus.Running
    ];

    public KubernetesWorkDistributor(
        IDbContextFactory<PipelineDbContext> dbFactory,
        WorkItemTransitionService transitionService,
        ILogger<KubernetesWorkDistributor> logger)
    {
        _dbFactory = dbFactory;
        _transitionService = transitionService;
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
        var payload = JsonDocument.Parse(payloadJson);

        // Insert WorkItem row with Status=Pending — DispatchService handles pod spawning
        var entity = new WorkItemEntity
        {
            Id = workItemId,
            TaskType = request.TaskType,
            IssueIdentifier = request.IssueIdentifier,
            IssueProviderConfigId = request.IssueProviderConfigId,
            Status = WorkItemStatus.Pending,
            Payload = payload,
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
            _logger.LogError(ex, "Failed to insert WorkItem for issue {IssueIdentifier}", request.IssueIdentifier);
            payload.Dispose();
            return new DistributionResult(false, null, $"DB insert failed: {ex.Message}");
        }

        // Detach entity so the JsonDocument (payload) is no longer referenced by change tracker
        db.Entry(entity).State = EntityState.Detached;

        _logger.LogInformation(
            "WorkItem {WorkItemId} created (Pending) for issue {IssueIdentifier} — awaiting DispatchService",
            workItemId, request.IssueIdentifier);

        return new DistributionResult(true, workItemId.ToString(), null);
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
