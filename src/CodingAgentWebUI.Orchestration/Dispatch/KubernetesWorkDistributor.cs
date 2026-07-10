using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
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
/// <remarks>
/// Inherits shared DB operations (RunId resolution, cancel, status, dedup) from <see cref="DbWorkDistributorBase"/>.
/// Only overrides <see cref="DistributeAsync"/> to insert as Pending (all task types, including consolidation).
/// </remarks>
public sealed class KubernetesWorkDistributor : DbWorkDistributorBase
{
    public KubernetesWorkDistributor(
        IDbContextFactory<PipelineDbContext> dbFactory,
        WorkItemTransitionService transitionService,
        ILogger<KubernetesWorkDistributor> logger)
        : base(dbFactory, transitionService, logger) { }

    /// <inheritdoc />
    public override async Task<DistributionResult> DistributeAsync(JobDistributionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await InsertWorkItemAsync(request, WorkItemStatus.Pending, ct,
            queued: true, successMessage: null);
    }
}
