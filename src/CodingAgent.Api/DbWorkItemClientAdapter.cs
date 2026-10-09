using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Orchestration.Dispatch;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Api;

/// <summary>
/// Minimal <see cref="IK8sJobNameLookup"/> that reads the K8s Job name from the local
/// Postgres database rather than making an HTTP round-trip to itself.
/// Used by <see cref="Orchestration.Dispatch.KubernetesJobCleanup"/> in the API
/// process so that cancelled or failed runs can delete their K8s Jobs without an HTTP
/// call to itself.
/// </summary>
internal sealed class DbWorkItemClientAdapter : IK8sJobNameLookup
{
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;

    public DbWorkItemClientAdapter(IDbContextFactory<PipelineDbContext> dbFactory)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        _dbFactory = dbFactory;
    }

    /// <inheritdoc />
    public async Task<string?> GetK8sJobNameAsync(Guid workItemId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var name = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Id == workItemId)
            .Select(w => w.K8sJobName)
            .FirstOrDefaultAsync(ct);
        return string.IsNullOrEmpty(name) ? null : name;
    }
}

/// <summary>
/// No-op implementation of <see cref="IJobCleanupStrategy"/> used when K8s is unavailable.
/// </summary>
internal sealed class NoOpJobCleanupStrategy : CodingAgent.Kubernetes.IJobCleanupStrategy
{
    public Task TryDeleteJobForRunAsync(Pipeline.Models.RunId runId, CancellationToken ct) => Task.CompletedTask;
}
