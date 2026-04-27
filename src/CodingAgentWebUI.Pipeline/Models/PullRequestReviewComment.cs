namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// A single review comment from a pull request (inline or conversation).
/// </summary>
public sealed class PullRequestReviewComment
{
    public required string Id { get; init; }
    public required string Body { get; init; }
    public required string Author { get; init; }
    public required DateTime CreatedAt { get; init; }
    /// <summary>File path the comment is on, or null for general conversation comments.</summary>
    public string? Path { get; init; }
}
