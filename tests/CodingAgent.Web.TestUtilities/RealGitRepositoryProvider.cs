using CodingAgent.Infrastructure.Git;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using LibGit2Sharp;
using Polly;
using MergeResult = CodingAgent.Pipeline.Models.MergeResult;

namespace CodingAgent.Web.TestUtilities;

/// <summary>One pull request kept in memory by <see cref="RealGitRepositoryProvider"/>.</summary>
public sealed class RealGitPullRequest
{
    public required int Number { get; init; }
    public required string BranchName { get; init; }
    public required string BaseBranch { get; init; }
    public required string Title { get; set; }
    public required string Body { get; set; }
    public required bool IsDraft { get; set; }
    public PullRequestState State { get; set; } = PullRequestState.Open;
    public List<string> Labels { get; } = [];
}

/// <summary>
/// Repository provider for E2E tests that runs the production git operations against a local bare repository
/// and keeps the pull requests in memory. Clone, branch, commit, push, rebase and diff are real, so rework,
/// rebase, conflict and brain paths see the history a real provider would.
/// A test selects it explicitly, for example through <c>FakeProviderFactory.RepositoryProviderOverride</c>.
/// <para>
/// One instance serves the worker, API and Scheduler hosts, which call it from different threads. Production code
/// disposes the providers it receives from a factory, so <see cref="DisposeAsync"/> does nothing.
/// </para>
/// <para>
/// A local path sends no auth challenge, so the credentials the git operations are given are never used.
/// </para>
/// </summary>
public sealed class RealGitRepositoryProvider : IRepositoryProvider
{
    private const string TokenUsername = "user";
    private const string Token = "token";
    private const string PullRequestUrlPrefix = "https://github.com/e2e-org/e2e-repo/pull/";

    private readonly string _remotePath;
    private readonly object _lock = new();

    public RealGitRepositoryProvider(string remotePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(remotePath);
        _remotePath = remotePath;
    }

    public RepositoryProviderType ProviderType => RepositoryProviderType.GitHub;
    public string BaseBranch => "main";
    public string RepositoryFullName => "e2e-org/e2e-repo";
    public bool SupportsServerSideBranchUpdate => true;

    /// <summary>Every pull request created through this provider. Guarded by a lock inside the provider.</summary>
    public List<RealGitPullRequest> PullRequests { get; } = [];

    /// <summary>Per-PR mergeability status. PRs not in the dictionary return <see cref="PrMergeabilityStatus.Unknown"/>.</summary>
    public Dictionary<int, PrMergeabilityStatus> PrMergeability { get; } = [];

    /// <summary>Per-PR linked issue identifiers. PRs not in the dictionary return an empty list.</summary>
    public Dictionary<int, List<string>> PrLinkedIssues { get; } = [];

    /// <summary>PR numbers for which <see cref="UpdatePullRequestBranchAsync"/> was called.</summary>
    public List<int> BranchUpdateCalls { get; } = [];

    /// <summary>Every review submitted through either <c>SubmitPullRequestReviewAsync</c> overload.</summary>
    public List<(int PrNumber, string Body, PullRequestReviewType Type)> PostedReviews { get; } = [];

    // ── Git: production operations against the bare repository ──────────────────────

    public Task CloneAsync(WorkspacePath workspacePath, CancellationToken ct) =>
        RepositoryGitOperations.Clone(
            workspacePath, _remotePath, BaseBranch, TokenUsername, Token, ResiliencePipeline.Empty, ct);

    public Task PullAsync(WorkspacePath workspacePath, CancellationToken ct) =>
        RepositoryGitOperations.Pull(
            workspacePath, _remotePath, BaseBranch, TokenUsername, Token, ResiliencePipeline.Empty, ct);

    public Task<string> CreateBranchAsync(WorkspacePath workspacePath, BranchName branchName, CancellationToken ct) =>
        Task.Run(() => RepositoryGitOperations.CreateBranch(workspacePath, branchName), ct);

    public Task CheckoutRemoteBranchAsync(WorkspacePath workspacePath, BranchName branchName, CancellationToken ct) =>
        Task.Run(() => RepositoryGitOperations.CheckoutRemoteBranch(workspacePath, branchName), ct);

    public Task<MergeResult> MergeFromBaseAsync(WorkspacePath workspacePath, CancellationToken ct) =>
        RepositoryGitOperations.MergeFromBase(
            workspacePath, _remotePath, BaseBranch, TokenUsername, Token, ResiliencePipeline.Empty, ct);

