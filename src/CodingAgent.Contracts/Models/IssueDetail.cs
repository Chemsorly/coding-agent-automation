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
    // TODO [WARNING]: Silent MessagePack key collision risk. IssueDetail now has consecutive keys
    // 0–6. If a future branch adds a field at Key(6) (or Key(5) colliding with Url), the wire
    // format resolves the duplicate by last-writer-wins with no compile-time error. In a distributed
    // deployment where the Scheduler and API hosts can run different build versions simultaneously,
    // an older consumer that does not know about Key(6) will deserialise CreatedAt as null, causing
    // FIFO ordering to fall back to "sort last" for all issues from that provider. Always increment
    // the key sequentially and ensure all keys are unique across branches before merging.
    // A missing key 6 in wire data produced by an older node deserializes safely as null (the
    // intended default for DateTime?).
    [Key(6)]
    public DateTime? CreatedAt { get; init; }
}
