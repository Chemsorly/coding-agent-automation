namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Accumulated tokens and cost for a single pipeline phase.
/// </summary>
public sealed record PhaseUsage(long Tokens, decimal? Cost);
