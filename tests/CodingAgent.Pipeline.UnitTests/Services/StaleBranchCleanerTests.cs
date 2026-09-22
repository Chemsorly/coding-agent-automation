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
            wasInputTruncated,
            RepoId, repoTag,
            enabled, intervalMinutes,
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

    // ── Branch with open PR in agentDonePrs → not deleted ────────────────────

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

        // Pass the open PR in agentDonePrs — this is the branch protection source
        await RunAsync(cleaner, repo, issues, agentDonePrs: [openPr]);

        repo.Verify(p => p.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "branch has an open PR in agentDonePrs — must not be deleted");
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

        await RunAsync(cleaner, repo, issues, agentDonePrs: Array.Empty<PullRequestSummary>());

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

        await RunAsync(cleaner, repo, issues, agentDonePrs: Array.Empty<PullRequestSummary>());

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

        await RunAsync(cleaner, repo, issues, agentDonePrs: Array.Empty<PullRequestSummary>());

        repo.Verify(p => p.DeleteBranchAsync(agentBranch, It.IsAny<CancellationToken>()), Times.Once,
            "no open PR + terminal issue label → branch must be deleted");
    }

    // ── Truncated input → cleanup skipped with Warning ────────────────────────
    // Coverage traceability for replaced tests (FetchAllOpenAgentPrBranchesAsync was deleted):
    //
    // Old: RunIfDueAsync_FetchAllOpenPrBranchesThrows_CleanupSkipped
    //   Verified: exception from the independent PR scan skipped cleanup and did not propagate.
    //   New coverage: RunIfDueAsync_TruncatedInput_CleanupSkipped (this section) verifies the
    //   equivalent skip behaviour via wasInputTruncated=true. The polling-exception → truncated
    //   propagation path is covered by HousekeepingPollCycleIntegrationTests:
    //   PollTemplateQueuesAsync_FetchPrsFails_AgentDonePrTruncatedIsTrue.
    //
    // Old: RunIfDueAsync_MaxPagesCap_StopsLoopAndProtectsBranch
    //   Verified: the independent scan stopped at exactly 50 pages (Times.Exactly(50)) and
    //   protected a branch found on page 1 when HasMore was perpetually true.
    //   New coverage: TemplatePolllerStaticMethodTests:
    //   FetchAllPagesWithTruncationAsync_HitsPageCap_ReturnsTrueWasTruncated verifies the
    //   page-cap stops the loop and returns WasTruncated=true.
    //   RunIfDueAsync_TruncatedInput_BranchWithOpenPrBeyondCap_NotDeleted (below) verifies
    //   that WasTruncated=true prevents any branch deletion.
    //
    // Old: RunIfDueAsync_BranchWithOpenPrNotInAgentDonePrs_NotDeleted
    //   Verified: a PR absent from agentDonePrs but present via the independent scan was protected.
    //   New coverage: RunIfDueAsync_NotTruncated_OpenPrInAgentDonePrs_NotDeleted verifies the
    //   complement (PR present in agentDonePrs → protected). The truncated path (PR absent from
    //   agentDonePrs because it was beyond the cap) is covered by the two truncation tests below.
    //
    // TODO [WARNING]: RunIfDueAsync_TruncatedInput_CleanupSkipped and
    // RunIfDueAsync_TruncatedInput_BranchWithOpenPrBeyondCap_NotDeleted both verify that
    // ListAgentBranchesAsync is not called (short-circuit), but neither asserts that the
    // Warning log is actually emitted. The AC requires "skipped with a Warning log (not silently
    // proceeding)". Consider injecting a mock ILogger and asserting that _logger.Warning(...) is
    // called when wasInputTruncated=true. A regression that drops the _logger.Warning() call
    // would pass these tests while violating the documented requirement.

    [Fact]
    public async Task RunIfDueAsync_TruncatedInput_CleanupSkipped()
    {
        var cleaner = Create();
        var agentBranch = $"{PipelineConstants.BranchPrefix}42-fix-something";

        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[agentBranch]);

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.Done));

        // Pass wasInputTruncated=true — cleanup must be skipped entirely
        var ex = await Record.ExceptionAsync(() =>
            RunAsync(cleaner, repo, issues,
                agentDonePrs: Array.Empty<PullRequestSummary>(),
                wasInputTruncated: true));

        ex.Should().BeNull("truncated input must not propagate an exception");
        repo.Verify(p => p.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "cleanup must be skipped entirely when input was truncated");
        // ListAgentBranchesAsync must NOT even be called — we short-circuit before that
        repo.Verify(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()), Times.Never,
            "ListAgentBranchesAsync must not be called when input is truncated");
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

        var ex = await Record.ExceptionAsync(() =>
            RunAsync(cleaner, repo, issues, agentDonePrs: Array.Empty<PullRequestSummary>()));

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

    // ── Open PR not in agentDonePrs (truncation path) ────────────────────────

    /// <summary>
    /// Verifies that when <paramref name="wasInputTruncated"/> is true, branch cleanup is skipped
    /// entirely even if the branch would otherwise be eligible for deletion (open PR absent from
    /// the truncated agentDonePrs). This is the key regression guard: no branch whose PR was
    /// beyond the pagination cap must ever be deleted.
    /// </summary>
    [Fact]
    public async Task RunIfDueAsync_TruncatedInput_BranchWithOpenPrBeyondCap_NotDeleted()
    {
        var cleaner = Create();
        var agentBranch = $"{PipelineConstants.BranchPrefix}99-over-cap-feature";

        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[agentBranch]);

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("99"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("99", AgentLabels.Done));

        // agentDonePrs is EMPTY (PR was beyond the pagination cap) and wasInputTruncated=true
        await RunAsync(cleaner, repo, issues,
            agentDonePrs: Array.Empty<PullRequestSummary>(),
            wasInputTruncated: true);

        repo.Verify(p => p.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "cleanup must be skipped when input is truncated — no branch deletion allowed");
    }

    /// <summary>
    /// Verifies that when the full PR list is available (not truncated) and the open PR IS
    /// present in agentDonePrs, the branch is correctly protected from deletion.
    /// </summary>
    [Fact]
    public async Task RunIfDueAsync_NotTruncated_OpenPrInAgentDonePrs_NotDeleted()
    {
        var cleaner = Create();
        var agentBranch = $"{PipelineConstants.BranchPrefix}99-some-feature";
        var openPr = MakePr(99, agentBranch);

        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[agentBranch]);

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("99"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("99", AgentLabels.Done));

        // The PR is present in agentDonePrs AND wasInputTruncated=false
        await RunAsync(cleaner, repo, issues,
            agentDonePrs: [openPr],
            wasInputTruncated: false);

        repo.Verify(p => p.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "branch with open PR present in agentDonePrs must not be deleted");
    }
}
