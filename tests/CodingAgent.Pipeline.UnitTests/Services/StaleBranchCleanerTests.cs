using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;
using Serilog;
using Xunit;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="StaleBranchCleaner"/> extracted from HousekeepingService.
/// All tests control the <c>UtcNow</c> seam directly on the <see cref="StaleBranchCleaner"/> instance.
/// </summary>
public class StaleBranchCleanerTests
{
    private const string RepoId = "rp-cleaner";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static StaleBranchCleaner Create()
        => new(Log.Logger);

    private static PullRequestSummary MakePr(int number, string branch = "feature/pr")
        => new()
        {
            Number = number,
            Identifier = number.ToString(),
            Title = $"PR #{number}",
            Description = string.Empty,
            Labels = Array.Empty<string>(),
            BranchName = branch,
            TargetBranch = "main",
            Url = $"https://example.com/pr/{number}",
            IsDraft = false,
        };

    private static IssueDetail MakeIssue(string id, params string[] labels) => new()
    {
        Identifier = id,
        Title = $"Issue {id}",
        Description = string.Empty,
        Labels = labels
    };

    private static Task RunAsync(
        StaleBranchCleaner cleaner,
        Mock<IRepositoryProvider> repo,
        Mock<IIssueProvider> issues,
        bool enabled = true,
        int intervalMinutes = 0,
        IReadOnlyList<PullRequestSummary>? agentDonePrs = null)
    {
        var repoTag = new KeyValuePair<string, object?>("repo_provider_id", RepoId);
        return cleaner.RunIfDueAsync(
            repo.Object, issues.Object,
            agentDonePrs ?? Array.Empty<PullRequestSummary>(),
            RepoId, repoTag,
            enabled, intervalMinutes,
            CancellationToken.None);
    }

    private static Mock<IRepositoryProvider> MakeRepoWithEmptyBranches()
    {
        var mock = new Mock<IRepositoryProvider>();
        mock.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[]);
        mock.Setup(p => p.ListOpenPullRequestsAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.Is<IReadOnlyList<string>?>(l => l == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PullRequestSummary>
            {
                Items = Array.Empty<PullRequestSummary>().AsReadOnly(),
                Page = 1, PageSize = 100, HasMore = false
            });
        return mock;
    }

    // ── ExtractIssueId ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("feature/auto-123-fix-login", "123")]
    [InlineData("feature/auto-42-update-deps", "42")]
    [InlineData("feature/auto-999", "999")]     // no slug
    [InlineData("feature/auto-", null)]          // empty after prefix
    [InlineData("main", null)]                   // not an agent branch
    [InlineData("feature/manual-123", null)]     // wrong prefix
    public void ExtractIssueId_VariousInputs_ReturnsExpected(string branchName, string? expected)
    {
        var result = StaleBranchCleaner.ExtractIssueId(branchName);
        result.Should().Be(expected);
    }

    // ── Enabled=false → no API calls ─────────────────────────────────────────

    [Fact]
    public async Task RunIfDueAsync_CleanupDisabled_ListNotCalled()
    {
        var cleaner = Create();
        var repo = MakeRepoWithEmptyBranches();
        var issues = new Mock<IIssueProvider>();

        await RunAsync(cleaner, repo, issues, enabled: false);

        repo.Verify(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Interval not elapsed → no call ───────────────────────────────────────

    [Fact]
    public async Task RunIfDueAsync_IntervalNotElapsed_ListNotCalled()
    {
        var cleaner = Create();
        var now = DateTimeOffset.UtcNow;
        cleaner.UtcNow = () => now;

        var repo = MakeRepoWithEmptyBranches();
        var issues = new Mock<IIssueProvider>();

        // First call — seeds _lastCleanupAt
        await RunAsync(cleaner, repo, issues, intervalMinutes: 60);
        repo.Invocations.Clear();

        // Second call — same instant, interval has not elapsed
        await RunAsync(cleaner, repo, issues, intervalMinutes: 60);

        repo.Verify(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()), Times.Never,
            "interval has not elapsed — ListAgentBranchesAsync must not be called again");
    }

    // ── Interval elapsed → cleanup runs ──────────────────────────────────────

    [Fact]
    public async Task RunIfDueAsync_IntervalElapsed_ListIsCalled()
    {
        var cleaner = Create();
        var t0 = DateTimeOffset.UtcNow;
        cleaner.UtcNow = () => t0;

        var repo = MakeRepoWithEmptyBranches();
        var issues = new Mock<IIssueProvider>();

        // First call — seeds
        await RunAsync(cleaner, repo, issues, intervalMinutes: 60);
        repo.Invocations.Clear();

        // Advance past interval
        cleaner.UtcNow = () => t0.AddMinutes(61);
        await RunAsync(cleaner, repo, issues, intervalMinutes: 60);

        repo.Verify(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()), Times.Once,
            "interval elapsed — ListAgentBranchesAsync must be called");
    }

