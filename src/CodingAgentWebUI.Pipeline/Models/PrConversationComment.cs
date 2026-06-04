namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Represents a comment from a PR/MR conversation thread, including
/// discussion comments, review findings, and human replies.
/// </summary>
public sealed class PrConversationComment
{
    public required string Author { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required string Body { get; init; }
    public required bool IsBot { get; init; }

    /// <summary>Whether this comment is from the PR author.</summary>
    public bool IsAuthor { get; init; }

    /// <summary>File path if this is an inline review comment, null for general discussion.</summary>
    public string? FilePath { get; init; }

    /// <summary>Line number if this is an inline review comment.</summary>
    public int? Line { get; init; }

    /// <summary>Whether the review thread this comment belongs to has been resolved/dismissed.</summary>
    public bool? IsResolved { get; init; }
}
