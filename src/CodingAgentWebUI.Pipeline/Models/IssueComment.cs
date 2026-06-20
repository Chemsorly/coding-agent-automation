using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

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
}
