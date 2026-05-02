using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.E2ETests.Fakes;

/// <summary>
/// In-memory repository provider for E2E tests. Creates real temp directories for workspaces.
/// </summary>
public sealed class InMemoryRepositoryProvider : IRepositoryProvider
{
    public RepositoryProviderType ProviderType => RepositoryProviderType.GitHub;
    public string BaseBranch => "main";
    public string RepositoryFullName => "e2e-org/e2e-repo";

    public List<string> MethodCalls { get; } = new();
    public string? LastCreatedPrUrl { get; private set; }
    public string? LastBranchName { get; private set; }
    public bool ShouldFail { get; set; }

    public void Reset()
    {
        MethodCalls.Clear();
        LastCreatedPrUrl = null;
        LastBranchName = null;
        ShouldFail = false;
    }

    public Task CloneAsync(string workspacePath, CancellationToken ct)
    {
        MethodCalls.Add(nameof(CloneAsync));
        if (ShouldFail) throw new InvalidOperationException("Fake clone failure");
        Directory.CreateDirectory(workspacePath);
        return Task.CompletedTask;
    }

    public Task<string> CreateBranchAsync(string workspacePath, string branchName, CancellationToken ct)
    {
        MethodCalls.Add(nameof(CreateBranchAsync));
        LastBranchName = branchName;
        return Task.FromResult(branchName);
    }

    public Task<IReadOnlyList<string>> CommitAllAsync(string workspacePath, string message, IReadOnlyList<string>? blacklistedPaths, CancellationToken ct)
    {
        MethodCalls.Add(nameof(CommitAllAsync));
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    public Task<IReadOnlyList<string>> CommitAllAsync(string workspacePath, string message, IReadOnlyList<string>? blacklistedPaths, bool allowEmpty, CancellationToken ct)
    {
        MethodCalls.Add(nameof(CommitAllAsync));
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    public Task CommitAllAsync(string workspacePath, string message, CancellationToken ct)
    {
        MethodCalls.Add(nameof(CommitAllAsync));
        return Task.CompletedTask;
    }

    public Task PushBranchAsync(string workspacePath, string branchName, CancellationToken ct)
    {
        MethodCalls.Add(nameof(PushBranchAsync));
        return Task.CompletedTask;
    }

    public Task<string> CreatePullRequestAsync(PullRequestInfo prInfo, CancellationToken ct)
    {
        MethodCalls.Add(nameof(CreatePullRequestAsync));
        LastCreatedPrUrl = $"https://github.com/e2e-org/e2e-repo/pull/1";
        return Task.FromResult(LastCreatedPrUrl);
    }

    public Task<string> GetHeadCommitShaAsync(string workspacePath, CancellationToken ct) =>
        Task.FromResult("abc123def456");

    public Task<bool> HasCommitsAheadAsync(string workspacePath, CancellationToken ct) =>
        Task.FromResult(true);

    public Task<IReadOnlyList<FileChangeSummary>> GetFileChangesAsync(string workspacePath, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FileChangeSummary>>(Array.Empty<FileChangeSummary>());

    public Task ValidateAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<LinkedPullRequest>> GetAgentPullRequestsAsync(string issueIdentifier, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LinkedPullRequest>>(Array.Empty<LinkedPullRequest>());

    public Task CheckoutRemoteBranchAsync(string workspacePath, string branchName, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<MergeResult> MergeFromBaseAsync(string workspacePath, CancellationToken ct) =>
        Task.FromResult(new MergeResult { Success = true, HasConflicts = false, ConflictFiles = Array.Empty<string>() });

    public Task PullAsync(string workspacePath, CancellationToken ct) => Task.CompletedTask;

    public Task UpdatePullRequestAsync(int pullRequestNumber, string body, CancellationToken ct) =>
        Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
