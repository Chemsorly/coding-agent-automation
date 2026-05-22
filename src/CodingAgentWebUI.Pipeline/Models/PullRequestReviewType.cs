namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// The type of PR review to submit.
/// </summary>
public enum PullRequestReviewType
{
    /// <summary>Neutral, informational review (does not block merge).</summary>
    Comment,

    /// <summary>Requests changes (blocks merge on GitHub).</summary>
    RequestChanges
}
