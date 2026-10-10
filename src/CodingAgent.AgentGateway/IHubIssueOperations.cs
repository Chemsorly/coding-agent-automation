using CodingAgent.Pipeline.Models;

namespace CodingAgent.AgentGateway;

/// <summary>
/// Shared issue operations used by both the AgentHub (for agent-initiated requests like
/// RequestLabelChange, RequestPostComment) and the <see cref="IAgentJobLifecycleService"/>
/// (for post-completion label swaps and feedback comments).
/// </summary>
public interface IHubIssueOperations
{
    /// <summary>
    /// Swaps the agent label on the entity (issue or PR) using the appropriate provider.
    /// Routes based on <see cref="PipelineRun.LabelTargetKind"/>: Issue → IssueProviderConfigId, PullRequest → RepoProviderConfigId.
    /// </summary>
    Task SwapLabelAsync(PipelineRun run, string newLabel, CancellationToken ct = default);

    /// <summary>
    /// Posts a comment on the issue using the issue provider from the run's config.
    /// Returns the comment URL if available. Non-fatal: returns null on failure.
    /// </summary>
    Task<string?> PostCommentViaIssueProviderAsync(PipelineRun run, string body, CancellationToken ct = default);

    /// <summary>
    /// Posts issue-level feedback as a comment on the issue if present.
    /// If a PR exists, appends a link to the feedback comment in the PR body.
    /// Returns <c>true</c> when the comment was posted or there was no feedback to post, and
    /// <c>false</c> when posting failed (logged as a warning), so the caller can leave the durable
    /// outbox row for the relay. Throws <see cref="OperationCanceledException"/> when
    /// <paramref name="ct"/> is cancelled before the comment is posted. A failed PR-body append
    /// does not change the result.
    /// </summary>
    Task<bool> PostIssueFeedbackCommentAsync(PipelineRun run, CancellationToken ct = default);
}
