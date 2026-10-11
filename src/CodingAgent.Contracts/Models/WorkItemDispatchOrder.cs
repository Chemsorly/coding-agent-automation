namespace CodingAgent.Pipeline.Models;

/// <summary>
/// The default and the normal form of <see cref="PipelineConfiguration.DispatchOrder"/>, the order in which pending work
/// dispatches by task type.
/// </summary>
public static class WorkItemDispatchOrder
{
    /// <summary>Triage, Review, Decomposition, Implementation, Consolidation.</summary>
    public static IReadOnlyList<WorkItemTaskType> Default { get; } =
    [
        WorkItemTaskType.Triage, WorkItemTaskType.Review, WorkItemTaskType.Decomposition,
        WorkItemTaskType.Implementation, WorkItemTaskType.Consolidation,
    ];

    /// <summary>
    /// <paramref name="order"/> as a complete order: each task type once, at its first position. Unknown values are
    /// dropped, and missing task types follow in their default order. Null is the default.
    /// </summary>
    public static IReadOnlyList<WorkItemTaskType> Normalize(IEnumerable<WorkItemTaskType>? order) =>
        order is null ? Default : order.Where(t => Enum.IsDefined(t)).Concat(Default).Distinct().ToArray();
}
