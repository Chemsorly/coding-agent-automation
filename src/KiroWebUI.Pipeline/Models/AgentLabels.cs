namespace KiroWebUI.Pipeline.Models;

/// <summary>
/// Defines the agent status labels applied to GitHub issues during pipeline execution.
/// Only one <c>agent:*</c> label should be present on an issue at a time.
/// </summary>
public static class AgentLabels
{
    public const string Next = "agent:next";
    public const string InProgress = "agent:in-progress";
    public const string Error = "agent:error";
    public const string NeedsRefinement = "agent:needs-refinement";
    public const string WontDo = "agent:wont-do";

    /// <summary>All agent labels with their display colors (without '#' prefix).</summary>
    public static readonly IReadOnlyList<(string Name, string Color)> Definitions = new[]
    {
        (Next, "0e8a16"),
        (InProgress, "1d76db"),
        (Error, "d73a4a"),
        (NeedsRefinement, "fbca04"),
        (WontDo, "cfd3d7")
    };

    /// <summary>All agent label names.</summary>
    public static readonly IReadOnlyList<string> All = Definitions.Select(d => d.Name).ToList().AsReadOnly();
}
