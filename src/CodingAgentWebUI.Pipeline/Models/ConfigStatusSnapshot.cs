namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Immutable snapshot of a <see cref="PipelineJobTemplate"/>'s runtime polling status.
/// Swapped atomically into a ConcurrentDictionary — safe for UI reads without locking.
/// </summary>
public sealed record ConfigStatusSnapshot
{
    /// <summary>Timestamp of the most recent poll attempt for this template.</summary>
    public DateTimeOffset? LastPollTime { get; init; }

    /// <summary>Number of agent:next issues found in the last successful poll.</summary>
    public int LastPollIssueCount { get; init; }

    /// <summary>Error message from the last failed poll, or null if last poll succeeded.</summary>
    public string? LastError { get; init; }

    /// <summary>Number of consecutive poll failures. Reset to 0 on success.</summary>
    public int ConsecutiveFailures { get; init; }

    /// <summary>Rate limit reset time, if the last failure was a rate limit. Null otherwise.</summary>
    public DateTimeOffset? RateLimitResetAt { get; init; }

    /// <summary>Whether this template is currently being polled in the active cycle.</summary>
    public bool IsCurrentlyPolling { get; init; }

    /// <summary>Creates a default (empty) status snapshot.</summary>
    public static ConfigStatusSnapshot Empty => new();
}
