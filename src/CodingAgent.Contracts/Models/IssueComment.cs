using MessagePack;

namespace CodingAgent.Pipeline.Models;

[MessagePackObject]
public sealed class IssueComment
{
    [Key(0)]
    public required string Author { get; init; }

    [Key(1)]
    public required string Body { get; init; }

    [Key(2)]
    public required DateTime CreatedAt { get; init; }

    [Key(3)]
    public required string Id { get; init; }

    /// <summary>
    /// Whether the author is on the repository. Set only by the provider mapping. True: verified.
    /// False: the provider was asked and the author is not on the repository. Null: the sender predates
    /// this field (an older API, or a test fake). Treat null as before this field existed, so that a new
    /// agent image on an older API keeps every comment it relies on.
    /// </summary>
    [Key(4)]
    public bool? IsVerified { get; init; }
}
