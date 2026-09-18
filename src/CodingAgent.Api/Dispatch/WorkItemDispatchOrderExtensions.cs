using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Api.Dispatch;

/// <summary>
/// Extension methods that apply the canonical dispatch sort order to a
/// <see cref="WorkItemEntity"/> queryable.
/// </summary>
internal static class WorkItemDispatchOrderExtensions
{
    /// <summary>
    /// Applies the standard three-level dispatch sort:
    /// <list type="number">
    ///   <item>RunType tier ASC — Review (0) &gt; Decomposition (1) &gt; Implementation (2) &gt; Consolidation (3).
    ///     EF Core translates the ternary chain to a SQL <c>CASE WHEN … THEN … END</c> expression.</item>
    ///   <item><c>PriorityWeight</c> DESC — manual items (100) before automated (0) within a tier.</item>
    ///   <item><c>CreatedAt</c> ASC — FIFO tiebreaker within the same tier and weight.</item>
    /// </list>
    /// Decision: <c>decisions.md</c> "Dispatch priority: static ordering Review &gt; Decomp &gt; Impl &gt; Consolidation"
    /// and "PriorityWeight: secondary sort key within RunType tier".
    /// </summary>
    internal static IOrderedQueryable<WorkItemEntity> ApplyDispatchOrder(
        this IQueryable<WorkItemEntity> source) =>
        source
            .OrderBy(w =>
                w.TaskType == WorkItemTaskType.Review         ? 0 :
                w.TaskType == WorkItemTaskType.Decomposition  ? 1 :
                w.TaskType == WorkItemTaskType.Implementation ? 2 :
                /* Consolidation */                             3)
            .ThenByDescending(w => w.PriorityWeight)
            .ThenBy(w => w.CreatedAt);
}
