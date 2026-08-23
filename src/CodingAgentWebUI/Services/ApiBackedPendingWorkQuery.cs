using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Services;

/// <summary>
/// API-backed implementation of <see cref="IPendingWorkQuery"/>.
/// Calls GET /api/work-items/pending and maps the result to <see cref="PendingJob"/>
/// for display on the Agent Monitoring page's Job Queue widget.
/// Only exposes the subset of PendingJob fields available from PendingWorkItemDto —
/// enough for the UI display (type, labels, work item ID). Full pipeline execution
/// fields (provider IDs, repo details) are not populated since the UI doesn't need them.
/// </summary>
internal sealed class ApiBackedPendingWorkQuery : IPendingWorkQuery
{
    private readonly IPipelineApiWorkItemClient _client;
    private volatile int _pendingCount;

    public ApiBackedPendingWorkQuery(IPipelineApiWorkItemClient client)
    {
        _client = client;
    }

    public int PendingCount => _pendingCount;

    public async Task<IReadOnlyList<PendingJob>> GetPendingJobsAsync(CancellationToken ct = default)
    {
        var items = await _client.GetPendingAsync(maxResults: 200, ct);
        _pendingCount = items.Count;

        return items.Select(item => new PendingJob
        {
            WorkItemId = item.Id.ToString(),
            IssueIdentifier = new IssueIdentifier(item.IssueIdentifier),
            IssueProviderId = new ProviderConfigId(item.IssueProviderConfigId),
            RepoProviderId = new ProviderConfigId(""),  // not available from DTO; UI doesn't render it
            EnqueuedAt = item.CreatedAt,
            InitiatedBy = "",
            RequiredLabels = item.AgentSelector
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            TaskType = item.TaskType,
            RunType = item.TaskType == WorkItemTaskType.Consolidation
                ? PipelineRunType.Consolidation
                : PipelineRunType.Implementation
        }).ToList();
    }
}

/// <summary>
/// No-op fallback used when <see cref="IPipelineApiWorkItemClient"/> is not registered (e.g. test environments).
/// Returns an empty list so the Agent Monitoring page renders without crashing.
/// </summary>
internal sealed class EmptyPendingWorkQuery : IPendingWorkQuery
{
    public int PendingCount => 0;
    public Task<IReadOnlyList<PendingJob>> GetPendingJobsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PendingJob>>(Array.Empty<PendingJob>());
}
