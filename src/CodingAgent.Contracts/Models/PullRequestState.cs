namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Represents the open/merged/closed state of a pull request or merge request.
/// Returned by <see cref="Interfaces.IPullRequestProvider.GetPullRequestStateAsync"/>.
/// </summary>
public enum PullRequestState
{
    /// <summary>The pull request is open and active.</summary>
    Open,

    /// <summary>The pull request was merged into the base branch.</summary>
    Merged,

    /// <summary>The pull request was closed without merging.</summary>
    Closed
}
