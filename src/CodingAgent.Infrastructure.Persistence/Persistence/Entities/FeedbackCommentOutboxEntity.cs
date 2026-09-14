namespace CodingAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// Persistent outbox row for a feedback comment that needs to be delivered after a pipeline run.
/// Written before the ApplicationStopping-linked CancellationTokenSource fires, so it survives
/// graceful pod shutdown. The relay (FeedbackCommentRelayService in CodingAgent.Scheduler)
/// drains this table on every sweep.
/// </summary>
public class FeedbackCommentOutboxEntity
{
    public Guid Id { get; set; }

    /// <summary>Unique per-run. Unique index enforces one row per run (ON CONFLICT DO NOTHING).</summary>
    public required string RunId { get; set; }

    public required string IssueProviderConfigId { get; set; }
    public required string IssueIdentifier { get; set; }

    /// <summary>Used for the optional PR-body feedback-link append.</summary>
    public required string RepoProviderConfigId { get; set; }

    public string? PullRequestNumber { get; set; }

    /// <summary>JSON-serialized IssueFeedback.</summary>
    public required string FeedbackJson { get; set; }

    /// <summary>Pending | Completed | Failed</summary>
    public string Status { get; set; } = "Pending";

    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>Concurrency token mapped to PostgreSQL xmin system column.</summary>
    public uint RowVersion { get; set; }
}
