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
            var (issueTitle, repoProviderIdStr, consolidationRunType, projectId, projectName) = ExtractFromPayload(w.Payload);
            var isConsolidation = w.TaskType == WorkItemTaskType.Consolidation;
            // RepoProviderId from payload may be empty when payload is null or deserialization fails.
            // Fall back to the IssueProviderConfigId as a best-effort value to satisfy the required
            // ProviderConfigId constraint while still allowing the job to be dequeued.
            // A job with a missing RepoProviderId will fail at dispatch time anyway.
            // TODO: [WARNING] If w.IssueProviderConfigId is itself null or empty (e.g., a corrupt/legacy
            // DB row with an empty IssueProviderConfigId column), the implicit string → ProviderConfigId
            // conversion below will throw ArgumentException("The value cannot be an empty string"),
            // crashing GetPendingJobsAsync for the entire page of results and causing ALL pending jobs
            // in that batch to be lost — not just the malformed one. This is a DoS risk on the job
            // polling loop. Consider skipping/logging rows with empty IssueProviderConfigId rather than
            // crashing the entire Select projection. Same applies to IssueProviderId = w.IssueProviderConfigId.
            var effectiveRepoProviderId = string.IsNullOrEmpty(repoProviderIdStr)
                ? w.IssueProviderConfigId
                : repoProviderIdStr;
            var pendingJob = new PendingJob
            {
                WorkItemId = w.Id.ToString(),
                IssueIdentifier = w.IssueIdentifier,
                IssueProviderId = w.IssueProviderConfigId,
                IssueTitle = issueTitle,
                RepoProviderId = effectiveRepoProviderId,
                EnqueuedAt = w.OriginalEnqueuedAt ?? w.CreatedAt,
                InitiatedBy = "loop",
                RequiredLabels = string.IsNullOrEmpty(w.AgentSelector)
                    ? []
                    : w.AgentSelector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                TaskType = w.TaskType,
                RunType = ResolveRunType(w.TaskType),
                ConsolidationRunType = isConsolidation ? consolidationRunType : null,
                Project = !string.IsNullOrEmpty(projectId) && !string.IsNullOrEmpty(projectName)
                    ? new PipelineProject { Id = projectId, Name = projectName }
                    : null
            };

            return pendingJob;
        }).ToList();

        _cachedCount = result.Count;
        return result;
    }

    private static PipelineRunType ResolveRunType(WorkItemTaskType taskType)
    {
        if (taskType == WorkItemTaskType.Review) return PipelineRunType.Review;
        if (taskType == WorkItemTaskType.Decomposition) return PipelineRunType.DecompositionAnalysis;
        if (taskType == WorkItemTaskType.Consolidation) return PipelineRunType.Consolidation;
        return PipelineRunType.Implementation;
    }

    /// <summary>
    /// Extracts IssueTitle, RepoProviderConfigId, ConsolidationRunType, ProjectId, and ProjectName
    /// from the serialized payload JSONB.
    /// Falls back to empty strings/null if payload is null or deserialization fails.
    /// </summary>
    internal static (string IssueTitle, string RepoProviderId, ConsolidationRunType? ConsolidationRunType, string? ProjectId, string? ProjectName) ExtractFromPayload(string? payload)
    {
        if (string.IsNullOrEmpty(payload))
            return ("", "", null, null, null);

        try
        {
            var request = JsonSerializer.Deserialize<JobDistributionRequest>(payload, PipelineJsonOptions.Default);
            if (request is null)
                return ("", "", null, null, null);

            var title = request.IssueDetail?.Title ?? "";
            var repoId = request.RepoProviderConfigId ?? "";
            return (title, repoId, request.ConsolidationRunType, request.ProjectId, request.ProjectName);
        }
        catch (JsonException)
        {
            return ("", "", null, null, null);
        }
    }
}
