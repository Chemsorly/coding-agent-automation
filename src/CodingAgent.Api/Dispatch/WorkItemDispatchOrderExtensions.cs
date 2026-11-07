using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
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
    ///   <item>Spec 056 DD10: items held for an operator hold (<see cref="HoldReasons.OperatorHolds"/>: a paused
    ///     credential or a route that cannot run) last, so they never fill the dispatch window. The null check is needed:
    ///     in Postgres <c>x = ANY(@p)</c> is NULL for a NULL <c>x</c>, and NULLs sort last.</item>
    ///   <item>Task type tier ASC — a task type's position in <paramref name="dispatchOrder"/>
    ///     (<see cref="PipelineConfiguration.DispatchOrder"/>; default Triage, Review, Decomposition, Implementation,
    ///     Consolidation). EF Core translates the ternary chain to a SQL <c>CASE WHEN … THEN … END</c> expression
    ///     with the positions as parameters.</item>
    ///   <item><c>PriorityWeight</c> DESC — manual items (100) before automated (0) within a tier.</item>
    ///   <item><c>CreatedAt</c> ASC — FIFO tiebreaker within the same tier and weight.</item>
    /// </list>
    /// Decision: <c>decisions.md</c> "Dispatch priority: one configurable order by task type".
    /// </summary>
    internal static IOrderedQueryable<WorkItemEntity> ApplyDispatchOrder(
        this IQueryable<WorkItemEntity> source, IReadOnlyList<WorkItemTaskType> dispatchOrder)
    {
        var order = WorkItemDispatchOrder.Normalize(dispatchOrder);
        var triage = Tier(order, WorkItemTaskType.Triage);
        var review = Tier(order, WorkItemTaskType.Review);
        var decomposition = Tier(order, WorkItemTaskType.Decomposition);
        var implementation = Tier(order, WorkItemTaskType.Implementation);
        var consolidation = Tier(order, WorkItemTaskType.Consolidation);

        return source
            .OrderByOperatorHoldsLast()
            .ThenBy(w =>
                w.TaskType == WorkItemTaskType.Triage         ? triage :
                w.TaskType == WorkItemTaskType.Review         ? review :
                w.TaskType == WorkItemTaskType.Decomposition  ? decomposition :
                w.TaskType == WorkItemTaskType.Implementation ? implementation :
                /* Consolidation */                             consolidation)
            .ThenByDescending(w => w.PriorityWeight)
            .ThenBy(w => w.CreatedAt);
    }

    private static int Tier(IReadOnlyList<WorkItemTaskType> order, WorkItemTaskType taskType)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (order[i] == taskType)
                return i;
        }
        return order.Count;
    }
}
