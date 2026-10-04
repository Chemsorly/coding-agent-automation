namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Accumulated tokens, cost, session count, and agent execution time for a single pipeline phase.
/// </summary>
/// <param name="Tokens">Total LLM tokens consumed in this phase.</param>
/// <param name="Cost">Total LLM cost in USD for this phase, or null if unavailable.</param>
/// <param name="SessionCount">Number of agent CLI invocations (sessions) in this phase.</param>
/// <param name="AgentTimeSeconds">Total agent execution time in seconds for this phase.</param>
/// <param name="Provider">Provider name tag (e.g. "kiro", "opencode"). Null means unknown.</param>
/// <param name="Model">Model name (e.g. "claude-sonnet-4-5"). Null means unknown.</param>
public sealed record PhaseUsage(
    long Tokens,
    decimal? Cost,
    int SessionCount = 0,
    double AgentTimeSeconds = 0.0,
    string? Provider = null,
    string? Model = null)
{
    /// <summary>Input tokens (excluding cache reads and writes) consumed in this phase.</summary>
    public long InputTokens { get; init; }

    /// <summary>Output tokens in this phase, excluding <see cref="ReasoningTokens"/>.</summary>
    public long OutputTokens { get; init; }

    /// <summary>Reasoning (thinking) tokens in this phase.</summary>
    public long ReasoningTokens { get; init; }

    /// <summary>Tokens read from the prompt cache in this phase.</summary>
    public long CacheReadTokens { get; init; }

    /// <summary>Tokens written to the prompt cache in this phase.</summary>
    public long CacheWriteTokens { get; init; }

    /// <summary>Agent turns (model round trips) in this phase, when the provider reports them.</summary>
    public int Turns { get; init; }

    /// <summary>Web search requests the model made in this phase, when the provider reports them.</summary>
    public int WebSearchRequests { get; init; }

    /// <summary>
    /// How the phase's LLM calls were paid for (<see cref="AgentBillingModes"/>), or null if unknown.
    /// </summary>
    public string? BillingMode { get; init; }
}
