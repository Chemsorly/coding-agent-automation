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
        IReadOnlyList<PullRequestSummary>? agentDonePrs = null,
        bool wasInputTruncated = false)
    {
        var repoTag = new KeyValuePair<string, object?>("repo_provider_id", RepoId);
        return cleaner.RunIfDueAsync(
            repo.Object, issues.Object,
            agentDonePrs ?? Array.Empty<PullRequestSummary>(),
            RepoId, repoTag,
            enabled, intervalMinutes,
            wasInputTruncated,
            CancellationToken.None);
    }

    private static Mock<IRepositoryProvider> MakeRepoWithEmptyBranches()
    {
        var mock = new Mock<IRepositoryProvider>();
        mock.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[]);
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

    // ── Input truncated → cleanup skipped ────────────────────────────────────

    [Fact]
    public async Task RunIfDueAsync_WhenInputTruncated_CleanupSkipped()
    {
        var cleaner = Create();
        var agentBranch = $"{PipelineConstants.BranchPrefix}99-some-feature";

        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[agentBranch]);

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("99"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("99", AgentLabels.Done));

        // Pass wasInputTruncated=true with no agentDonePrs entry for this branch
        await RunAsync(cleaner, repo, issues, wasInputTruncated: true,
            agentDonePrs: Array.Empty<PullRequestSummary>());

        repo.Verify(p => p.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "cleanup must be skipped when input is truncated — branch must not be deleted");
    }

    [Fact]
    public async Task RunIfDueAsync_WhenInputTruncated_NoProviderApisCalled()
    {
        // Confirms the truncation early-exit fires before any per-branch API calls (GetIssueAsync).
        var cleaner = Create();
        var agentBranch = $"{PipelineConstants.BranchPrefix}42-fix-login";

        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[agentBranch]);

        var issues = new Mock<IIssueProvider>();

        await RunAsync(cleaner, repo, issues, wasInputTruncated: true,
            agentDonePrs: Array.Empty<PullRequestSummary>());

        issues.Verify(i => i.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "GetIssueAsync must not be called when cleanup is skipped due to truncated input");
        repo.Verify(p => p.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Input not truncated: agentDonePrs used for branch protection ──────────

    [Fact]
    public async Task RunIfDueAsync_WhenInputNotTruncated_UsesAgentDonePrsForProtection()
    {
        // Branch whose PR is in agentDonePrs — must not be deleted.
        // ListOpenPullRequestsAsync is never called (independent fetch removed).
        var cleaner = Create();
        var agentBranch = $"{PipelineConstants.BranchPrefix}99-some-feature";
        var openPr = MakePr(99, agentBranch);

        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[agentBranch]);

        var issues = new Mock<IIssueProvider>();

        await RunAsync(cleaner, repo, issues,
            wasInputTruncated: false,
            agentDonePrs: new[] { openPr });

        repo.Verify(p => p.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "branch has an open PR in agentDonePrs — must not be deleted");
        repo.Verify(p => p.ListOpenPullRequestsAsync(
            It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<IReadOnlyList<string>?>(),
            It.IsAny<CancellationToken>()), Times.Never,
            "StaleBranchCleaner must not call ListOpenPullRequestsAsync independently");
    }

    [Fact]
    public async Task RunIfDueAsync_WhenInputNotTruncated_BranchNotInAgentDonePrs_Deleted()
    {
        // No PR in agentDonePrs + done issue = branch is deleted.
        // ListOpenPullRequestsAsync is never called.
        var cleaner = Create();
        var agentBranch = $"{PipelineConstants.BranchPrefix}42-fix-login";

        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[agentBranch]);
        repo.Setup(p => p.DeleteBranchAsync(agentBranch, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.Done));

        await RunAsync(cleaner, repo, issues,
            wasInputTruncated: false,
            agentDonePrs: Array.Empty<PullRequestSummary>());

        repo.Verify(p => p.DeleteBranchAsync(agentBranch, It.IsAny<CancellationToken>()), Times.Once,
            "no open PR in agentDonePrs + terminal issue label → branch must be deleted");
        repo.Verify(p => p.ListOpenPullRequestsAsync(
            It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<IReadOnlyList<string>?>(),
            It.IsAny<CancellationToken>()), Times.Never,
            "StaleBranchCleaner must not call ListOpenPullRequestsAsync independently");
    }

    // ── Branch with open PR (in agentDonePrs) → not deleted ──────────────────

    [Fact]
    public async Task RunIfDueAsync_BranchWithOpenPr_NotDeleted()
    {
        var cleaner = Create();
        var agentBranch = $"{PipelineConstants.BranchPrefix}99-some-feature";
        var openPr = MakePr(99, agentBranch);

        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[agentBranch]);

        var issues = new Mock<IIssueProvider>();

        // Pass the open PR via agentDonePrs — branch protection comes from agentDonePrs now
        await RunAsync(cleaner, repo, issues, agentDonePrs: new[] { openPr });

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
        repo.Setup(p => p.DeleteBranchAsync(agentBranch, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.Done));

        await RunAsync(cleaner, repo, issues);

        repo.Verify(p => p.DeleteBranchAsync(agentBranch, It.IsAny<CancellationToken>()), Times.Once,
            "no open PR + terminal issue label → branch must be deleted");
    }

    // ── Cleanup skipped when input was truncated (replaces old FetchAllOpenPrBranchesThrows test) ──

    [Fact]
    public async Task RunIfDueAsync_WhenInputTruncated_NoDeletionOccurs()
    {
        // Verifies that when wasInputTruncated=true, no branches are deleted regardless of
        // issue label state. Replaces the old FetchAllOpenPrBranchesThrows test which tested
        // the now-removed independent fetch code path.
        var cleaner = Create();
        var agentBranch = $"{PipelineConstants.BranchPrefix}42-fix-something";

        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[agentBranch]);

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.Done));

        var ex = await Record.ExceptionAsync(() => RunAsync(cleaner, repo, issues,
            wasInputTruncated: true,
            agentDonePrs: Array.Empty<PullRequestSummary>()));

        ex.Should().BeNull("truncated-input skip must not propagate an exception");
        repo.Verify(p => p.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "cleanup must be skipped entirely when input is truncated");
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
}
