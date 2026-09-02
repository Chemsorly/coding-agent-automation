using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
using Moq;
using Serilog;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="HousekeepingService"/> (spec 040 / conflict-rework / stale-branch-cleanup).
/// All tests use the synchronous <c>FireAndForget</c> seam and a controlled <c>UtcNow</c> clock.
/// </summary>
public class HousekeepingServiceTests
{
    private const string RepoId = "rp-1";
    private const string IssueProviderId = "ip-1";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PullRequestSummary MakePr(int number, string branch = "feature/pr", bool isDraft = false)
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
            IsDraft = isDraft
        };

    private static IssueDetail MakeIssue(string id, params string[] labels) => new()
    {
        Identifier = id,
        Title = $"Issue {id}",
        Description = string.Empty,
        Labels = labels
    };

    private static (HousekeepingService Service,
                    Mock<IRepositoryProvider> ProviderMock,
                    Mock<IIssueProvider> IssueProviderMock,
                    Mock<IOrchestratorRunService> RunsMock)
        Create(IEnumerable<PipelineRun>? activeRuns = null)
    {
        var providerMock = new Mock<IRepositoryProvider>();
        var issueProviderMock = new Mock<IIssueProvider>();
        var runsMock = new Mock<IOrchestratorRunService>();
        var activeRunList = (activeRuns ?? Enumerable.Empty<PipelineRun>()).ToList();
        runsMock.Setup(r => r.GetActiveRuns())
                .Returns(activeRunList.AsReadOnly());
        // Also set up the async branch-name method, which HousekeepingService uses since #2270.
        // The default interface implementation derives from GetActiveRuns(), but Moq does not
        // automatically execute default interface members — we must set it up explicitly.
        var activeBranches = activeRunList
            .Where(r => r.BranchName != null)
            .Select(r => r.BranchName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        runsMock.Setup(r => r.GetActiveRunBranchesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(activeBranches);

        var svc = new HousekeepingService(runsMock.Object, Log.Logger);
        svc.FireAndForget = task => task;
        return (svc, providerMock, issueProviderMock, runsMock);
    }

    /// <summary>Runs service with no cleanup, standard defaults.</summary>
    private static Task ExecAsync(
        HousekeepingService svc,
        Mock<IRepositoryProvider> repo,
        Mock<IIssueProvider> issues,
        IReadOnlyList<PullRequestSummary> prs,
        int limit = 1,
        bool branchCleanup = false,
        int intervalMinutes = 60)
        => svc.ExecuteAsync(
            repo.Object, RepoId, issues.Object, IssueProviderId,
            prs, limit, branchCleanup, intervalMinutes, CancellationToken.None);

    private static PipelineRun ActiveRun(string branch) => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = new IssueIdentifier("1"),
        IssueTitle = "Test",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        BranchName = branch
    };

    // ── ExtractIssueId static helper ──────────────────────────────────────────

    [Theory]
    [InlineData("feature/auto-123-fix-login", "123")]
    [InlineData("feature/auto-42-update-deps", "42")]
    [InlineData("feature/auto-999", "999")]     // no slug
    [InlineData("feature/auto-", null)]          // empty after prefix
    [InlineData("main", null)]                   // not an agent branch
    [InlineData("feature/manual-123", null)]     // wrong prefix
    public void ExtractIssueId_VariousInputs_ReturnsExpected(string branchName, string? expected)
    {
        var result = HousekeepingService.ExtractIssueId(branchName);
        result.Should().Be(expected);
    }

    // ── Draft PRs are skipped ─────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_DraftPr_IsSkipped()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);

        await ExecAsync(svc, provider, issues, [MakePr(1, isDraft: true)]);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Active rework branch is excluded ─────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ActiveReworkBranch_IsSkipped()
    {
        var (svc, provider, issues, _) = Create(activeRuns: [ActiveRun("feature/pr-1")]);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);

        await ExecAsync(svc, provider, issues, [MakePr(1, branch: "feature/pr-1")]);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── ExecuteAsync_BranchUpdate_BranchIsActive_IsSkipped ───────────────────

    /// <summary>
    /// Acceptance-criteria test (Issue #2270): asserts UpdatePullRequestBranchAsync is never
    /// called when <c>activeRunBranches</c> contains the PR's branch name.
    /// The active run is injected via IOrchestratorRunService.GetActiveRuns() (in-process path).
    /// See also <see cref="SchedulerRunQueryServiceTests"/> for the Scheduler-specific variant
    /// that exercises the API-backed override of GetActiveRunBranchesAsync.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_BranchUpdate_BranchIsActive_IsSkipped()
    {
        // Arrange: PR #1 is behind base, but its branch has an active pipeline run.
        var activeBranch = "feature/auto-42-my-feature";
        var (svc, provider, issues, _) = Create(activeRuns: [ActiveRun(activeBranch)]);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);

        // Act
        await ExecAsync(svc, provider, issues, [MakePr(1, branch: activeBranch)]);

        // Assert: branch update must be skipped
        provider.Verify(
            p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "UpdatePullRequestBranchAsync must not be called when the PR's branch has an active run");
    }

    /// <summary>
    /// Complementary test: a PR on a *different* branch from the active run is still updated.
    /// Guards against a "any active run → skip all PRs" regression.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_BranchUpdate_DifferentBranchIsActive_ProceedsWithUpdate()
    {
        // Arrange: active run is on a different branch from the PR being evaluated.
        var (svc, provider, issues, _) = Create(activeRuns: [ActiveRun("feature/auto-99-other")]);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        // Act
        await ExecAsync(svc, provider, issues, [MakePr(1, branch: "feature/auto-42-my-feature")]);

        // Assert: branch update proceeds for the unrelated PR
        provider.Verify(
            p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()),
            Times.Once,
            "UpdatePullRequestBranchAsync must be called when only a different branch is active");
    }

    // ── Blocked / Unknown → slot kept ────────────────────────────────────────

    [Theory]
    [InlineData(PrMergeabilityStatus.Blocked)]
    [InlineData(PrMergeabilityStatus.Unknown)]
    public async Task ExecuteAsync_BlockedOrUnknown_IsSkippedAndSlotKept(PrMergeabilityStatus status)
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(1)]);

        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(status);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(2, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);

        await ExecAsync(svc, provider, issues, [MakePr(1), MakePr(2)]);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(2, It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── UpToDate → skipped ────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_UpToDate_IsSkipped()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.UpToDate);

        await ExecAsync(svc, provider, issues, [MakePr(1)]);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Behind → update triggered ─────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Behind_TriggersUpdate()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(1)]);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Concurrency limit = 1: exactly one PR triggered (random selection) ───

    [Fact]
    public async Task ExecuteAsync_LimitOne_OnlyLowestNumberTriggered()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(20), MakePr(10)], limit: 1);

        // With limit=1 and random selection, exactly one PR update should be triggered.
        // The selected PR number must be one of the valid input candidates {10, 20}.
        var updatedPrNumbers = provider.Invocations
            .Where(i => i.Method.Name == nameof(IRepositoryProvider.UpdatePullRequestBranchAsync))
            .Select(i => (int)i.Arguments[0])
            .ToList();
        updatedPrNumbers.Should().HaveCount(1,
            "limit=1 must allow exactly one branch update per tick");
        updatedPrNumbers[0].Should().BeOneOf(new[] { 10, 20 },
            because: "the selected PR must be drawn from the candidate input set");
    }

    // ── Random selection: multiple Behind PRs vary across calls ──────────────

    [Fact]
    public async Task ExecuteAsync_MultipleBehindPrs_LimitOne_SelectionVariesAcrossCallsRandom()
    {
        var selectedPrs = new HashSet<int>();

        for (int i = 0; i < 20; i++)
        {
            var (svc, provider, issues, _) = Create();
            provider.Setup(p => p.IsPullRequestBehindBaseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(PrMergeabilityStatus.Behind);
            provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);

            await ExecAsync(svc, provider, issues, [MakePr(10), MakePr(20)], limit: 1);

            // Capture which PR was selected this iteration
            var updated = provider.Invocations
                .Where(inv => inv.Method.Name == nameof(IRepositoryProvider.UpdatePullRequestBranchAsync))
                .Select(inv => (int)inv.Arguments[0]);
            selectedPrs.UnionWith(updated);
        }

        selectedPrs.Should().HaveCountGreaterThan(1,
            "with random selection, both PR #10 and PR #20 should be selected at least once across 20 trials");
        // NOTE: HaveCountGreaterThan(1) only confirms diversity, not fairness — a heavily biased shuffle (e.g.,
        // 19 of 20 selections always picking PR #10) would still pass. Consider asserting that each candidate
        // appears in at least some minimum fraction of trials if stricter distribution validation is needed.
    }

    // ── Concurrency limit = 2: both triggered ─────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_LimitTwo_BothEligiblePrsTriggered()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(10), MakePr(20)], limit: 2);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()), Times.Once);
        provider.Verify(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── In-flight UpToDate → evicted, slot freed ──────────────────────────────

    [Fact]
    public async Task ExecuteAsync_InFlightPrWithUpToDate_EvictedAndSlotFreed()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.UpToDate);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(20, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);

        await ExecAsync(svc, provider, issues, [MakePr(10), MakePr(20)]);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── In-flight Behind → evicted and re-selected same tick ─────────────────

    [Fact]
    public async Task ExecuteAsync_InFlightPrWithBehind_EvictedAndReselected()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(10)]);
        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ── In-flight absent from list → evicted ─────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_InFlightPrNotInList_Evicted()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        provider.Setup(p => p.IsPullRequestBehindBaseAsync(20, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);

        await ExecAsync(svc, provider, issues, [MakePr(20)]);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Mergeability checked once per PR ─────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_MergeabilityCheckedOncePerPr()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        provider.Invocations.Clear();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);

        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        provider.Verify(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── UpdateAsync throws → warning, PR stays in-flight ─────────────────────

    [Fact]
    public async Task ExecuteAsync_UpdateThrows_WarningLoggedAndPrStaysInFlight()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Network error"));

        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Blocked);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(20, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);

        await ExecAsync(svc, provider, issues, [MakePr(10), MakePr(20)]);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── GetActiveRunBranchesAsync throws → conservative skip ──────────────────

    [Fact]
    public async Task ExecuteAsync_GetActiveRunsThrows_SkipsBranchUpdatesConservatively()
    {
        var providerMock = new Mock<IRepositoryProvider>();
        var issuesMock = new Mock<IIssueProvider>();
        var runsMock = new Mock<IOrchestratorRunService>();
        // Simulate failure of the active-branch lookup (e.g. API down in Scheduler deployment).
        runsMock.Setup(r => r.GetActiveRunBranchesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("API down"));

        var svc = new HousekeepingService(runsMock.Object, Log.Logger);
        svc.FireAndForget = task => task;

        providerMock.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(PrMergeabilityStatus.Behind);

        // Act: must not throw
        var ex = await Record.ExceptionAsync(() =>
            svc.ExecuteAsync(providerMock.Object, RepoId, issuesMock.Object, IssueProviderId,
                [MakePr(1)], 1, false, 60, CancellationToken.None));

        ex.Should().BeNull("HousekeepingService must not propagate GetActiveRunBranchesAsync exceptions");

        // Assert: conservative fallback — branch update must be SKIPPED, not called.
        // Requirement: "If branch name data is unavailable, housekeeping MUST default to
        // conservative behavior: skip branch updates for PRs where branch state cannot be confirmed."
        providerMock.Verify(
            p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "UpdatePullRequestBranchAsync must not be called when active-run branch data is unavailable");
        // NOTE: This test only asserts the Step 6b (branch update) path of the conservative
        //   fallback. The activeRunBranchesUnavailable flag also gates the Step 6a rework-swap path
        //   (if (activeRunBranchesUnavailable || activeRunBranches.Contains(pr.BranchName))).
        //   A regression that removes the flag check from Step 6a while leaving Step 6b intact
        //   would pass all tests. Add a complementary test with a Conflicted PR that asserts
        //   the rework-swap (label change / TriggerReworkAsync) is also skipped when
        //   GetActiveRunBranchesAsync throws.
    }

    // ── Limit = 0 → clamped to 1 ─────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_LimitZero_ClampedToOne()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(10), MakePr(20)], limit: 0);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Empty list → eviction runs ────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_EmptyList_EvictsInFlightEntries()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(10)]);
        provider.Invocations.Clear();

        await ExecAsync(svc, provider, issues, []);

        provider.Verify(p => p.IsPullRequestBehindBaseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);

        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);

        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Conflicted → ExtractLinkedIssues called ───────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ConflictedPr_CallsExtractLinkedIssues()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Conflicted);
        provider.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string>)[]);

        await ExecAsync(svc, provider, issues, [MakePr(1)]);

        provider.Verify(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Conflicted + agent:done → label swap triggered ────────────────────────

    [Fact]
    public async Task ExecuteAsync_ConflictedPr_IssueWithAgentDone_SwapsToAgentNext()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Conflicted);
        provider.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string>)["42"]);
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.Done));
        issues.Setup(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), AgentLabels.Next, It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);
        issues.Setup(i => i.RemoveLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(1)]);

        issues.Verify(i => i.AddLabelAsync(
            It.Is<IssueIdentifier>(id => id.Value == "42"),
            AgentLabels.Next, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Conflicted + agent:next → no swap ────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ConflictedPr_IssueAlreadyAgentNext_SkipsSwap()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Conflicted);
        provider.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string>)["42"]);
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.Next));

        await ExecAsync(svc, provider, issues, [MakePr(1)]);

        issues.Verify(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Conflicted + agent:in-progress → no swap ─────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ConflictedPr_IssueAgentInProgress_SkipsSwap()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Conflicted);
        provider.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string>)["42"]);
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.InProgress));

        await ExecAsync(svc, provider, issues, [MakePr(1)]);

        issues.Verify(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Conflicted + branch active → no swap (guard fires before TriggerReworkAsync) ─

    [Fact]
    public async Task ExecuteAsync_ConflictedPr_BranchIsActive_SkipsReworkSwap()
    {
        var (svc, provider, issues, _) = Create(activeRuns: [ActiveRun("feature/auto-42-some-fix")]);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Conflicted);

        await ExecAsync(svc, provider, issues, [MakePr(1, branch: "feature/auto-42-some-fix")]);

        provider.Verify(p => p.ExtractLinkedIssuesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never, "TriggerReworkAsync must not be called when branch has an active run");
        issues.Verify(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "label swap must not be triggered when branch has an active run");
    }

    // ── Conflicted + different branch active → guard is branch-name–specific ─

    [Fact]
    public async Task ExecuteAsync_ConflictedPr_DifferentBranchIsActive_ProceedsWithReworkSwap()
    {
        // An active run on a *different* branch must not block the swap for the conflicted PR.
        // Guards a buggy "any active run → skip all" implementation.
        var (svc, provider, issues, _) = Create(activeRuns: [ActiveRun("feature/auto-99-other")]);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Conflicted);
        provider.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string>)["42"]);
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.Done));
        issues.Setup(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);
        issues.Setup(i => i.RemoveLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(1, branch: "feature/auto-42-some-fix")]);

        provider.Verify(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()),
            Times.Once, "TriggerReworkAsync must proceed when only a different branch is active");
        issues.Verify(i => i.AddLabelAsync(
            It.Is<IssueIdentifier>(id => id.Value == "42"),
            AgentLabels.Next, It.IsAny<CancellationToken>()), Times.Once,
            "label swap must be triggered when the PR's branch is not in active runs");
    }

    // ── Conflicted + empty linked issues → no crash ───────────────────────────

    [Fact]
    public async Task ExecuteAsync_ConflictedPr_NoLinkedIssues_NoSwap()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Conflicted);
        provider.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string>)[]);

        var ex = await Record.ExceptionAsync(() => ExecAsync(svc, provider, issues, [MakePr(1)]));

        ex.Should().BeNull();
        issues.Verify(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Conflicted ExtractLinkedIssues throws → continues ────────────────────

    [Fact]
    public async Task ExecuteAsync_ConflictedPr_ExtractLinkedIssuesThrows_ContinuesProcessing()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Conflicted);
        provider.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("API error"));
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(2, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(2, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        var ex = await Record.ExceptionAsync(
            () => ExecAsync(svc, provider, issues, [MakePr(1), MakePr(2)]));

        ex.Should().BeNull();
        provider.Verify(p => p.UpdatePullRequestBranchAsync(2, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Conflicted in-flight → evicted ────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ConflictedInFlightPr_IsEvicted()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Conflicted);
        provider.Setup(p => p.ExtractLinkedIssuesAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string>)[]);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(20, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);

        await ExecAsync(svc, provider, issues, [MakePr(10), MakePr(20)]);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── UpToDate PR → no rework swap ─────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_UpToDatePr_NoReworkSwap()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.UpToDate);

        await ExecAsync(svc, provider, issues, [MakePr(1)]);

        provider.Verify(p => p.ExtractLinkedIssuesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        issues.Verify(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ─── Stale branch cleanup ─────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_BranchCleanupDisabled_ListAgentBranchesNotCalled()
    {
        var (svc, provider, issues, _) = Create();

        await ExecAsync(svc, provider, issues, [], branchCleanup: false);

        provider.Verify(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_BranchCleanupEnabled_IntervalNotElapsed_ListNotCalled()
    {
        var (svc, provider, issues, _) = Create();
        var now = DateTimeOffset.UtcNow;
        svc.UtcNow = () => now;

        // First tick — seeds _lastCleanupAt
        provider.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string>)[]);

        await ExecAsync(svc, provider, issues, [], branchCleanup: true, intervalMinutes: 60);
        provider.Invocations.Clear();

        // Second tick — interval not elapsed (still the same time)
        await ExecAsync(svc, provider, issues, [], branchCleanup: true, intervalMinutes: 60);

        provider.Verify(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()), Times.Never,
            "cleanup interval has not elapsed — ListAgentBranchesAsync must not be called again");
    }

    [Fact]
    public async Task ExecuteAsync_BranchCleanupEnabled_IntervalElapsed_ListIsCalled()
    {
        var (svc, provider, issues, _) = Create();
        var t0 = DateTimeOffset.UtcNow;
        svc.UtcNow = () => t0;

        provider.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string>)[]);

        // First tick
        await ExecAsync(svc, provider, issues, [], branchCleanup: true, intervalMinutes: 60);
        provider.Invocations.Clear();

        // Advance time past the interval
        svc.UtcNow = () => t0.AddMinutes(61);
        await ExecAsync(svc, provider, issues, [], branchCleanup: true, intervalMinutes: 60);

        provider.Verify(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()), Times.Once,
            "interval elapsed — ListAgentBranchesAsync must be called");
    }

    [Fact]
    public async Task ExecuteAsync_BranchWithOpenPr_NotDeleted()
    {
        var (svc, provider, issues, _) = Create();
        var agentBranch = $"{PipelineConstants.BranchPrefix}99-some-feature";
        var openPr = MakePr(99, agentBranch);

        provider.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string>)[agentBranch]);
        // Return Behind so the mergeability step also runs
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(99, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.UpToDate);

        await ExecAsync(svc, provider, issues, [openPr], branchCleanup: true, intervalMinutes: 0);

        provider.Verify(p => p.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "branch has an open PR — must not be deleted");
    }

    [Fact]
    public async Task ExecuteAsync_BranchWithAgentNextIssue_NotDeleted()
    {
        var (svc, provider, issues, _) = Create();
        var agentBranch = $"{PipelineConstants.BranchPrefix}42-fix-login";

        provider.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string>)[agentBranch]);
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.Next));

        await ExecAsync(svc, provider, issues, [], branchCleanup: true, intervalMinutes: 0);

        provider.Verify(p => p.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "issue has agent:next — may create a new PR soon, must not delete");
    }

    [Fact]
    public async Task ExecuteAsync_BranchWithAgentDoneIssueAndNoPr_IsDeleted()
    {
        var (svc, provider, issues, _) = Create();
        var agentBranch = $"{PipelineConstants.BranchPrefix}42-fix-login";

        provider.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string>)[agentBranch]);
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.Done));
        provider.Setup(p => p.DeleteBranchAsync(agentBranch, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [], branchCleanup: true, intervalMinutes: 0);

        provider.Verify(p => p.DeleteBranchAsync(agentBranch, It.IsAny<CancellationToken>()), Times.Once,
            "no open PR + terminal issue label → branch must be deleted");
    }

    [Fact]
    public async Task ExecuteAsync_BranchDeleteThrows_ContinuesProcessingOtherBranches()
    {
        var (svc, provider, issues, _) = Create();
        var branch1 = $"{PipelineConstants.BranchPrefix}10-feat-a";
        var branch2 = $"{PipelineConstants.BranchPrefix}20-feat-b";

        provider.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string>)[branch1, branch2]);
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("10"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("10", AgentLabels.Done));
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("20"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("20", AgentLabels.Done));
        provider.Setup(p => p.DeleteBranchAsync(branch1, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("server error"));
        provider.Setup(p => p.DeleteBranchAsync(branch2, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        var ex = await Record.ExceptionAsync(
            () => ExecAsync(svc, provider, issues, [], branchCleanup: true, intervalMinutes: 0));

        ex.Should().BeNull("delete failure must be swallowed");
        provider.Verify(p => p.DeleteBranchAsync(branch2, It.IsAny<CancellationToken>()), Times.Once,
            "second branch must still be processed after first delete fails");
    }

    [Fact]
    public async Task ExecuteAsync_EmptyBranchList_NoBranchApiCalls()
    {
        var (svc, provider, issues, _) = Create();

        provider.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string>)[]);

        var ex = await Record.ExceptionAsync(
            () => ExecAsync(svc, provider, issues, [], branchCleanup: true, intervalMinutes: 0));

        ex.Should().BeNull();
        provider.Verify(p => p.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        issues.Verify(i => i.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ListAgentBranchesThrows_ContinuesWithoutCleanup()
    {
        var (svc, provider, issues, _) = Create();

        provider.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("API down"));

        var ex = await Record.ExceptionAsync(
            () => ExecAsync(svc, provider, issues, [], branchCleanup: true, intervalMinutes: 0));

        ex.Should().BeNull("ListAgentBranches failure must be swallowed");
        provider.Verify(p => p.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Conflicted + agent:epic-review → no rework swap ───────────────────────

    [Fact]
    public async Task ExecuteAsync_ConflictedPr_IssueWithEpicReview_SkipsReworkSwap()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Conflicted);
        provider.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string>)["42"]);
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.EpicReview));

        await ExecAsync(svc, provider, issues, [MakePr(1)]);

        // TODO: also verify RemoveLabelAsync is never called — SwapAsync invokes both Remove+Add, so a
        // regression that removes the label but correctly guards the add would not be caught here.
        // Pre-existing gap shared with ExecuteAsync_ConflictedPr_IssueAgentInProgress_SkipsSwap.
        issues.Verify(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "issue has agent:epic-review — awaiting human review, must not be re-queued for rework");
        issues.Verify(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()),
            Times.Once,
            "issue was fetched (guard fired after fetch, not before)");
    }

    // ── Branch with agent:epic-review issue → not deleted ────────────────────

    [Fact]
    public async Task ExecuteAsync_BranchWithEpicReviewIssue_NotDeleted()
    {
        var (svc, provider, issues, _) = Create();
        var agentBranch = $"{PipelineConstants.BranchPrefix}42-epic-decomp";

        provider.Setup(p => p.ListAgentBranchesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string>)[agentBranch]);
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.EpicReview));

        await ExecAsync(svc, provider, issues, [], branchCleanup: true, intervalMinutes: 0);

        provider.Verify(p => p.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "issue has agent:epic-review — awaiting human review, branch must not be deleted");
    }
}
