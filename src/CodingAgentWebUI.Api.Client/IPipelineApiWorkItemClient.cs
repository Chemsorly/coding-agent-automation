using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// Typed HTTP client for the /api/work-items endpoint group.
/// </summary>
public interface IPipelineApiWorkItemClient
{
    Task<IReadOnlyList<PendingWorkItemDto>> GetPendingAsync(int maxResults = 50, CancellationToken ct = default);
    Task<WorkItemClaimResponse?> ClaimAsync(Guid workItemId, ClaimWorkItemRequest request, CancellationToken ct = default);
    Task<JobAssignmentMessage?> GetAssignmentAsync(Guid workItemId, CancellationToken ct = default);
    Task PostStatusAsync(Guid workItemId, WorkItemStatusUpdate request, CancellationToken ct = default);
    Task RequeueAsync(Guid workItemId, CancellationToken ct = default);
    Task<int> GetRetryCountAsync(Guid workItemId, CancellationToken ct = default);
    Task<WorkItemStalenessResult?> GetStalenessAsync(string issueIdentifier, string issueProviderConfigId, DateTimeOffset since, CancellationToken ct = default);
    Task<Guid> CreateAsync(JobDistributionRequest request, CancellationToken ct = default);
}
