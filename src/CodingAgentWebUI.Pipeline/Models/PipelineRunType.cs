namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Discriminates between implementation runs and PR review runs.
/// </summary>
public enum PipelineRunType
{
    /// <summary>Standard issue → implementation → PR workflow.</summary>
    Implementation,

    /// <summary>PR → code review → review comment workflow.</summary>
    Review
}
