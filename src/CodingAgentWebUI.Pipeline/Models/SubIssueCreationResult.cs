namespace CodingAgentWebUI.Pipeline.Models;

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
}
