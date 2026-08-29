using System.Diagnostics;

namespace CodingAgentWebUI.Pipeline.Models;

public static class WorkItemTaskTypeExtensions
{
    /// <summary>
    /// Maps a <see cref="WorkItemTaskType"/> to its canonical <see cref="PipelineRunType"/> for UI display.
    /// Pending items are always Phase 1; <see cref="WorkItemTaskType.Decomposition"/> therefore maps to
    /// <see cref="PipelineRunType.DecompositionAnalysis"/> (Phase 1), not <see cref="PipelineRunType.Decomposition"/> (Phase 2).
    /// Throws <see cref="UnreachableException"/> for any unhandled value — callers must not silently fall back.
    /// </summary>
    public static PipelineRunType ToDefaultRunType(this WorkItemTaskType taskType) => taskType switch
    {
        WorkItemTaskType.Implementation => PipelineRunType.Implementation,
        WorkItemTaskType.Review         => PipelineRunType.Review,
        WorkItemTaskType.Decomposition  => PipelineRunType.DecompositionAnalysis,
        WorkItemTaskType.Consolidation  => PipelineRunType.Consolidation,
        _                               => throw new UnreachableException($"Unhandled WorkItemTaskType: {taskType}")
    };
}
