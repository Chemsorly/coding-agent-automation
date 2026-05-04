namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Configuration for retry behavior and timeouts.
/// </summary>
public sealed record RetryConfiguration
{
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Maximum number of retry attempts for the analysis phase.
    /// Default 2 = 3 total attempts (initial + 2 retries).
    /// Set to 0 to disable retry (fail on first failure).
    /// </summary>
    public int MaxAnalysisRetries { get; init; } = 2;

    public TimeSpan AgentTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long the agent can be silent (no output) before the stall monitor logs a warning.
    /// </summary>
    public TimeSpan StallWarningInterval { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How often the stall monitor polls agent health status.
    /// </summary>
    public TimeSpan StallPollInterval { get; init; } = TimeSpan.FromSeconds(30);
}
