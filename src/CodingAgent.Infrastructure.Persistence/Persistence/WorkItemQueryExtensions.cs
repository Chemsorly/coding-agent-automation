using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Infrastructure.Persistence;

/// <summary>
/// LINQ query extensions for <see cref="WorkItemEntity"/> queryables.
/// </summary>
public static class WorkItemQueryExtensions
{
    /// <summary>
    /// Filters a WorkItem query to only include "active" items —
    /// those in <see cref="WorkItemStatus.Dispatched"/> or <see cref="WorkItemStatus.Running"/> state.
    /// </summary>
    /// <remarks>
    /// This is the single definition of the "active work item" concept. All callers that need
    /// to filter for currently-running or dispatched work items must use this method rather than
    /// duplicating the status predicate inline. EF Core translates this to
    /// <c>WHERE Status = @p0 OR Status = @p1</c> — identical to the inline form.
    /// </remarks>
    public static IQueryable<WorkItemEntity> WhereActive(this IQueryable<WorkItemEntity> query)
        => query.Where(w =>
            w.Status == WorkItemStatus.Dispatched ||
            w.Status == WorkItemStatus.Running);

    /// <summary>
    /// Filters a WorkItem query to include items that are either active (status in
    /// <see cref="PipelineConstants.ActiveWorkItemStatuses"/>) or recently terminated
    /// (terminal status with <see cref="WorkItemEntity.CompletedAt"/> at or after
    /// <paramref name="cutoff"/>).
    /// </summary>
    /// <param name="query">The source queryable.</param>
    /// <param name="cutoff">
    /// The earliest <see cref="WorkItemEntity.CompletedAt"/> that counts as "recently terminal".
    /// Callers supply <c>DateTimeOffset.UtcNow - PipelineConstants.DefaultRestartDedupCooldown</c>
    /// so that the timestamp is a caller-controlled constant from EF Core's perspective and the
    /// method body remains a pure expression tree with no side-effects.
    /// </param>
    /// <remarks>
    /// This is the single definition of the dispatch-dedup predicate. Both
    /// <c>GetIsDistributed</c> and <c>GetActiveIdentifiers</c> must call this method so the
    /// "active or recently terminal" semantics cannot drift between the two endpoints.
    /// EF Core translates this to a single WHERE clause:
    /// <c>WHERE Status IN (...) OR (CompletedAt IS NOT NULL AND CompletedAt &gt;= @cutoff)</c>.
    /// </remarks>
    public static IQueryable<WorkItemEntity> WhereActiveOrRecentlyTerminal(
        this IQueryable<WorkItemEntity> query,
        DateTimeOffset cutoff)
    {
        var activeStatuses = PipelineConstants.ActiveWorkItemStatuses;
        return query.Where(w =>
            activeStatuses.Contains(w.Status) ||
            (w.CompletedAt != null && w.CompletedAt >= cutoff));
    }
}
