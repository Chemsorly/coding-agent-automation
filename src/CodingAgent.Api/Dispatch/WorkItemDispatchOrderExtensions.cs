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
    ///   <item>RunType tier ASC — Triage (0) &gt; Review (1) &gt; Decomposition (2) &gt; Implementation (3) &gt; Consolidation (4).
    ///     EF Core translates the ternary chain to a SQL <c>CASE WHEN … THEN … END</c> expression.</item>
    ///   <item><c>PriorityWeight</c> DESC — manual items (100) before automated (0) within a tier.</item>
    ///   <item><c>CreatedAt</c> ASC — FIFO tiebreaker within the same tier and weight.</item>
    /// </list>
    /// Decision: <c>decisions.md</c> "Dispatch priority: static ordering Triage &gt; Review &gt; Decomposition &gt; Implementation &gt; Consolidation".
    /// </summary>
    internal static IOrderedQueryable<WorkItemEntity> ApplyDispatchOrder(
        this IQueryable<WorkItemEntity> source) =>
        source
            .OrderBy(w =>
                w.TaskType == WorkItemTaskType.Triage         ? 0 :
                w.TaskType == WorkItemTaskType.Review         ? 1 :
                w.TaskType == WorkItemTaskType.Decomposition  ? 2 :
                w.TaskType == WorkItemTaskType.Implementation ? 3 :
                /* Consolidation */                             4)
            .ThenByDescending(w => w.PriorityWeight)
            .ThenBy(w => w.CreatedAt);
}
