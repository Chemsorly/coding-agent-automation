namespace KiroWebUI.Pipeline.Models;

public sealed class PullRequestInfo
{
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required string BranchName { get; init; }
    public required string BaseBranch { get; init; }
    public bool IsDraft { get; init; }
}
