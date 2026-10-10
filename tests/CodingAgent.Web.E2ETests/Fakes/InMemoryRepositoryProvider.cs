using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.E2ETests.Fakes;

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

    // ── IPullRequestHousekeepingProvider support ──────────────────────────

    /// <summary>
    /// Overrides the default <c>false</c> — the loop guard in
    /// <c>PipelineLoopService.RunHousekeepingAsync</c> checks this before calling
    /// <c>HousekeepingService.ExecuteAsync</c>, so it must be <c>true</c> for E2E tests
    /// that exercise the full loop path (scenario 5).
    /// </summary>
    public bool SupportsServerSideBranchUpdate => true;

    /// <summary>
    /// Per-PR mergeability status. Keyed by PR number.
    /// Tests set entries before calling <c>HousekeepingService.ExecuteAsync</c>.
    /// PRs not in the dictionary return <see cref="PrMergeabilityStatus.Unknown"/>.
    /// </summary>
    public Dictionary<int, PrMergeabilityStatus> PrMergeability { get; } = new();

    /// <summary>
    /// Per-PR linked issue identifiers. Keyed by PR number.
    /// Tests set entries to control what <c>IssueReworkService</c> sees when it calls
    /// <c>ExtractLinkedIssuesAsync</c> for a conflicted PR.
    /// PRs not in the dictionary return an empty list.
    /// </summary>
    public Dictionary<int, List<string>> PrLinkedIssues { get; } = new();

    /// <summary>
    /// Agent branch names returned by <c>ListAgentBranchesAsync</c>.
    /// Tests populate this list before calling <c>HousekeepingService.ExecuteAsync</c>.
    /// All names must use the <c>feature/auto-{issueId}-{slug}</c> prefix so
    /// <c>StaleBranchCleaner.ExtractIssueId</c> can parse them.
    /// </summary>
    public List<string> AgentBranches { get; } = new();

    /// <summary>
    /// Branch names deleted by <c>DeleteBranchAsync</c>. Accumulates across calls.
    /// Tests assert on this list after calling <c>HousekeepingService.ExecuteAsync</c>.
    /// </summary>
    public List<string> DeletedBranches { get; } = new();

    /// <summary>
    /// PR numbers for which <c>UpdatePullRequestBranchAsync</c> was called.
    /// Accumulates across calls.
    /// <para>
    /// IMPORTANT: <c>HousekeepingService.FireAndForget</c> must be overridden to
    /// <c>task => task</c> before asserting on this list — the production default discards
    /// the update task, so assertions would be non-deterministic without the override.
    /// </para>
    /// </summary>
    public List<int> BranchUpdateCalls { get; } = new();

    /// <summary>
    /// Per-PR merged state returned by <c>GetPullRequestMergedStateAsync</c>.
    /// Keyed by PR number. <c>true</c> = merged, <c>false</c> = closed-unmerged, <c>null</c> = unknown.
    /// PRs not in the dictionary return <c>null</c>.
    /// </summary>
    public Dictionary<int, bool?> PrMergedState { get; } = new();

    public void Reset()
    {
        MethodCalls.Clear();
        LastCreatedPrUrl = null;
        LastBranchName = null;
        ShouldFail = false;
        PullRequests.Clear();
        PrLabelChanges.Clear();
        PostedReviews.Clear();
        // Housekeeping collections
        PrMergeability.Clear();
        PrLinkedIssues.Clear();
        AgentBranches.Clear();
        DeletedBranches.Clear();
        BranchUpdateCalls.Clear();
        PrMergedState.Clear();
    }

    public Task CloneAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        MethodCalls.Add(nameof(CloneAsync));
        if (ShouldFail) throw new InvalidOperationException("Fake clone failure");
        Directory.CreateDirectory(workspacePath);
        return Task.CompletedTask;
    }

    public Task<string> CreateBranchAsync(WorkspacePath workspacePath, BranchName branchName, CancellationToken ct)
    {
        MethodCalls.Add(nameof(CreateBranchAsync));
        LastBranchName = branchName.Value;
        return Task.FromResult(branchName.Value);
    }

    public Task<IReadOnlyList<string>> CommitAllAsync(WorkspacePath workspacePath, string message, IReadOnlyList<string>? blacklistedPaths, CancellationToken ct, IReadOnlyList<string>? pipelineInjectedPaths = null)
    {
        MethodCalls.Add(nameof(CommitAllAsync));
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    public Task<IReadOnlyList<string>> CommitAllAsync(WorkspacePath workspacePath, string message, IReadOnlyList<string>? blacklistedPaths, bool allowEmpty, CancellationToken ct, IReadOnlyList<string>? pipelineInjectedPaths = null)
    {
        MethodCalls.Add(nameof(CommitAllAsync));
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    public Task CommitAllAsync(WorkspacePath workspacePath, string message, CancellationToken ct)
    {
        MethodCalls.Add(nameof(CommitAllAsync));
        return Task.CompletedTask;
    }

    public Task PushBranchAsync(WorkspacePath workspacePath, BranchName branchName, CancellationToken ct)
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

    public Task<string> GetHeadCommitShaAsync(WorkspacePath workspacePath, CancellationToken ct) =>
        Task.FromResult("abc123def456");

    public Task<bool> HasCommitsAheadAsync(WorkspacePath workspacePath, CancellationToken ct) =>
        Task.FromResult(true);

    public Task<IReadOnlyList<FileChangeSummary>> GetFileChangesAsync(WorkspacePath workspacePath, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<FileChangeSummary>>(Array.Empty<FileChangeSummary>());

    public Task ValidateAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<IReadOnlyList<LinkedPullRequest>> GetAgentPullRequestsAsync(IssueIdentifier issueIdentifier, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<LinkedPullRequest>>(Array.Empty<LinkedPullRequest>());

    public Task ClosePullRequestAsync(int prNumber, CancellationToken ct) => Task.CompletedTask;

    public Task CheckoutRemoteBranchAsync(WorkspacePath workspacePath, BranchName branchName, CancellationToken ct) =>
        Task.CompletedTask;

    public Task<MergeResult> MergeFromBaseAsync(WorkspacePath workspacePath, CancellationToken ct) =>
        Task.FromResult(new MergeResult { Success = true, HasConflicts = false, ConflictFiles = Array.Empty<string>() });

    public Task PullAsync(WorkspacePath workspacePath, CancellationToken ct) => Task.CompletedTask;

    public Task UpdatePullRequestAsync(int prNumber, string body, bool markReady, CancellationToken ct) =>
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
        var idx = PullRequests.FindIndex(pr => pr.Number == prNumber);
        if (idx >= 0)
        {
            var existing = PullRequests[idx];
            var updatedLabels = existing.Labels.Append(label).Distinct().ToList();
            PullRequests[idx] = new PullRequestSummary
            {
                Number = existing.Number,
                Identifier = existing.Identifier,
                Title = existing.Title,
                Description = existing.Description,
                Labels = updatedLabels,
                BranchName = existing.BranchName,
                TargetBranch = existing.TargetBranch,
                Url = existing.Url,
                IsDraft = existing.IsDraft,
                Author = existing.Author,
                CreatedAt = existing.CreatedAt,
                HasAutoMerge = existing.HasAutoMerge,
            };
        }
        PrLabelChanges.Add(("Add", prNumber, label));
        return Task.CompletedTask;
    }

    public Task RemovePrLabelAsync(int prNumber, string label, CancellationToken ct)
    {
        var idx = PullRequests.FindIndex(pr => pr.Number == prNumber);
        if (idx >= 0)
        {
            var existing = PullRequests[idx];
            var updatedLabels = existing.Labels.Where(l => l != label).ToList();
            PullRequests[idx] = new PullRequestSummary
            {
                Number = existing.Number,
                Identifier = existing.Identifier,
                Title = existing.Title,
                Description = existing.Description,
                Labels = updatedLabels,
                BranchName = existing.BranchName,
                TargetBranch = existing.TargetBranch,
                Url = existing.Url,
                IsDraft = existing.IsDraft,
                Author = existing.Author,
                CreatedAt = existing.CreatedAt,
                HasAutoMerge = existing.HasAutoMerge,
            };
        }
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

    // ── IPullRequestHousekeepingProvider implementations ─────────────────

    /// <summary>
    /// Returns the status seeded in <see cref="PrMergeability"/> for the given PR number,
    /// or <see cref="PrMergeabilityStatus.Unknown"/> if no entry exists.
    /// </summary>
    public Task<PrMergeabilityStatus> IsPullRequestBehindBaseAsync(int prNumber, CancellationToken ct)
    {
        var status = PrMergeability.TryGetValue(prNumber, out var s)
            ? s
            : PrMergeabilityStatus.Unknown;
        return Task.FromResult(status);
    }

    /// <summary>
    /// Records <paramref name="prNumber"/> in <see cref="BranchUpdateCalls"/>. No-op otherwise.
    /// <para>
    /// IMPORTANT: <c>HousekeepingService.FireAndForget</c> must be overridden to
    /// <c>task => task</c> before asserting on <see cref="BranchUpdateCalls"/> — the
    /// production default discards this task asynchronously.
    /// </para>
    /// </summary>
    public Task UpdatePullRequestBranchAsync(int prNumber, CancellationToken ct)
    {
        BranchUpdateCalls.Add(prNumber);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the linked issue identifiers seeded in <see cref="PrLinkedIssues"/> for the
    /// given PR number, or an empty list if no entry exists.
    /// </summary>
    public Task<IReadOnlyList<string>> ExtractLinkedIssuesAsync(int prNumber, CancellationToken ct)
    {
        var issues = PrLinkedIssues.TryGetValue(prNumber, out var list)
            ? (IReadOnlyList<string>)list
            : Array.Empty<string>();
        return Task.FromResult(issues);
    }

    /// <summary>
    /// Returns the branch names seeded in <see cref="AgentBranches"/>.
    /// All names should use the <c>feature/auto-{issueId}-{slug}</c> prefix so
    /// <c>StaleBranchCleaner.ExtractIssueId</c> can parse them correctly.
    /// </summary>
    public Task<IReadOnlyList<string>> ListAgentBranchesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>(AgentBranches);

    /// <summary>
    /// Records <paramref name="branchName"/> in <see cref="DeletedBranches"/>. No-op otherwise.
    /// </summary>
    public Task DeleteBranchAsync(string branchName, CancellationToken ct)
    {
        DeletedBranches.Add(branchName);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the merged state seeded in <see cref="PrMergedState"/> for the given PR number,
    /// or <c>null</c> if no entry exists.
    /// </summary>
    public Task<bool?> GetPullRequestMergedStateAsync(int prNumber, CancellationToken ct)
    {
        var state = PrMergedState.TryGetValue(prNumber, out var s) ? s : (bool?)null;
        return Task.FromResult(state);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
