namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Configuration for the closed-loop autonomous polling mode.
/// </summary>
public sealed record ClosedLoopConfiguration
{
    /// <summary>
    /// Poll interval for the closed pipeline loop when checking for new agent:next issues.
    /// Default: 60 seconds.
    /// </summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum number of issues to process per poll cycle in the closed loop.
    /// 0 means unlimited (process entire backlog). Counter resets each poll cycle.
    /// </summary>
    public int MaxRunsPerCycle { get; init; } = 0;

    /// <summary>
    /// Number of consecutive poll failures before the circuit breaker pauses the loop.
    /// Default: 5.
    /// </summary>
    public int MaxConsecutivePollFailures
    {
        get => _maxConsecutivePollFailures;
        init => _maxConsecutivePollFailures = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxConsecutivePollFailures), value, "Value must be at least 1.");
    }
    private readonly int _maxConsecutivePollFailures = 5;

    /// <summary>
    /// Maximum backoff interval between poll retries after consecutive failures.
    /// Backoff uses exponential formula capped at this value. Default: 15 minutes.
    /// </summary>
    public TimeSpan MaxBackoffInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Maximum number of pages to fetch when polling for agent:next issues.
    /// Each page contains up to 100 issues. Default: 10 (1000 issues max).
    /// </summary>
    public int MaxPagesToFetch
    {
        get => _maxPagesToFetch;
        init => _maxPagesToFetch = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(MaxPagesToFetch), value, "Value must be at least 1.");
    }
    private readonly int _maxPagesToFetch = 10;
}
