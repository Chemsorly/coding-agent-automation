using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Outcome of the pipeline run from the feedback perspective.
/// </summary>
public enum FeedbackOutcome
{
    Success,
    Failure
}

/// <summary>
/// Structured feedback collected from the agent after a pipeline run.
/// Contains both harness feedback (for the pipeline team) and issue feedback
/// (for the issue author). Persisted alongside the run summary.
/// </summary>
[MessagePackObject]
public sealed class RunFeedback
{
    [Key(0)]
    public required FeedbackOutcome Outcome { get; init; }

    [Key(1)]
    public required DateTime CollectedAtUtc { get; init; }

    [Key(2)]
    public required HarnessFeedback Harness { get; init; }

    [Key(3)]
    public IssueFeedback? Issue { get; init; }
}

/// <summary>
/// Feedback about the pipeline, tools, prompts, and working environment.
/// Targets the pipeline team for harness improvements.
/// </summary>
[MessagePackObject]
public sealed class HarnessFeedback
{
    /// <summary>Short root-cause label for clustering (e.g., "missing file context", "mcp tool timeout"). Max 50 chars.</summary>
    [Key(0)]
    public string? Category { get; init; }

    /// <summary>What blocked progress (required for Failure outcome). Max 500 chars.</summary>
    [Key(1)]
    public string? StuckReason { get; init; }

    /// <summary>Files, data, or information that should have been provided upfront. Max 5 items.</summary>
    [Key(2)]
    public IReadOnlyList<string> MissingContext { get; init; } = [];

    /// <summary>Tools or abilities the agent wished it had. Max 5 items.</summary>
    [Key(3)]
    public IReadOnlyList<string> MissingCapabilities { get; init; } = [];

    /// <summary>Confusing, contradictory, or unhelpful instructions from the pipeline. Max 5 items.</summary>
    [Key(4)]
    public IReadOnlyList<string> PromptIssues { get; init; } = [];

    /// <summary>Concrete improvements to the harness. Max 3 items.</summary>
    [Key(5)]
    public IReadOnlyList<string> Suggestions { get; init; } = [];
}

/// <summary>
/// Feedback about the target repository and issue quality.
/// Targets the issue author for action. Entirely nullable — agents may report
/// no issue-level feedback if the issue was well-written.
/// </summary>
[MessagePackObject]
public sealed class IssueFeedback
{
    /// <summary>Short label for issue-quality clustering (e.g., "contradictory acceptance criteria", "missing component"). Max 50 chars.</summary>
    [Key(0)]
    public string? Category { get; init; }

    /// <summary>What's wrong with the issue or repo. Max 500 chars.</summary>
    [Key(1)]
    public string? Description { get; init; }

    /// <summary>Specific files where problems were found. Max 5 items.</summary>
    [Key(2)]
    public IReadOnlyList<string> AffectedFiles { get; init; } = [];

    /// <summary>What the issue author should do. Max 500 chars.</summary>
    [Key(3)]
    public string? HumanActionNeeded { get; init; }
}

/// <summary>
/// Validation constraints for feedback fields. Used during deserialization
/// to truncate oversized fields rather than rejecting them.
/// </summary>
public static class FeedbackConstraints
{
    public const int MaxCategoryLength = 50;
    public const int MaxStringLength = 500;
    public const int MaxMissingContextItems = 5;
    public const int MaxMissingCapabilitiesItems = 5;
    public const int MaxPromptIssuesItems = 5;
    public const int MaxSuggestionsItems = 3;
    public const int MaxAffectedFilesItems = 5;
    public const int FailureFeedbackTimeoutSeconds = 60;
    public const int MaxRecentRunsForCategories = 50;
}
