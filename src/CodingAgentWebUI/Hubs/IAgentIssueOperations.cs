using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Hubs;

/// <summary>
/// Shared issue operations used by both the AgentHub (for agent-initiated requests like
/// RequestLabelChange, RequestPostComment) and the <see cref="IAgentJobLifecycleService"/>
/// (for post-completion label swaps and feedback comments).
/// </summary>
public interface IHubIssueOperations
{
    /// <summary>
    /// Swaps the agent label on the entity (issue or PR) using the appropriate provider.
    /// Routes based on <paramref name="targetKind"/>: Issue → IssueProviderConfigId, PullRequest → RepoProviderConfigId.
    /// </summary>
    Task SwapLabelAsync(PipelineRun run, string newLabel, LabelTargetKind targetKind);

    /// <summary>
    /// Posts a comment on the issue using the issue provider from the run's config.
    /// Returns the comment URL if available. Non-fatal: returns null on failure.
    /// </summary>
    Task<string?> PostCommentViaIssueProviderAsync(PipelineRun run, string body);

    /// <summary>
    /// Posts issue-level feedback as a comment on the issue if present.
    /// If a PR exists, appends a link to the feedback comment in the PR body.
    /// Non-fatal: logs warning on failure and continues.
    /// </summary>
    Task PostIssueFeedbackCommentAsync(PipelineRun run);
}
