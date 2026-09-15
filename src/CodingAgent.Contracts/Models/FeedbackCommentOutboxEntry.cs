namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Represents a feedback comment that needs to be posted to an issue after a pipeline run.
/// Stored in the durable outbox table so it survives graceful pod shutdown.
/// </summary>
public sealed record FeedbackCommentOutboxEntry
{
    public Guid Id { get; init; }

    /// <summary>
    /// Unique idempotency key — one row per pipeline run.
    /// Maps to the unique index on the FeedbackCommentOutbox table.
    /// </summary>
    public required string RunId { get; init; }

    /// <summary>Issue provider config ID used to post the comment.</summary>
    public required string IssueProviderConfigId { get; init; }

    /// <summary>Issue identifier on the provider (e.g. "GH-42").</summary>
    public required string IssueIdentifier { get; init; }

    /// <summary>
    /// Repository provider config ID used for the optional PR-body feedback-link append.
    /// </summary>
    public required string RepoProviderConfigId { get; init; }

    /// <summary>PR number (e.g. "47"), or null if this run did not create a PR.</summary>
    public string? PullRequestNumber { get; init; }

    /// <summary>
    /// JSON-serialized <see cref="CodingAgent.Pipeline.Models.IssueFeedback"/>.
    /// Stored so the relay can reconstitute and format the comment body without
    /// the in-memory PipelineRun.
    /// </summary>
    public required string FeedbackJson { get; init; }

    /// <summary>Pending | Completed | Failed</summary>
    public string Status { get; init; } = "Pending";

    public int AttemptCount { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? ErrorMessage { get; init; }
}
