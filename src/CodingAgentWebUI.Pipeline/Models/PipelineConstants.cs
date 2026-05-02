namespace CodingAgentWebUI.Pipeline.Models;

// TODO: Consider splitting formatting constants (MaxBranchNameLength, MaxCommentLength) into a separate FormattingConstants class to match the domain organization described in issue #238.

/// <summary>
/// Centralized domain constants for the pipeline. Avoids scattering magic numbers
/// across production and test code.
/// </summary>
public static class PipelineConstants
{
    /// <summary>Default page size for paginated API calls (issue listing).</summary>
    public const int DefaultPageSize = 100;

    /// <summary>Maximum total length of a generated branch name.</summary>
    public const int MaxBranchNameLength = 100;

    /// <summary>Maximum character length for a comment body in the PR description before truncation.</summary>
    public const int MaxCommentLength = 200;

    /// <summary>Minimum length in bytes for analysis.md to be considered valid.</summary>
    public const int MinAnalysisLength = 100;

    /// <summary>Default capacity (line count) for the output ring buffer.</summary>
    public const int DefaultOutputBufferCapacity = 10_000;
}
