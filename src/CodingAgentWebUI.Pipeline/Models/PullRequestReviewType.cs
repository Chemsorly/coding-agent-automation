namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// The type of PR review to submit.
/// </summary>
public enum PullRequestReviewType
{
    /// <summary>Neutral, informational review (does not block merge, not dismissible).</summary>
    Comment,

    /// <summary>Requests changes (blocks merge on GitHub, dismissible).</summary>
    RequestChanges,

    /// <summary>Approves the PR (does not block merge, dismissible).</summary>
    Approve
}
