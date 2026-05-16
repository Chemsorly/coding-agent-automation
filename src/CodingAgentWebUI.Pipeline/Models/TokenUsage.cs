namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Token usage for a single agent invocation (delta, not cumulative).
/// </summary>
public sealed record TokenUsage
{
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long ReasoningTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public long TotalTokens => InputTokens + OutputTokens + ReasoningTokens;
}