    public Task<IReadOnlyList<string>> CommitAllAsync(
        WorkspacePath workspacePath, string message, IReadOnlyList<string>? blacklistedPaths,
        CancellationToken ct, IReadOnlyList<string>? pipelineInjectedPaths = null) =>
        SharedRepositoryOperations.CommitAllAsync(workspacePath, message, blacklistedPaths, ct, pipelineInjectedPaths);

    public Task<IReadOnlyList<string>> CommitAllAsync(
        WorkspacePath workspacePath, string message, IReadOnlyList<string>? blacklistedPaths,
        bool allowEmpty, CancellationToken ct, IReadOnlyList<string>? pipelineInjectedPaths = null) =>
        SharedRepositoryOperations.CommitAllAsync(
            workspacePath, message, blacklistedPaths, allowEmpty, ct, pipelineInjectedPaths);

    /// <summary>
    /// Public so a test holding this class, not the interface, can commit without a blacklist: a default
    /// interface method is not callable through the class type.
    /// </summary>
    public Task CommitAllAsync(WorkspacePath workspacePath, string message, CancellationToken ct) =>
        SharedRepositoryOperations.CommitAllAsync(workspacePath, message, ct);

    public Task PushBranchAsync(WorkspacePath workspacePath, BranchName branchName, CancellationToken ct) =>
        PushBranchAsync(workspacePath, branchName, forcePush: false, ct);

    public Task PushBranchAsync(WorkspacePath workspacePath, BranchName branchName, bool forcePush, CancellationToken ct) =>
        RepositoryGitOperations.Push(
            workspacePath, _remotePath, branchName, forcePush, TokenUsername,
            _ => Task.FromResult(Token), ResiliencePipeline.Empty, ct);

    public Task<string> GetHeadCommitShaAsync(WorkspacePath workspacePath, CancellationToken ct) =>
        SharedRepositoryOperations.GetHeadCommitShaAsync(workspacePath, ct);

    public Task<bool> HasCommitsAheadAsync(WorkspacePath workspacePath, CancellationToken ct) =>
        SharedRepositoryOperations.HasCommitsAheadAsync(workspacePath, BaseBranch, ResiliencePipeline.Empty, ct);

    public Task<IReadOnlyList<FileChangeSummary>> GetFileChangesAsync(WorkspacePath workspacePath, CancellationToken ct) =>
        SharedRepositoryOperations.GetFileChangesAsync(workspacePath, BaseBranch, ct);

    public Task<IReadOnlyDictionary<string, string>?> GetWorkingTreeFingerprintAsync(
        WorkspacePath workspacePath, IReadOnlyList<string> excludedPaths, CancellationToken ct) =>
        SharedRepositoryOperations.GetWorkingTreeFingerprintAsync(workspacePath, excludedPaths, ct);

    public Task ValidateAsync(CancellationToken ct) => Task.CompletedTask;

    // ── Pull requests: in memory ────────────────────────────────────────────────────

    // TODO [WARNING] The provider is a public, reusable double for follow-up work (#4121, #4122, #4100, #4102). Most of its
    // behaviour has no test in this diff: the smoke test exercises only the create, list-agent-PRs and close happy paths.
    // Untested: the duplicate-branch InvalidOperationException ("A pull request already exists", which
    // PullRequestOrchestrator matches on text); number allocation after PRs exist; GetPullRequestStateAsync and
    // UpdatePullRequestAsync on an unknown PR (KeyNotFoundException); UpdatePullRequestAsync(markReady: false) setting
    // IsDraft = true; GetPullRequestMergedStateAsync mapping; ReadRemoteFile returning null for a missing branch or file;
    // RemoteCommitMessages ordering and trimming; ListAgentBranchesAsync and DeleteBranchAsync. Add small unit tests.
    public Task<string> CreatePullRequestAsync(PullRequestInfo prInfo, CancellationToken ct)
    {
        lock (_lock)
        {
            if (PullRequests.Any(p => p.State == PullRequestState.Open && p.BranchName == prInfo.BranchName))
                throw new InvalidOperationException($"A pull request already exists for branch {prInfo.BranchName}");

            var number = PullRequests.Count == 0 ? 1 : PullRequests.Max(p => p.Number) + 1;
            PullRequests.Add(new RealGitPullRequest
            {
                Number = number,
                BranchName = prInfo.BranchName,
                BaseBranch = prInfo.BaseBranch,
                Title = prInfo.Title,
                Body = prInfo.Body,
                IsDraft = prInfo.IsDraft
            });
            return Task.FromResult(UrlOf(number));
        }
    }

