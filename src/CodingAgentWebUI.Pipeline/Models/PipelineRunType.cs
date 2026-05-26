namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Discriminates between pipeline run types: implementation, review, and decomposition phases.
/// </summary>
public enum PipelineRunType
{
    /// <summary>Standard issue → implementation → PR workflow.</summary>
    Implementation,

    /// <summary>PR → code review → review comment workflow.</summary>
    Review,

    /// <summary>Epic decomposition Phase 1 — explore codebase and produce validated plan.</summary>
    DecompositionAnalysis,

    /// <summary>Epic decomposition Phase 2 — create implementation-ready sub-issues from approved plan.</summary>
    Decomposition
}
