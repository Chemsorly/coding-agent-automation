namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Thrown when the GitHub API returns a rate limit exceeded response (403 with X-RateLimit-Remaining: 0).
/// Contains the reset timestamp so callers can wait until the limit resets.
/// </summary>
public sealed class RateLimitExceededException : Exception
{
    /// <summary>When the rate limit resets.</summary>
    public DateTimeOffset ResetAt { get; }

    public RateLimitExceededException() : base("GitHub API rate limit exceeded.") { }

    public RateLimitExceededException(string message) : base(message) { }

    public RateLimitExceededException(string message, Exception? innerException) : base(message, innerException) { }

    public RateLimitExceededException(DateTimeOffset resetAt, Exception? innerException = null)
        : base($"GitHub API rate limit exceeded. Resets at {resetAt:u}.", innerException)
    {
        ResetAt = resetAt;
    }
}
