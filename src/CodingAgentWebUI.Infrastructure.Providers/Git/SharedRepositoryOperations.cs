using System.Diagnostics.CodeAnalysis;
using Polly;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Infrastructure.Git;

/// <summary>
/// Static helpers that both <c>GitHubRepositoryProvider</c> and <c>GitLabRepositoryProvider</c>
/// delegate to for token-free local git operations. Centralises the shared implementation so
/// that Sonar CPD sees a single definition rather than two identical copies.
/// </summary>
internal static class SharedRepositoryOperations
{
    internal static Task CommitAllAsync(WorkspacePath workspacePath, string message, CancellationToken ct)
        => CommitAllAsync(workspacePath, message, null, ct);

    internal static Task<IReadOnlyList<string>> CommitAllAsync(WorkspacePath workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, CancellationToken ct,
        IReadOnlyList<string>? pipelineInjectedPaths = null)
        => CommitAllAsync(workspacePath, message, blacklistedPaths, allowEmpty: false, ct, pipelineInjectedPaths);

    internal static Task<IReadOnlyList<string>> CommitAllAsync(WorkspacePath workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, bool allowEmpty, CancellationToken ct,
        IReadOnlyList<string>? pipelineInjectedPaths = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);
        ArgumentNullException.ThrowIfNull(message);

        return Task.Run(() => RepositoryGitOperations.CommitAll(workspacePath, message, blacklistedPaths, allowEmpty, pipelineInjectedPaths), ct);
    }

    [ExcludeFromCodeCoverage]
    internal static Task<string> GetHeadCommitShaAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);
        return Task.Run(() => RepositoryGitOperations.GetHeadCommitSha(workspacePath), ct);
    }

    [ExcludeFromCodeCoverage]
    internal static async Task<bool> HasCommitsAheadAsync(WorkspacePath workspacePath, string baseBranch, ResiliencePipeline gitPipeline, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);
        return await RepositoryGitOperations.HasCommitsAhead(workspacePath, baseBranch, gitPipeline, ct);
    }

    [ExcludeFromCodeCoverage]
    internal static Task<IReadOnlyList<FileChangeSummary>> GetFileChangesAsync(WorkspacePath workspacePath, string baseBranch, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value);
        return Task.Run(() => RepositoryGitOperations.GetFileChanges(workspacePath, baseBranch), ct);
    }
}
