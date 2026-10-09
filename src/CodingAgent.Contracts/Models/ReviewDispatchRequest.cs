namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Groups all parameters needed to dispatch a PR review job.
/// </summary>
public sealed record ReviewDispatchRequest
{
    public required string PrIdentifier { get; init; }

    /// <summary>
    /// The PR/MR number on the repository provider (GitHub PR number, GitLab MR iid). Carried into
    /// <see cref="LinkedPullRequest.Number"/> so the review run's PR-state guard queries the real PR.
    /// </summary>
    public required int PrNumber { get; init; }

    public required string PrBranchName { get; init; }
    public required string PrTitle { get; init; }
    public string? PrDescription { get; init; }
    public string? PrAuthor { get; init; }
    public required string PrUrl { get; init; }
    public required string PrTargetBranch { get; init; }
    public required ProviderConfigId IssueProviderId { get; init; }
    public required ProviderConfigId RepoProviderId { get; init; }
    public string? BrainProviderId { get; init; }
    public required string InitiatedBy { get; init; }
}
