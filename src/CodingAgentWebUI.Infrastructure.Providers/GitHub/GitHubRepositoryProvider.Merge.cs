using CodingAgentWebUI.Infrastructure.Git;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Infrastructure.GitHub;

[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification = "Thin delegation to RepositoryGitOperations — covered by integration tests.")]
public partial class GitHubRepositoryProvider
{
    public Task<MergeResult> MergeFromBaseAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);

        return Task.Run(async () =>
        {
            var token = await GetTokenAsync(ct);
            return await RepositoryGitOperations.MergeFromBase(workspacePath, _baseBranch, GitConstants.TokenUsername, token, _gitPipeline, ct);
        }, ct);
    }
}
