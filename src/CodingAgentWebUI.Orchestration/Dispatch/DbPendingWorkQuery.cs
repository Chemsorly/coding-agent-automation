using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// DB-mode implementation of <see cref="IPendingWorkQuery"/>.
/// Queries the WorkItems table for items with Status=Pending.
/// Used in both SignalR+DB and Kubernetes modes.
/// </summary>
public sealed class DbPendingWorkQuery : IPendingWorkQuery
{
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;

    /// <summary>Cached count, updated on each <see cref="GetPendingJobsAsync"/> call and periodic refresh.</summary>
    private volatile int _cachedCount;

    public DbPendingWorkQuery(IDbContextFactory<PipelineDbContext> dbFactory)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        _dbFactory = dbFactory;
    }

    public int PendingCount => _cachedCount;

    public async Task<IReadOnlyList<PendingJob>> GetPendingJobsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var items = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Status == WorkItemStatus.Pending)
            .OrderBy(w => w.CreatedAt)
            .Select(w => new { w.Id, w.IssueIdentifier, w.IssueProviderConfigId, w.CreatedAt, w.OriginalEnqueuedAt, w.AgentSelector, w.TaskType, w.Payload })
            .ToListAsync(ct);

        var result = items.Select(w =>
        {
            var (issueTitle, repoProviderId, consolidationRunType) = ExtractFromPayload(w.Payload);
            var isConsolidation = w.TaskType == WorkItemTaskType.Consolidation;
            var pendingJob = new PendingJob
            {
                WorkItemId = w.Id.ToString(),
                IssueIdentifier = w.IssueIdentifier,
                IssueProviderId = w.IssueProviderConfigId,
                IssueTitle = issueTitle,
                RepoProviderId = repoProviderId,
                EnqueuedAt = w.OriginalEnqueuedAt ?? w.CreatedAt,
                InitiatedBy = "loop",
                RequiredLabels = string.IsNullOrEmpty(w.AgentSelector)
                    ? []
                    : w.AgentSelector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                TaskType = w.TaskType,
                RunType = w.TaskType == WorkItemTaskType.Review ? PipelineRunType.Review
                    : w.TaskType == WorkItemTaskType.Decomposition ? PipelineRunType.DecompositionAnalysis
                    : PipelineRunType.Implementation,
                ConsolidationRunType = isConsolidation ? consolidationRunType : null
            };

            if (isConsolidation && !pendingJob.IsConsolidation)
            {
                // TaskType says Consolidation but payload parsing didn't produce a ConsolidationRunType.
                // This would cause the UI filter (!IsConsolidation) to incorrectly include this item.
                Serilog.Log.Warning(
                    "DbPendingWorkQuery: WorkItem {WorkItemId} has TaskType=Consolidation but ConsolidationRunType is null " +
                    "(payload extraction failed). IssueIdentifier={IssueIdentifier}",
                    w.Id, w.IssueIdentifier);
            }

            return pendingJob;
        }).ToList();

        _cachedCount = result.Count;
        return result;
    }

    /// <summary>
    /// Extracts IssueTitle, RepoProviderConfigId, and ConsolidationRunType from the serialized payload JSONB.
    /// Falls back to empty strings/null if payload is null or deserialization fails.
    /// </summary>
    internal static (string IssueTitle, string RepoProviderId, ConsolidationRunType? ConsolidationRunType) ExtractFromPayload(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
            return ("", "", null);

        try
        {
            var request = JsonSerializer.Deserialize<JobDistributionRequest>(payload, PipelineJsonOptions.Default);
            if (request is null)
                return ("", "", null);

            var title = request.IssueDetail?.Title ?? "";
            var repoId = request.RepoProviderConfigId.Value ?? "";
            return (title, repoId, request.ConsolidationRunType);
        }
        catch (JsonException)
        {
            return ("", "", null);
        }
    }
}
