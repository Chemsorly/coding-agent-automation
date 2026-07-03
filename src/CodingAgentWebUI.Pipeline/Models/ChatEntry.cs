namespace CodingAgentWebUI.Pipeline.Models;

public sealed class ChatEntry
{
    public required ChatRole Role { get; init; }
    public required string Content { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}
