using System.Diagnostics;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Extension methods for <see cref="WorkItemTaskType"/>.
/// </summary>
public static class WorkItemTaskTypeExtensions
{
    /// <summary>
    /// Maps a <see cref="WorkItemTaskType"/> to its canonical <see cref="PipelineRunType"/>.
    /// Pending items are always Phase 1 (analysis) for decomposition jobs.
    /// This is the single authoritative mapping in the codebase — all callers must use this
    /// rather than maintaining independent switch expressions or ternaries.
    /// </summary>
    /// <exception cref="UnreachableException">
    /// Thrown for any unrecognised <see cref="WorkItemTaskType"/> value to prevent silent
    /// fallback to a wrong run type when a new enum member is added in future.
    /// </exception>
    public static PipelineRunType ToDefaultRunType(this WorkItemTaskType taskType) => taskType switch
    {
        WorkItemTaskType.Implementation => PipelineRunType.Implementation,
        WorkItemTaskType.Review         => PipelineRunType.Review,
        WorkItemTaskType.Decomposition  => PipelineRunType.DecompositionAnalysis,
        WorkItemTaskType.Consolidation  => PipelineRunType.Consolidation,
        _                               => throw new UnreachableException($"Unhandled WorkItemTaskType: {taskType}")
    };
}
