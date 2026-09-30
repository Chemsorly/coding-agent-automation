using MessagePack;

namespace CodingAgent.Pipeline.Models;

[MessagePackObject]
public sealed class IssueDetail
{
    [Key(0)]
    public required string Description { get; init; }

    [Key(1)]
    public required string Identifier { get; init; }

    [Key(2)]
    public required IReadOnlyList<string> Labels { get; init; }

    [Key(3)]
    public required string Title { get; init; }

    [Key(4)]
    public IReadOnlyList<ImageReference> Images { get; init; } = [];

    /// <summary>Web URL of the issue on the provider (e.g. GitHub HtmlUrl / GitLab WebUrl), or null if unknown.</summary>
    [Key(5)]
    public string? Url { get; init; }

    /// <summary>Issue creation date, used for FIFO ordering in the pipeline loop.</summary>
    // NOTE: IssueDetail now has consecutive MessagePack keys 0–6. If a future field is
    // added at key 5 on a different branch and later merged, the numbering will collide silently
    // (MessagePack resolves duplicate keys by last-writer-wins, with no compile-time error).
    // A missing key 6 in wire data produced by an older node (pre-CreatedAt) deserializes safely
    // as null, which is the intended default for DateTime?. Structural observation only — no
    // current bug — but worth tracking to preserve serialization stability across branches.
    [Key(6)]
    public DateTime? CreatedAt { get; init; }
}
