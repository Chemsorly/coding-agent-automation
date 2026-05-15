namespace CodingAgentWebUI.Pipeline.Models;

public sealed class AgentResult
{
    public required int ExitCode { get; init; }
    public required IReadOnlyList<string> OutputLines { get; init; }
    public bool Success => ExitCode == 0;

    /// <summary>Token usage delta for this specific invocation, or null if unavailable.</summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>Cost in USD for this invocation, or null if unavailable/unknown.</summary>
    public decimal? Cost { get; init; }
}
