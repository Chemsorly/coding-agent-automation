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

    /// <summary>Seed PRs for ListOpenPullRequestsAsync.</summary>
    public List<PullRequestSummary> PullRequests { get; } = new();

    /// <summary>Tracks label changes for assertion (action, prNumber, label).</summary>
    public List<(string Action, int PrNumber, string Label)> PrLabelChanges { get; } = new();

    /// <summary>Tracks submitted reviews for assertion.</summary>
    public List<(int PrNumber, string Body, PullRequestReviewType Type)> PostedReviews { get; } = new();

    public void Reset()
    {
        MethodCalls.Clear();
        LastCreatedPrUrl = null;
        LastBranchName = null;
        ShouldFail = false;
        PullRequests.Clear();
        PrLabelChanges.Clear();
        PostedReviews.Clear();
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

    public Task UpdatePullRequestAsync(int pullRequestNumber, string body, bool markReady, CancellationToken ct) =>
        Task.CompletedTask;

    // TODO: This implementation ignores the labels parameter. If future tests rely on label-filtered
    // PR listing (e.g., pipeline loop filtering by "agent:next"), add filtering logic here.
    public Task<PagedResult<PullRequestSummary>> ListOpenPullRequestsAsync(
        int page, int pageSize, IReadOnlyList<string>? labels, CancellationToken ct)
    {
        var items = PullRequests.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return Task.FromResult(new PagedResult<PullRequestSummary>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            HasMore = PullRequests.Count > page * pageSize
        });
    }

    public Task AddPrLabelAsync(int prNumber, string label, CancellationToken ct)
    {
        PrLabelChanges.Add(("Add", prNumber, label));
        return Task.CompletedTask;
    }

    public Task RemovePrLabelAsync(int prNumber, string label, CancellationToken ct)
    {
        PrLabelChanges.Add(("Remove", prNumber, label));
        return Task.CompletedTask;
    }

    public Task SubmitPullRequestReviewAsync(
        int prNumber, string body, PullRequestReviewType type, CancellationToken ct)
    {
        PostedReviews.Add((prNumber, body, type));
        return Task.CompletedTask;
    }

    public Task SubmitPullRequestReviewAsync(int prNumber, ReviewSubmission submission, CancellationToken ct)
    {
        PostedReviews.Add((prNumber, submission.Body, submission.Type));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
