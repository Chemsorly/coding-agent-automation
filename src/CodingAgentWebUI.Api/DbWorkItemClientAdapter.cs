using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics.CodeAnalysis;

namespace CodingAgentWebUI.Api;

/// <summary>
/// Minimal <see cref="IPipelineApiWorkItemClient"/> adapter that reads from the local
/// Postgres database rather than making an HTTP round-trip to itself.
/// Used exclusively by <see cref="Orchestration.Dispatch.KubernetesJobCleanup"/> in the API
/// process so that cancelled/failed runs can delete their K8s Jobs without HTTP self-calls.
/// All other methods throw <see cref="NotSupportedException"/> — they are never called
/// by <c>KubernetesJobCleanup</c>.
/// </summary>
internal sealed class DbWorkItemClientAdapter : IPipelineApiWorkItemClient
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

    // ── Not used by KubernetesJobCleanup — throw to surface accidental usage ──

    [ExcludeFromCodeCoverage(Justification = "Intentional NotSupportedException stubs — never called by KubernetesJobCleanup")]
    public Task<IReadOnlyList<PendingWorkItemDto>> GetPendingAsync(int maxResults = 50, CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(DbWorkItemClientAdapter)} only supports {nameof(GetK8sJobNameAsync)}.");

    [ExcludeFromCodeCoverage(Justification = "Intentional NotSupportedException stub")]
    public Task<WorkItemClaimResponse?> ClaimAsync(Guid workItemId, ClaimWorkItemRequest request, CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(DbWorkItemClientAdapter)} only supports {nameof(GetK8sJobNameAsync)}.");

    [ExcludeFromCodeCoverage(Justification = "Intentional NotSupportedException stub")]
    public Task<JobAssignmentMessage?> GetAssignmentAsync(Guid workItemId, CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(DbWorkItemClientAdapter)} only supports {nameof(GetK8sJobNameAsync)}.");

    [ExcludeFromCodeCoverage(Justification = "Intentional NotSupportedException stub")]
    public Task PostStatusAsync(Guid workItemId, WorkItemStatusUpdate request, CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(DbWorkItemClientAdapter)} only supports {nameof(GetK8sJobNameAsync)}.");

    [ExcludeFromCodeCoverage(Justification = "Intentional NotSupportedException stub")]
    public Task RequeueAsync(Guid workItemId, CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(DbWorkItemClientAdapter)} only supports {nameof(GetK8sJobNameAsync)}.");

    [ExcludeFromCodeCoverage(Justification = "Intentional NotSupportedException stub")]
    public Task<int> GetRetryCountAsync(Guid workItemId, CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(DbWorkItemClientAdapter)} only supports {nameof(GetK8sJobNameAsync)}.");

    [ExcludeFromCodeCoverage(Justification = "Intentional NotSupportedException stub")]
    public Task<WorkItemStalenessResult?> GetStalenessAsync(string issueIdentifier, string issueProviderConfigId, DateTimeOffset since, CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(DbWorkItemClientAdapter)} only supports {nameof(GetK8sJobNameAsync)}.");

    [ExcludeFromCodeCoverage(Justification = "Intentional NotSupportedException stub")]
    public Task<Guid> CreateAsync(JobDistributionRequest request, CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(DbWorkItemClientAdapter)} only supports {nameof(GetK8sJobNameAsync)}.");

    [ExcludeFromCodeCoverage(Justification = "Intentional NotSupportedException stub")]
    public Task PostLabelSwapAsync(Guid workItemId, string label, CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(DbWorkItemClientAdapter)} only supports {nameof(GetK8sJobNameAsync)}.");

    [ExcludeFromCodeCoverage(Justification = "Intentional NotSupportedException stub")]
    public Task<IReadOnlyList<ActiveWorkItemDto>> GetActiveAsync(int olderThanSeconds, CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(DbWorkItemClientAdapter)} only supports {nameof(GetK8sJobNameAsync)}.");

    [ExcludeFromCodeCoverage(Justification = "Intentional NotSupportedException stub")]
    public Task PostLastProgressAsync(Guid workItemId, DateTimeOffset timestamp, CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(DbWorkItemClientAdapter)} only supports {nameof(GetK8sJobNameAsync)}.");

    [ExcludeFromCodeCoverage(Justification = "Intentional NotSupportedException stub")]
    public Task<WorkItemStatus?> GetStatusAsync(Guid workItemId, CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(DbWorkItemClientAdapter)} only supports {nameof(GetK8sJobNameAsync)}.");

    [ExcludeFromCodeCoverage(Justification = "Intentional NotSupportedException stub")]
    public Task<bool> IsIssueDistributedAsync(string issueIdentifier, string issueProviderConfigId, CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(DbWorkItemClientAdapter)} only supports {nameof(GetK8sJobNameAsync)}.");

    [ExcludeFromCodeCoverage(Justification = "Intentional NotSupportedException stub")]
    public Task<IReadOnlyList<(string IssueIdentifier, string IssueProviderConfigId)>> GetActiveIdentifiersAsync(CancellationToken ct = default)
        => throw new NotSupportedException($"{nameof(DbWorkItemClientAdapter)} only supports {nameof(GetK8sJobNameAsync)}.");
}

/// <summary>
/// No-op implementation of <see cref="IJobCleanupStrategy"/> used when K8s is unavailable.
/// </summary>
internal sealed class NoOpJobCleanupStrategy : CodingAgentWebUI.Kubernetes.IJobCleanupStrategy
{
    public Task TryDeleteJobForRunAsync(Pipeline.Models.RunId runId, CancellationToken ct) => Task.CompletedTask;
}
