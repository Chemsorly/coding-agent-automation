using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Logging;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Kubernetes work distributor. All operations are backed by the Pipeline API
/// (<see cref="IPipelineApiWorkItemClient"/>). No direct database access.
/// </summary>
/// <remarks>
/// <see cref="DistributeAsync"/> creates a WorkItem row via <c>POST /api/work-items</c>.
/// The Job Controller's dispatch loop picks up the Pending item and creates the K8s Job.
/// <para>
/// Cancel, status-query, and dedup operations route through the same API client.
/// This class no longer inherits <c>DbWorkDistributorBase</c> — all DB coupling is removed.
/// </para>
/// </remarks>
public sealed class KubernetesWorkDistributor : IWorkDistributor
{
    private readonly IPipelineApiWorkItemClient _apiClient;
    private readonly ILogger<KubernetesWorkDistributor> _logger;

    public KubernetesWorkDistributor(
        IPipelineApiWorkItemClient apiClient,
        ILogger<KubernetesWorkDistributor> logger)
    {
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(logger);
        _apiClient = apiClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<DistributionResult> DistributeAsync(JobDistributionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var workItemId = await _apiClient.CreateAsync(request, ct);
            _logger.LogInformation(
                "WorkItem {WorkItemId} created via Pipeline API for issue {IssueIdentifier}",
                workItemId, request.IssueIdentifier);
            return new DistributionResult(true, workItemId.ToString(), null, Queued: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create WorkItem via Pipeline API for issue {IssueIdentifier}",
                request.IssueIdentifier);
            return new DistributionResult(false, null, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<bool> CancelJobAsync(JobId jobId, CancellationToken ct)
    {
        if (!Guid.TryParse(jobId.Value, out var workItemId))
            return false;

        try
        {
            await _apiClient.PostStatusAsync(workItemId, new WorkItemStatusUpdate
            {
                Status = "Cancelled",
                ErrorMessage = "Cancelled by orchestrator"
            }, ct);
            return true;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            // Invalid transition (e.g. already terminal) — treat as not-found/no-op
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to cancel WorkItem {WorkItemId} via Pipeline API", workItemId);
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<JobDistributionStatus> GetJobStatusAsync(JobId jobId, CancellationToken ct)
    {
        if (!Guid.TryParse(jobId.Value, out var workItemId))
            return JobDistributionStatus.Unknown;

        var status = await _apiClient.GetStatusAsync(workItemId, ct);
        return status is null ? JobDistributionStatus.Unknown : MapStatus(status.Value);
    }

    /// <inheritdoc />
    public async Task<bool> IsIssueDistributedAsync(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId, CancellationToken ct)
    {
        return await _apiClient.IsIssueDistributedAsync(
            issueIdentifier.Value,
            issueProviderConfigId.Value,
            ct);
    }

    /// <inheritdoc />
    public async Task<HashSet<(IssueIdentifier IssueIdentifier, ProviderConfigId IssueProviderConfigId)>> GetActiveIssueIdentifiersAsync(CancellationToken ct)
    {
        var pairs = await _apiClient.GetActiveIdentifiersAsync(ct);
        return pairs
            .Select(p => ((IssueIdentifier)p.IssueIdentifier, (ProviderConfigId)p.IssueProviderConfigId))
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
