using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Infrastructure.Persistence;

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
}
