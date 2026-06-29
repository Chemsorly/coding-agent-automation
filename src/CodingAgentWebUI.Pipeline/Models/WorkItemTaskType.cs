namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Discriminates the type of work a work item represents.
/// </summary>
public enum WorkItemTaskType
{
    /// <summary>Standard issue implementation workflow.</summary>
    Implementation,

    /// <summary>Pull request code review workflow.</summary>
    Review,

    /// <summary>Epic decomposition into sub-issues.</summary>
    Decomposition,

    /// <summary>Consolidation of multiple results.</summary>
    Consolidation
}
