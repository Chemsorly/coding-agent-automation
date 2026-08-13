using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests that IRepositoryProvider default interface implementations for the
/// housekeeping methods behave correctly (spec 040, task 1.4).
/// </summary>
public class HousekeepingInterfaceDefaultTests
{
    // Minimal concrete implementation that uses only the default interface members
    private sealed class DefaultOnlyProvider : IRepositoryProvider
    {
        public RepositoryProviderType ProviderType => RepositoryProviderType.GitHub;
        public string BaseBranch => "main";
        public string RepositoryFullName => "owner/repo";

        public Task CloneAsync(WorkspacePath workspacePath, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CreateBranchAsync(WorkspacePath workspacePath, string branchName, CancellationToken ct) => Task.FromResult(string.Empty);
        public Task<IReadOnlyList<string>> CommitAllAsync(WorkspacePath workspacePath, string message, IReadOnlyList<string>? blacklistedPaths, CancellationToken ct, IReadOnlyList<string>? pipelineInjectedPaths = null) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task<IReadOnlyList<string>> CommitAllAsync(WorkspacePath workspacePath, string message, IReadOnlyList<string>? blacklistedPaths, bool allowEmpty, CancellationToken ct, IReadOnlyList<string>? pipelineInjectedPaths = null) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        public Task PushBranchAsync(WorkspacePath workspacePath, string branchName, CancellationToken ct) => Task.CompletedTask;
        public Task<string> CreatePullRequestAsync(PullRequestInfo prInfo, CancellationToken ct) => Task.FromResult(string.Empty);
        public Task<string> GetHeadCommitShaAsync(WorkspacePath workspacePath, CancellationToken ct) => Task.FromResult(string.Empty);
        public Task<bool> HasCommitsAheadAsync(WorkspacePath workspacePath, CancellationToken ct) => Task.FromResult(false);
        public Task<IReadOnlyList<FileChangeSummary>> GetFileChangesAsync(WorkspacePath workspacePath, CancellationToken ct) => Task.FromResult<IReadOnlyList<FileChangeSummary>>(Array.Empty<FileChangeSummary>());
        public Task ValidateAsync(CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void SupportsServerSideBranchUpdate_Default_ReturnsFalse()
    {
        IRepositoryProvider provider = new DefaultOnlyProvider();
        Assert.False(provider.SupportsServerSideBranchUpdate);
    }

    [Fact]
    public async Task IsPullRequestBehindBaseAsync_Default_ReturnsUnknown()
    {
        IRepositoryProvider provider = new DefaultOnlyProvider();
        var result = await provider.IsPullRequestBehindBaseAsync(42, CancellationToken.None);
        Assert.Equal(PrMergeabilityStatus.Unknown, result);
    }

    [Fact]
    public async Task UpdatePullRequestBranchAsync_Default_ThrowsNotSupportedException()
    {
        IRepositoryProvider provider = new DefaultOnlyProvider();
        await Assert.ThrowsAsync<NotSupportedException>(
            () => provider.UpdatePullRequestBranchAsync(42, CancellationToken.None));
    }
}
