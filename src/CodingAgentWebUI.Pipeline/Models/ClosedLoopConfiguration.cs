namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Configuration for closed-loop autonomous polling.
/// </summary>
public sealed record ClosedLoopConfiguration
{
    /// <summary>
    /// Poll interval when checking for new agent:next issues. Default: 60 seconds.
    /// </summary>
    public TimeSpan ClosedLoopPollInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum number of issues to process per poll cycle. 0 means unlimited.
    /// </summary>
    public int ClosedLoopMaxRunsPerCycle { get; init; } = 0;

    /// <summary>
    /// Number of consecutive poll failures before the circuit breaker pauses the loop.
    /// </summary>
    public int ClosedLoopMaxConsecutivePollFailures
    {
        get => _closedLoopMaxConsecutivePollFailures;
        init => _closedLoopMaxConsecutivePollFailures = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(ClosedLoopMaxConsecutivePollFailures), value, "Value must be at least 1.");
    }
    private readonly int _closedLoopMaxConsecutivePollFailures = 5;

    /// <summary>
    /// Maximum backoff interval between poll retries after consecutive failures. Default: 15 minutes.
    /// </summary>
    public TimeSpan ClosedLoopMaxBackoffInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Maximum number of pages to fetch when polling for agent:next issues. Default: 10 (1000 issues max).
    /// </summary>
    public int ClosedLoopMaxPagesToFetch
    {
        get => _closedLoopMaxPagesToFetch;
        init => _closedLoopMaxPagesToFetch = value >= 1
            ? value
            : throw new ArgumentOutOfRangeException(nameof(ClosedLoopMaxPagesToFetch), value, "Value must be at least 1.");
    }
    private readonly int _closedLoopMaxPagesToFetch = 10;
}
