using MessagePack;

namespace CodingAgent.Pipeline.Models;

[MessagePackObject]
public sealed class ChatEntry
{
    [Key(0)]
    public required ChatRole Role { get; init; }

    [Key(1)]
    public required string Content { get; init; }

    [Key(2)]
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
