using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Api.Dispatch;

/// <summary>What <see cref="WorkItemStore.CreateAsync"/> did with a request.</summary>
internal enum WorkItemCreateOutcome
{
    /// <summary>A new Pending work item was inserted.</summary>
    Created,

    /// <summary>A work item with the request's ID already exists (an idempotent retry).</summary>
    AlreadyCreated,

    /// <summary>Another live work item exists for the same issue and issue provider.</summary>
    LiveItemExists,
}

/// <summary>
/// The work-item reads and writes of the queue paths (spec 051 2.3a): enqueue, status, and the
/// dedup queries. The <c>/api/work-items</c> handlers and the in-process <see cref="ApiWorkDistributor"/>
/// both go through here, so a request behaves the same over HTTP and in the API process.
/// </summary>
internal sealed class WorkItemStore(IDbContextFactory<PipelineDbContext> dbFactory, IOrchestratorRunService runService)
{
    /// <summary>
    /// Inserts the request as a Pending work item and shows its run in the UI at once. The work
    /// item's ID is the request's <c>RunId</c> when it has one, so hub groups route by it.
    /// </summary>
    public async Task<(WorkItemCreateOutcome Outcome, Guid WorkItemId)> CreateAsync(JobDistributionRequest request, CancellationToken ct)
    {
        var workItemId = !string.IsNullOrEmpty(request.RunId) && Guid.TryParse(request.RunId, out var parsedRunId)
            ? parsedRunId
            : Guid.NewGuid();

        var entity = WorkItemDispatchEndpoints.BuildWorkItemEntity(request, workItemId);

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);

            // Pre-checks avoid the EF error log of a Postgres 23505 on the common paths (issue #2956).
            // They are a TOCTOU (issue #3243); the unique-violation catch below is the race backstop.
            // 1. Idempotent PK retry: the same RunId was already created.
            if (await db.WorkItems.AnyAsync(w => w.Id == workItemId, ct))
                return (WorkItemCreateOutcome.AlreadyCreated, workItemId);

            // 2. A live item for the same issue and provider (the partial unique index's predicate).
            var activeStatuses = PipelineConstants.ActiveWorkItemStatuses;
            if (await db.WorkItems.AnyAsync(
                    w => w.IssueIdentifier == entity.IssueIdentifier
                      && w.IssueProviderConfigId == entity.IssueProviderConfigId
                      && activeStatuses.Contains(w.Status),
                    ct))
                return (WorkItemCreateOutcome.LiveItemExists, workItemId);

            db.WorkItems.Add(entity);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (WorkItemDispatchEndpoints.IsUniqueViolation(ex))
        {
            // The context is faulted after the exception; re-query on a fresh one. A row with this
            // ID means an idempotent PK retry; otherwise a different run is already live for the issue.
            await using var freshDb = await dbFactory.CreateDbContextAsync(ct);
            return await freshDb.WorkItems.AnyAsync(w => w.Id == workItemId, ct)
                ? (WorkItemCreateOutcome.AlreadyCreated, workItemId)
                : (WorkItemCreateOutcome.LiveItemExists, workItemId);
        }

        // The in-memory run lets the UI subscribe to hub events right away (Req 1a.1, Option A).
        // Consolidation work items get one too (issue #3023): CancelRunAsync, CompleteRunAsync and
        // the history query depend on it.
        var run = PipelineRunFactory.CreateFromWorkItem(workItemId, request);
        if (run is not null)
            runService.AddRun(run);

        return (WorkItemCreateOutcome.Created, workItemId);
    }

    /// <summary>The work item's status, or null when it does not exist.</summary>
    public async Task<WorkItemStatus?> GetStatusAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Id == id)
            .Select(w => (WorkItemStatus?)w.Status)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Whether the issue has a live work item, or one that ended within
    /// <see cref="PipelineConstants.DefaultRestartDedupCooldown"/>. One query, so an item that
    /// ends in between cannot make both conditions read false.
    /// </summary>
    public async Task<bool> IsDistributedAsync(string issueIdentifier, string issueProviderConfigId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.WorkItems
            .AsNoTracking()
            .Where(w => w.IssueIdentifier == issueIdentifier && w.IssueProviderConfigId == issueProviderConfigId)
            .WhereActiveOrRecentlyTerminal(RecentTerminalCutoff())
            .AnyAsync(ct);
    }

    /// <summary>The (issue, issue provider) pairs that are live or ended within the dedup cooldown.</summary>
    public async Task<IReadOnlyList<(string IssueIdentifier, string IssueProviderConfigId)>> GetActiveIdentifiersAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var pairs = await db.WorkItems
            .AsNoTracking()
            .WhereActiveOrRecentlyTerminal(RecentTerminalCutoff())
            .Select(w => new { w.IssueIdentifier, w.IssueProviderConfigId })
            .Distinct()
            .ToListAsync(ct);
        return pairs.Select(p => (p.IssueIdentifier, p.IssueProviderConfigId)).ToList();
    }

    /// <summary>
    /// The drawers' badges for the issues of <paramref name="providerIds"/> that are live or ended within
    /// the dedup cooldown: <see cref="WorkItemStatus.Pending"/> (queued) when a Pending item exists,
    /// otherwise <see cref="WorkItemStatus.Running"/>.
    /// </summary>
    public async Task<IReadOnlyList<(string IssueIdentifier, string IssueProviderConfigId, WorkItemStatus Status)>> GetIssueStatusesAsync(
        IReadOnlyCollection<string> providerIds, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.WorkItems
            .AsNoTracking()
            .Where(w => providerIds.Contains(w.IssueProviderConfigId))
            .WhereActiveOrRecentlyTerminal(RecentTerminalCutoff())
            .Select(w => new { w.IssueIdentifier, w.IssueProviderConfigId, w.Status })
            .ToListAsync(ct);
        return rows
            .GroupBy(r => (r.IssueIdentifier, r.IssueProviderConfigId))
            .Select(g => (g.Key.IssueIdentifier, g.Key.IssueProviderConfigId,
                g.Any(r => r.Status == WorkItemStatus.Pending) ? WorkItemStatus.Pending : WorkItemStatus.Running))
            .ToList();
    }

    /// <summary>
    /// The decomposition work items that are Pending, Dispatched or Running. Both the analysis and
    /// the decomposition phase are stored as <see cref="WorkItemTaskType.Decomposition"/>; a separate
    /// analysis task type would have to be counted here too.
    /// </summary>
    public async Task<int> GetActiveDecompositionCountAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.WorkItems
            .AsNoTracking()
            .CountAsync(w =>
                w.TaskType == WorkItemTaskType.Decomposition &&
                (w.Status == WorkItemStatus.Pending ||
                 w.Status == WorkItemStatus.Dispatched ||
                 w.Status == WorkItemStatus.Running),
                ct);
    }

    private static DateTimeOffset RecentTerminalCutoff() => DateTimeOffset.UtcNow - PipelineConstants.DefaultRestartDedupCooldown;
}
