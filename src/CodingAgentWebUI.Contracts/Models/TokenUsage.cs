using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Token usage for a single agent invocation (delta, not cumulative).
/// </summary>
[MessagePackObject]
public sealed record TokenUsage
{
    [Key(0)]
    public long InputTokens { get; init; }

    [Key(1)]
    public long OutputTokens { get; init; }

    [Key(2)]
    public long ReasoningTokens { get; init; }

    [Key(3)]
    public long CacheReadTokens { get; init; }

    [Key(4)]
    public long CacheWriteTokens { get; init; }

    [IgnoreMember]
    public long TotalTokens => InputTokens + OutputTokens + ReasoningTokens;
}
