namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Tracks the outcome of creating a single sub-issue.
/// </summary>
public sealed record SubIssueCreationResult
{
    /// <summary>The sub-issue title that was attempted.</summary>
    public required string Title { get; init; }

    /// <summary>Whether the issue was created successfully.</summary>
    public required bool Success { get; init; }

    /// <summary>Issue identifier (e.g., "456"), or null if creation failed.</summary>
    public string? Identifier { get; init; }

    /// <summary>URL of the created issue, or null if creation failed.</summary>
    public string? Url { get; init; }

    /// <summary>Reason for failure, or null if creation succeeded.</summary>
    public string? FailureReason { get; init; }

    /// <summary>
    /// Whether this proposal was skipped because the configured
    /// <c>MaxDecompositionSubIssues</c> cap was reached.
    /// When true, <see cref="Success"/> is false and <see cref="FailureReason"/> names the cap.
    /// These are NOT counted as failed creations for the purpose of outcome label selection.
    /// </summary>
    public bool SkippedByCap { get; init; }
}
