namespace CodingAgentWebUI.Pipeline.Models;

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
    public const string Cancelled = "agent:cancelled";
    public const string Done = "agent:done";

    // Epic decomposition labels
    public const string Epic = "agent:epic";
    public const string EpicReview = "agent:epic-review";
    public const string EpicApproved = "agent:epic-approved";

    // Consolidation labels (applied to auto-generated refactoring issues)
    public const string Refactoring = "refactoring";
    public const string AgentGenerated = "agent-generated";

    /// <summary>All agent labels with their display colors (without '#' prefix).</summary>
    public static readonly IReadOnlyList<(string Name, string Color)> Definitions = new[]
    {
        (Next, "0e8a16"),
        (InProgress, "1d76db"),
        (Error, "d73a4a"),
        (NeedsRefinement, "fbca04"),
        (WontDo, "cfd3d7"),
        (Cancelled, "c5def5"),
        (Done, "0075ca"),
        (Refactoring, "5319e7"),
        (AgentGenerated, "bfd4f2"),
        (Epic, "7057ff"),
        (EpicReview, "fbca04"),
        (EpicApproved, "0e8a16")
    };

    /// <summary>All agent label names.</summary>
    public static readonly IReadOnlyList<string> All = Definitions.Select(d => d.Name).ToList().AsReadOnly();
}