    // ── Interval boundary: exactly 60 minutes ────────────────────────────────

    [Fact]
    public async Task RunIfDueAsync_IntervalBoundary_59Minutes_ListNotCalled()
    {
        var cleaner = Create();
        var t0 = DateTimeOffset.UtcNow;
        cleaner.UtcNow = () => t0;

        var repo = MakeRepoWithEmptyBranches();
        var issues = new Mock<IIssueProvider>();

        // First call — seeds
        await RunAsync(cleaner, repo, issues, intervalMinutes: 60);
        repo.Invocations.Clear();

        // Advance to exactly 59 minutes — should not trigger
        cleaner.UtcNow = () => t0.AddMinutes(59);
        await RunAsync(cleaner, repo, issues, intervalMinutes: 60);

        repo.Verify(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()), Times.Never,
            "59 minutes is below the 60-minute threshold — cleanup must not run");
    }

    [Fact]
    public async Task RunIfDueAsync_IntervalBoundary_ExactlyAtInterval_ListIsCalled()
    {
        var cleaner = Create();
        var t0 = DateTimeOffset.UtcNow;
        cleaner.UtcNow = () => t0;

        var repo = MakeRepoWithEmptyBranches();
        var issues = new Mock<IIssueProvider>();

        // First call — seeds
        await RunAsync(cleaner, repo, issues, intervalMinutes: 60);
        repo.Invocations.Clear();

        // Advance to exactly 60 minutes — should trigger (>= boundary)
        cleaner.UtcNow = () => t0.AddMinutes(60);
        await RunAsync(cleaner, repo, issues, intervalMinutes: 60);

        repo.Verify(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()), Times.Once,
            "exactly 60 minutes meets the >= boundary — cleanup must run");
    }

    // ── Branch with open PR → not deleted ────────────────────────────────────

    [Fact]
    public async Task RunIfDueAsync_BranchWithOpenPr_NotDeleted()
    {
        var cleaner = Create();
        var agentBranch = $"{PipelineConstants.BranchPrefix}99-some-feature";
        var openPr = MakePr(99, agentBranch);

        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[agentBranch]);
        repo.Setup(p => p.ListOpenPullRequestsAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.Is<IReadOnlyList<string>?>(l => l == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PullRequestSummary>
            {
                Items = new[] { openPr }.AsReadOnly(),
                Page = 1, PageSize = 100, HasMore = false
            });

        var issues = new Mock<IIssueProvider>();

        await RunAsync(cleaner, repo, issues);

        repo.Verify(p => p.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "branch has an open PR — must not be deleted");
    }

    // ── Branch with active issue label → not deleted ─────────────────────────

    [Fact]
    public async Task RunIfDueAsync_BranchWithActiveLabel_NotDeleted()
    {
        var cleaner = Create();
        var agentBranch = $"{PipelineConstants.BranchPrefix}42-fix-login";

        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[agentBranch]);
        repo.Setup(p => p.ListOpenPullRequestsAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.Is<IReadOnlyList<string>?>(l => l == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PullRequestSummary>
            {
                Items = Array.Empty<PullRequestSummary>().AsReadOnly(),
                Page = 1, PageSize = 100, HasMore = false
            });

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.Next));

        await RunAsync(cleaner, repo, issues);

        repo.Verify(p => p.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "issue has agent:next — must not delete branch");
    }

    [Fact]
    public async Task RunIfDueAsync_BranchWithEpicReviewLabel_NotDeleted()
    {
        var cleaner = Create();
        var agentBranch = $"{PipelineConstants.BranchPrefix}42-epic-feature";

        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[agentBranch]);
        repo.Setup(p => p.ListOpenPullRequestsAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.Is<IReadOnlyList<string>?>(l => l == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PullRequestSummary>
            {
                Items = Array.Empty<PullRequestSummary>().AsReadOnly(),
                Page = 1, PageSize = 100, HasMore = false
            });

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.EpicReview));

        await RunAsync(cleaner, repo, issues);

        repo.Verify(p => p.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "issue has agent:epic-review — awaiting human review, must not delete branch");
    }

    // ── Branch with done issue and no open PR → deleted ───────────────────────

    [Fact]
    public async Task RunIfDueAsync_BranchWithDoneIssueNoPr_Deleted()
    {
        var cleaner = Create();
        var agentBranch = $"{PipelineConstants.BranchPrefix}42-fix-login";

        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[agentBranch]);
        repo.Setup(p => p.ListOpenPullRequestsAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.Is<IReadOnlyList<string>?>(l => l == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PullRequestSummary>
            {
                Items = Array.Empty<PullRequestSummary>().AsReadOnly(),
                Page = 1, PageSize = 100, HasMore = false
            });
        repo.Setup(p => p.DeleteBranchAsync(agentBranch, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.Done));

        await RunAsync(cleaner, repo, issues);

        repo.Verify(p => p.DeleteBranchAsync(agentBranch, It.IsAny<CancellationToken>()), Times.Once,
            "no open PR + terminal issue label → branch must be deleted");
    }

    // ── FetchAllOpenAgentPrBranches throws → cleanup skipped ─────────────────

    [Fact]
    public async Task RunIfDueAsync_FetchAllOpenPrBranchesThrows_CleanupSkipped()
    {
        var cleaner = Create();
        var agentBranch = $"{PipelineConstants.BranchPrefix}42-fix-something";

        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[agentBranch]);
        repo.Setup(p => p.ListOpenPullRequestsAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.Is<IReadOnlyList<string>?>(l => l == null),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new NotSupportedException("not supported"));

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.Done));

        var ex = await Record.ExceptionAsync(() => RunAsync(cleaner, repo, issues));

        ex.Should().BeNull("FetchAllOpenAgentPrBranches failure must not propagate");
        repo.Verify(p => p.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "cleanup must be skipped entirely when open-PR fetch fails");
    }

    // ── DeleteBranchAsync throws → continues to next branch ──────────────────

    [Fact]
    public async Task RunIfDueAsync_DeleteThrows_ContinuesProcessing()
    {
        var cleaner = Create();
        var branch1 = $"{PipelineConstants.BranchPrefix}10-feat-a";
        var branch2 = $"{PipelineConstants.BranchPrefix}20-feat-b";

        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[branch1, branch2]);
        repo.Setup(p => p.ListOpenPullRequestsAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.Is<IReadOnlyList<string>?>(l => l == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PullRequestSummary>
            {
                Items = Array.Empty<PullRequestSummary>().AsReadOnly(),
                Page = 1, PageSize = 100, HasMore = false
            });
        repo.Setup(p => p.DeleteBranchAsync(branch1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("server error"));
        repo.Setup(p => p.DeleteBranchAsync(branch2, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("10"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("10", AgentLabels.Done));
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("20"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("20", AgentLabels.Done));

        var ex = await Record.ExceptionAsync(() => RunAsync(cleaner, repo, issues));

        ex.Should().BeNull("delete failure must be swallowed");
        repo.Verify(p => p.DeleteBranchAsync(branch2, It.IsAny<CancellationToken>()), Times.Once,
            "second branch must still be processed after first delete fails");
    }

    // ── ListAgentBranches throws → no crash ──────────────────────────────────

    [Fact]
    public async Task RunIfDueAsync_ListAgentBranchesThrows_NoCrash()
    {
        var cleaner = Create();

        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("API down"));

        var issues = new Mock<IIssueProvider>();

        var ex = await Record.ExceptionAsync(() => RunAsync(cleaner, repo, issues));

        ex.Should().BeNull("ListAgentBranches failure must be swallowed");
        repo.Verify(p => p.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── FetchAllOpenAgentPrBranches pagination — MaxPages cap ─────────────────

    [Fact]
    public async Task RunIfDueAsync_MaxPagesCap_StopsLoopAndProtectsBranch()
    {
        var cleaner = Create();
        var agentBranch = $"{PipelineConstants.BranchPrefix}99-capped-feature";

        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[agentBranch]);
        // Page 1: returns the agent branch, always HasMore=true (malformed provider)
        repo.Setup(p => p.ListOpenPullRequestsAsync(
                1, It.IsAny<int>(),
                It.Is<IReadOnlyList<string>?>(l => l == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PullRequestSummary>
            {
                Items = new[] { MakePr(99, agentBranch) }.AsReadOnly(),
                Page = 1, PageSize = 100, HasMore = true
            });
        // Pages 2+: empty, still HasMore=true
        repo.Setup(p => p.ListOpenPullRequestsAsync(
                It.Is<int>(p => p > 1), It.IsAny<int>(),
                It.Is<IReadOnlyList<string>?>(l => l == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PullRequestSummary>
            {
                Items = Array.Empty<PullRequestSummary>().AsReadOnly(),
                Page = 2, PageSize = 100, HasMore = true
            });

        var issues = new Mock<IIssueProvider>();

        var ex = await Record.ExceptionAsync(() => RunAsync(cleaner, repo, issues));

        ex.Should().BeNull("MaxPages cap must not throw");
        repo.Verify(p => p.DeleteBranchAsync(agentBranch, It.IsAny<CancellationToken>()), Times.Never,
            "branch found on page 1 before MaxPages cap must still be protected");
        repo.Verify(p => p.ListOpenPullRequestsAsync(
            It.IsAny<int>(), It.IsAny<int>(),
            It.Is<IReadOnlyList<string>?>(l => l == null),
            It.IsAny<CancellationToken>()), Times.Exactly(50),
            "exactly MaxPages=50 pages must be fetched before the cap breaks the loop");
    }

    // ── Open PR not in agentDonePrs (pagination regression) ──────────────────

    [Fact]
    public async Task RunIfDueAsync_BranchWithOpenPrNotInAgentDonePrs_NotDeleted()
    {
        var cleaner = Create();
        var agentBranch = $"{PipelineConstants.BranchPrefix}99-over-cap-feature";
        var openPr = MakePr(99, agentBranch);

        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[agentBranch]);
        // agentDonePrs passed in is EMPTY — the PR was beyond the pagination cap.
        // Independent open-PR check confirms an open PR exists.
        repo.Setup(p => p.ListOpenPullRequestsAsync(
                It.IsAny<int>(), It.IsAny<int>(),
                It.Is<IReadOnlyList<string>?>(l => l == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PullRequestSummary>
            {
                Items = new[] { openPr }.AsReadOnly(),
                Page = 1, PageSize = 100, HasMore = false
            });

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("99"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("99", AgentLabels.Done));

        // Pass empty agentDonePrs to simulate pagination cap
        await RunAsync(cleaner, repo, issues, agentDonePrs: Array.Empty<PullRequestSummary>());

        repo.Verify(p => p.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "branch has an open PR even though it was absent from agentDonePrs — must not be deleted");
    }
}