    public Task<IReadOnlyList<LinkedPullRequest>> GetAgentPullRequestsAsync(
        IssueIdentifier issueIdentifier, CancellationToken ct)
    {
        var prefix = $"{PipelineConstants.BranchPrefix}{issueIdentifier}-";
        lock (_lock)
        {
            IReadOnlyList<LinkedPullRequest> linked = PullRequests
                .Where(p => p.State == PullRequestState.Open && p.BranchName.StartsWith(prefix, StringComparison.Ordinal))
                .Select(p => new LinkedPullRequest
                {
                    BranchName = p.BranchName,
                    IsDraft = p.IsDraft,
                    Number = p.Number,
                    Url = UrlOf(p.Number)
                })
                .ToList();
            return Task.FromResult(linked);
        }
    }

    public Task ClosePullRequestAsync(int prNumber, CancellationToken ct)
    {
        lock (_lock)
        {
            Get(prNumber).State = PullRequestState.Closed;
            return Task.CompletedTask;
        }
    }

    public Task<PullRequestState> GetPullRequestStateAsync(int prNumber, CancellationToken ct)
    {
        lock (_lock)
            return Task.FromResult(Get(prNumber).State);
    }

    // The signature must match the interface exactly: a differing one does not implement the member,
    // and the interface default, which throws NotSupportedException, runs instead.
    public Task UpdatePullRequestAsync(int prNumber, string? body, bool? markReady, CancellationToken ct)
    {
        lock (_lock)
        {
            var pr = Get(prNumber);
            if (body is not null)
                pr.Body = body;
            if (markReady is not null)
                pr.IsDraft = !markReady.Value;
            return Task.CompletedTask;
        }
    }

    public Task<string?> GetPullRequestBodyAsync(int prNumber, CancellationToken ct)
    {
        lock (_lock)
            return Task.FromResult(Find(prNumber)?.Body);
    }

    /// <summary>
    /// Reads the pull request's changed files from the bare repository, as GitHub does: the diff between the
    /// branch and its merge base with the base branch, and the branch's tip as <c>HeadSha</c>.
    /// Null when the pull request or its branch is unknown, which the pipeline reads as "could not verify".
    /// </summary>
    public Task<PullRequestChangedFiles?> GetPullRequestChangedFilesAsync(int prNumber, CancellationToken ct)
    {
        RealGitPullRequest? pr;
        lock (_lock)
            pr = Find(prNumber);
        if (pr is null)
            return Task.FromResult<PullRequestChangedFiles?>(null);

        using var repo = new Repository(_remotePath);
        var branchTip = repo.Branches[pr.BranchName]?.Tip;
        var baseTip = repo.Branches[pr.BaseBranch]?.Tip;
        if (branchTip is null || baseTip is null)
            return Task.FromResult<PullRequestChangedFiles?>(null);

        var mergeBase = repo.ObjectDatabase.FindMergeBase(baseTip, branchTip);
        var changes = repo.Diff.Compare<TreeChanges>(mergeBase?.Tree, branchTip.Tree);
        var files = changes
            .Select(c => c.Path)
            .Concat(changes.Where(c => c.Status is ChangeKind.Renamed or ChangeKind.Copied).Select(c => c.OldPath))
            .Distinct()
            .ToList();
        return Task.FromResult<PullRequestChangedFiles?>(new PullRequestChangedFiles(files, true, branchTip.Sha));
    }

    public Task<PagedResult<PullRequestSummary>> ListOpenPullRequestsAsync(
        int page, int pageSize, IReadOnlyList<string>? labels, CancellationToken ct)
    {
        SharedPrOperations.ValidatePaginationArgs(page, pageSize);
        lock (_lock)
        {
            var open = PullRequests
                .Where(p => p.State == PullRequestState.Open)
                .Where(p => labels is null || labels.All(p.Labels.Contains))
                .Select(ToSummary)
                .ToList();
            var items = open.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return Task.FromResult(SharedPrOperations.BuildPagedResult(items, page, pageSize, open.Count > page * pageSize));
        }
    }

    public Task<PullRequestSummary?> GetOpenPullRequestAsync(int prNumber, CancellationToken ct)
    {
        lock (_lock)
        {
            var pr = Find(prNumber);
            return Task.FromResult(pr is { State: PullRequestState.Open } ? ToSummary(pr) : null);
        }
    }

    public Task AddPrLabelAsync(int prNumber, string label, CancellationToken ct)
    {
        lock (_lock)
        {
            var labels = Get(prNumber).Labels;
            if (!labels.Contains(label))
                labels.Add(label);
            return Task.CompletedTask;
        }
    }

