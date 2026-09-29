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
    string? Model = null);
