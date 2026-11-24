using MessagePack;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// A single review comment from a pull request (inline or conversation).
/// </summary>
[MessagePackObject]
public sealed class PullRequestReviewComment
{
    [Key(0)]
    public required string Author { get; init; }

    [Key(1)]
    public required string Body { get; init; }

    [Key(2)]
    public required DateTime CreatedAt { get; init; }

    [Key(3)]
    public required string Id { get; init; }

    /// <summary>File path the comment is on, or null for general conversation comments.</summary>
    [Key(4)]
    public string? Path { get; init; }

    /// <summary>
    /// Whether the author is on the repository. Set by the provider mapping and read only by
    /// SharedPrOperations.FinalizeReviewComments, in the same process. Not on the wire. Null counts as verified.
    /// </summary>
    [IgnoreMember]
    [System.Text.Json.Serialization.JsonIgnore]
    public bool? IsVerified { get; init; }
}
