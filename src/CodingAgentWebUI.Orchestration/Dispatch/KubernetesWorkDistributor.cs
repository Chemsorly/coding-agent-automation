using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Kubernetes work distributor. Creates a WorkItem via the Pipeline API (Status=Pending).
/// Pod spawning is handled separately by <see cref="DispatchService"/>, which polls for Pending
/// items via the API and creates K8s Jobs.
/// </summary>
/// <remarks>
/// Work item creation is API-backed (<see cref="IPipelineApiWorkItemClient.CreateAsync"/>).
/// Cancellation, status queries, and dedup operations are inherited from
/// <see cref="DbWorkDistributorBase"/> via EF — these remain local until Spec 045 removes
/// the monolith's direct DB access.
/// </remarks>
public sealed class KubernetesWorkDistributor : DbWorkDistributorBase
{
    private readonly IPipelineApiWorkItemClient _apiClient;

    public KubernetesWorkDistributor(
        IPipelineApiWorkItemClient apiClient,
        IDbContextFactory<PipelineDbContext> dbFactory,
        WorkItemTransitionService transitionService,
        ILogger<KubernetesWorkDistributor> logger)
        : base(dbFactory, transitionService, logger)
    {
        _apiClient = apiClient;
    }

    /// <inheritdoc />
    public override async Task<DistributionResult> DistributeAsync(JobDistributionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var workItemId = await _apiClient.CreateAsync(request, ct);
            Logger.LogInformation(
                "WorkItem {WorkItemId} created via Pipeline API for issue {IssueIdentifier}",
                workItemId, request.IssueIdentifier);
            return new DistributionResult(true, workItemId.ToString(), null, Queued: true);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to create WorkItem via Pipeline API for issue {IssueIdentifier}",
                request.IssueIdentifier);
            return new DistributionResult(false, null, ex.Message);
        }
    }
}
