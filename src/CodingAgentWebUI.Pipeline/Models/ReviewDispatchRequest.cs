namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Groups all parameters needed to dispatch a PR review job.
/// Replaces the 10-parameter signature on <see cref="Interfaces.IJobDispatcher.TryDispatchReviewAsync"/>.
/// </summary>
public sealed record ReviewDispatchRequest
{
    public required string PrIdentifier { get; init; }
    public required string PrBranchName { get; init; }
    public required string PrTitle { get; init; }
    public string? PrDescription { get; init; }
    public string? PrAuthor { get; init; }
    public required string PrUrl { get; init; }
    public required string PrTargetBranch { get; init; }
    public required string IssueProviderId { get; init; }
    public required string RepoProviderId { get; init; }
    public string? BrainProviderId { get; init; }
    public required string InitiatedBy { get; init; }
}
