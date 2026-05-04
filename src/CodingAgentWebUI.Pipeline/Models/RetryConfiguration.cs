namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Configuration for retry behavior, agent timeouts, and stall detection.
/// </summary>
public sealed record RetryConfiguration
{
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Maximum number of retry attempts for the analysis phase.
    /// Default 1 = 2 total attempts (initial + 1 retry).
    /// Set to 0 to disable retry (fail on first failure).
    /// </summary>
    public int MaxAnalysisRetries { get; init; } = 2;

    public TimeSpan AgentTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long the agent can be silent (no output) before the stall monitor logs a warning.
    /// The warning resets after each occurrence so it fires again after another interval of silence.
    /// </summary>
    public TimeSpan StallWarningInterval { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How often the stall monitor polls <see cref="IAgentProvider.GetHealthStatus"/>.
    /// Default is 30 seconds. Tests can set a shorter interval for faster execution.
    /// </summary>
    public TimeSpan StallPollInterval { get; init; } = TimeSpan.FromSeconds(30);
}