    public Task RemovePrLabelAsync(int prNumber, string label, CancellationToken ct)
    {
        lock (_lock)
        {
            Get(prNumber).Labels.Remove(label);
            return Task.CompletedTask;
        }
    }

    public Task<bool?> GetPullRequestMergedStateAsync(int prNumber, CancellationToken ct)
    {
        lock (_lock)
        {
            bool? merged = Find(prNumber)?.State switch
            {
                PullRequestState.Merged => true,
                PullRequestState.Closed => false,
                _ => null
            };
            return Task.FromResult(merged);
        }
    }

    public Task<PrMergeabilityStatus> IsPullRequestBehindBaseAsync(int prNumber, CancellationToken ct)
    {
        lock (_lock)
            return Task.FromResult(PrMergeability.GetValueOrDefault(prNumber, PrMergeabilityStatus.Unknown));
    }

    public Task<IReadOnlyList<string>> ExtractLinkedIssuesAsync(int prNumber, CancellationToken ct)
    {
        lock (_lock)
        {
            IReadOnlyList<string> issues = PrLinkedIssues.TryGetValue(prNumber, out var list) ? list.ToList() : [];
            return Task.FromResult(issues);
        }
    }

    public Task UpdatePullRequestBranchAsync(int prNumber, CancellationToken ct)
    {
        lock (_lock)
        {
            BranchUpdateCalls.Add(prNumber);
            return Task.CompletedTask;
        }
    }

    public Task SubmitPullRequestReviewAsync(
        int prNumber, string body, PullRequestReviewType type, CancellationToken ct)
    {
        lock (_lock)
        {
            PostedReviews.Add((prNumber, body, type));
            return Task.CompletedTask;
        }
    }

    public Task SubmitPullRequestReviewAsync(int prNumber, ReviewSubmission submission, CancellationToken ct) =>
        SubmitPullRequestReviewAsync(prNumber, submission.Body, submission.Type, ct);

    // ── Branches: the bare repository ───────────────────────────────────────────────

    public Task<IReadOnlyList<string>> ListAgentBranchesAsync(CancellationToken ct)
    {
        using var repo = new Repository(_remotePath);
        IReadOnlyList<string> branches = repo.Branches
            .Select(b => b.FriendlyName)
            .Where(name => name.StartsWith(PipelineConstants.BranchPrefix, StringComparison.Ordinal))
            .ToList();
        return Task.FromResult(branches);
    }

    public Task DeleteBranchAsync(BranchName branchName, CancellationToken ct)
    {
        using var repo = new Repository(_remotePath);
        var branch = repo.Branches[branchName.Value];
        if (branch is not null)
            repo.Branches.Remove(branch);
        return Task.CompletedTask;
    }

    // ── Test helpers ────────────────────────────────────────────────────────────────

    /// <summary>Marks the pull request merged. It moves no commits.</summary>
    public void MarkMerged(int prNumber)
    {
        lock (_lock)
            Get(prNumber).State = PullRequestState.Merged;
    }

    /// <summary>
    /// The content of <paramref name="path"/> at the tip of <paramref name="branch"/> in the bare repository,
    /// read from the blob (a bare repository has no working tree). Null when the branch or the file is absent.
    /// </summary>
    public string? ReadRemoteFile(string branch, string path)
    {
        using var repo = new Repository(_remotePath);
        var entry = repo.Branches[branch]?.Tip?[path];
        return (entry?.Target as Blob)?.GetContentText();
    }

    /// <summary>Commit messages of <paramref name="branch"/> in the bare repository, newest first, trimmed.</summary>
    public IReadOnlyList<string> RemoteCommitMessages(string branch)
    {
        using var repo = new Repository(_remotePath);
        var tip = repo.Branches[branch];
        return tip is null ? [] : tip.Commits.Select(c => c.Message.Trim()).ToList();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ── Internals ───────────────────────────────────────────────────────────────────

    private static string UrlOf(int number) => $"{PullRequestUrlPrefix}{number}";

    private RealGitPullRequest? Find(int prNumber) => PullRequests.FirstOrDefault(p => p.Number == prNumber);

    private RealGitPullRequest Get(int prNumber) =>
        Find(prNumber) ?? throw new KeyNotFoundException($"Pull request #{prNumber} not found.");

    private static PullRequestSummary ToSummary(RealGitPullRequest pr) => new()
    {
        Number = pr.Number,
        Identifier = pr.Number.ToString(),
        Title = pr.Title,
        Description = pr.Body,
        Labels = pr.Labels.ToList(),
        BranchName = pr.BranchName,
        TargetBranch = pr.BaseBranch,
        Url = UrlOf(pr.Number),
        IsDraft = pr.IsDraft
    };
}
