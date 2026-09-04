using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace CodingAgentWebUI.Infrastructure.Persistence.Services;

/// <summary>
/// EF Core-backed implementation of <see cref="IWorkItemTransitionStore"/> — the direct WorkItem
/// database operations the SignalR agent hub facade needs. Extracted from
/// <c>AgentHubFacade</c> in Spec 048 Phase 2 so the Hub library no longer references
/// <c>Infrastructure.Persistence</c>; wired only in the API host (the sole database owner).
/// Retry-count / re-queue delegate to <see cref="WorkItemTransitionService"/> (which owns the
/// concurrency-safe transition primitives); the metadata reads and the throttled progress write
/// carry the exact logic that previously lived in the facade.
/// </summary>
public sealed class EfWorkItemTransitionStore : IWorkItemTransitionStore
{
    /// <summary>
    /// Throttle interval for LastProgressAt DB writes. Only writes when the existing
    /// DB value is null or older than this threshold.
    /// </summary>
    private static readonly TimeSpan ProgressWriteThrottle = TimeSpan.FromMinutes(5);

    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly WorkItemTransitionService _transitionService;

    public EfWorkItemTransitionStore(
        IDbContextFactory<PipelineDbContext> dbFactory,
        WorkItemTransitionService transitionService)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        ArgumentNullException.ThrowIfNull(transitionService);
        _dbFactory = dbFactory;
        _transitionService = transitionService;
    }

    /// <inheritdoc />
    public Task<int> GetRetryCountAsync(Guid workItemId, CancellationToken ct)
        => _transitionService.GetRetryCountAsync(workItemId, ct);

    /// <inheritdoc />
    public Task RequeueAsync(Guid workItemId, CancellationToken ct)
        => _transitionService.RequeueAsync(workItemId, ct);

    /// <inheritdoc />
    public async Task<(string? RepoProviderConfigId, string? BrainProviderConfigId)?> GetWorkItemProviderConfigIdsAsync(
        Guid workItemId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var payload = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Id == workItemId)
            .Select(w => w.Payload)
            .FirstOrDefaultAsync(ct);

        if (payload is null) return null;

        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        var repoConfigId = root.TryGetProperty("repoProviderConfigId", out var repoProp)
            ? repoProp.GetString() : null;
        var brainConfigId = root.TryGetProperty("brainProviderConfigId", out var brainProp)
            ? brainProp.GetString() : null;

        return (repoConfigId, brainConfigId);
    }

    /// <inheritdoc />
    public async Task<(string IssueIdentifier, string IssueProviderConfigId)?> GetWorkItemIssueMetadataAsync(
        Guid workItemId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var result = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Id == workItemId)
            .Select(w => new { w.IssueIdentifier, w.IssueProviderConfigId })
            .FirstOrDefaultAsync(ct);

        if (result is null || string.IsNullOrEmpty(result.IssueIdentifier))
            return null;

        return (result.IssueIdentifier, result.IssueProviderConfigId);
    }

    /// <inheritdoc />
    public async Task TouchLastProgressAsync(Guid workItemId, DateTimeOffset timestamp, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var item = await db.WorkItems.FindAsync([workItemId], ct);

        if (item is null)
            return;

        // Throttle: skip write if DB value is recent enough (uses wall clock, not agent timestamp,
        // to avoid clock-skew issues where a behind-clock agent could permanently suppress writes)
        if (item.LastProgressAt.HasValue &&
            (DateTimeOffset.UtcNow - item.LastProgressAt.Value) < ProgressWriteThrottle)
            return;

        item.LastProgressAt = timestamp;
        await db.SaveChangesAsync(ct);
    }
}
