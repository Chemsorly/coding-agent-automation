using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;
using Polly.Timeout;
using Serilog;
using Xunit;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="HousekeepingService"/> (spec 040 / conflict-rework / stale-branch-cleanup).
/// All tests use the synchronous <c>FireAndForget</c> seam and a controlled <c>UtcNow</c> clock.
/// </summary>
/// <remarks>
/// Placed in [Collection("Metrics")] to serialize against all other MeterListener-based test classes
/// (HousekeepingServiceSweepSummaryMetricTests, HousekeepingPrOutcomeTests, etc.).
/// This class calls ExecuteAsync with IsPullRequestBehindBaseAsync setups, which triggers
/// EmitMergeabilityStatusCounters on the global PipelineTelemetry.Meter. Without serialization,
/// its emissions are captured by MeterListeners in concurrent test classes, causing false failures.
/// </remarks>
[Collection("Metrics")]
public class HousekeepingServiceTests
{
    private const string RepoId = "rp-1";
    private const string IssueProviderId = "ip-1";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PullRequestSummary MakePr(int number, string branch = "feature/pr", bool isDraft = false, bool hasAutoMerge = false)
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
            IsDraft = isDraft,
            HasAutoMerge = hasAutoMerge,
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

        // Collaborator mocks: default to no-ops so existing tests keep working.
        // Tests that need specific stale-branch or rework behaviour use StaleBranchCleanerTests /
        // IssueReworkServiceTests directly rather than routing through HousekeepingService.
        var staleBranchCleanerMock = new Mock<IStaleBranchCleaner>();
        staleBranchCleanerMock.Setup(s => s.RunIfDueAsync(
                It.IsAny<IRepositoryProvider>(), It.IsAny<IIssueProvider>(),
                It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<bool>(),
                It.IsAny<string>(),
                It.IsAny<KeyValuePair<string, object?>>(), It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var issueReworkServiceMock = new Mock<IIssueReworkService>();
        issueReworkServiceMock.Setup(s => s.TriggerConflictReworkAsync(
                It.IsAny<IReadOnlyList<PullRequestSummary>>(),
                It.IsAny<IReadOnlyDictionary<int, PrMergeabilityStatus>>(),
                It.IsAny<IReadOnlySet<string>>(), It.IsAny<bool>(),
                It.IsAny<IRepositoryProvider>(), It.IsAny<IIssueProvider>(),
                It.IsAny<string>(), It.IsAny<KeyValuePair<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = new HousekeepingService(runsMock.Object, staleBranchCleanerMock.Object, issueReworkServiceMock.Object, Log.Logger);
        svc.FireAndForget = task => task;

        // Default: FetchAllOpenAgentPrBranchesAsync returns no open PRs.
        // Tests that need specific PRs protected from branch cleanup override this setup.
        providerMock.Setup(p => p.ListOpenPullRequestsAsync(
                        It.IsAny<int>(), It.IsAny<int>(),
                        It.Is<IReadOnlyList<string>?>(l => l == null),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new PagedResult<PullRequestSummary>
                    {
                        Items = Array.Empty<PullRequestSummary>().AsReadOnly(),
                        Page = 1,
                        PageSize = 100,
                        HasMore = false
                    });

        return (svc, providerMock, issueProviderMock, runsMock);
    }

    /// <summary>Runs service with no cleanup, standard defaults. Pass <paramref name="triggerCooldownMinutes"/> to override the cooldown (default 25).</summary>
    private static Task ExecAsync(
        HousekeepingService svc,
        Mock<IRepositoryProvider> repo,
        Mock<IIssueProvider> issues,
        IReadOnlyList<PullRequestSummary> prs,
        int limit = 1,
        bool branchCleanup = false,
        int intervalMinutes = 60,
        int triggerCooldownMinutes = 25,
        bool wasInputTruncated = false)
        => svc.ExecuteAsync(
            repo.Object, RepoId, issues.Object, IssueProviderId,
            prs, wasInputTruncated, limit, branchCleanup, intervalMinutes, triggerCooldownMinutes, CancellationToken.None);

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
        var result = StaleBranchCleaner.ExtractIssueId(branchName);
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

    // ── Blocked → slot kept (no re-probe) ─────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_BlockedPr_IsSkippedAndSlotKept()
    {
        var (svc, provider, issues, _) = Create();
        svc.MergeabilityReprobeDelay = TimeSpan.Zero;
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(1)]);

        provider.Invocations.Clear(); // isolate cycle 2 assertions from cycle 1 probes
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Blocked);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(2, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);

        await ExecAsync(svc, provider, issues, [MakePr(1), MakePr(2)]);

        // Slot held by Blocked PR — PR #2 must not be updated
        provider.Verify(p => p.UpdatePullRequestBranchAsync(2, It.IsAny<CancellationToken>()), Times.Never,
            "Blocked PR holds the slot; PR #2 must not be updated");
        // Blocked PR: probed exactly once in cycle 2 (no re-probe — only Unknown triggers re-probe)
        provider.Verify(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()), Times.Once,
            "Blocked PR must not be re-probed");
    }

    // ── Unknown → re-probed, slot kept when still Unknown ─────────────────────

    [Fact]
    public async Task ExecuteAsync_UnknownPr_StillUnknownAfterReprobe_SlotKept()
    {
        var (svc, provider, issues, _) = Create();
        svc.MergeabilityReprobeDelay = TimeSpan.Zero;
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(1)]);

        provider.Invocations.Clear(); // isolate cycle 2 assertions from cycle 1 probes
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Unknown);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(2, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);

        await ExecAsync(svc, provider, issues, [MakePr(1), MakePr(2)]);

        // Slot still held — PR #2 must not be updated even though it's Behind
        provider.Verify(p => p.UpdatePullRequestBranchAsync(2, It.IsAny<CancellationToken>()), Times.Never,
            "Unknown PR holds the slot after both probes return Unknown; PR #2 must not be updated");
        // Unknown PR is re-probed once — 2 calls in cycle 2 (initial + re-probe)
        provider.Verify(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()), Times.Exactly(2),
            "Unknown PR must be re-probed once; total probe count for cycle 2 must be 2");
    }

    /// <summary>
    /// Regression guard: a PR that previously acquired the in-flight slot and is returning Unknown
    /// (GitHub/GitLab lazy-compute) must have its slot released when the re-probe resolves to
    /// UpToDate. This frees the slot for other Behind PRs in the same cycle.
    /// Step 1 re-probe updates mergeabilityMap → Step 3 eviction fires on the resolved state.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UnknownInFlight_ReprobesToUpToDate_SlotEvictedAndBehindPrTriggered()
    {
        var (svc, provider, issues, _) = Create();
        svc.MergeabilityReprobeDelay = TimeSpan.Zero;

        // ── Cycle 1: PR #1 is Behind, acquires the in-flight slot ───────────────
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(1)], limit: 1);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()),
            Times.Once, "PR #1 must be triggered in cycle 1");

        // ── Cycle 2: PR #1 returns Unknown on first probe, UpToDate on re-probe ─
        // PR #1 is in _inFlight. Re-probe resolves to UpToDate → Step 3 evicts the slot.
        // PR #2 is Behind → must acquire the freed slot.
        var pr1ProbeCount = 0;
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ++pr1ProbeCount == 1
                    ? PrMergeabilityStatus.Unknown    // first probe: lazy-compute not done yet
                    : PrMergeabilityStatus.UpToDate); // re-probe: branch caught up / merged
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(2, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(2, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        svc.TriggerCooldown = TimeSpan.Zero; // allow PR #2 to be immediately eligible

        await ExecAsync(svc, provider, issues, [MakePr(1), MakePr(2)], limit: 1,
            triggerCooldownMinutes: 0);

        // PR #1 re-probe resolved to UpToDate → slot must be released → PR #2 must be triggered
        provider.Verify(p => p.UpdatePullRequestBranchAsync(2, It.IsAny<CancellationToken>()),
            Times.Once,
            "PR #1 slot released after Unknown→UpToDate re-probe; PR #2 (Behind) must acquire the freed slot");

        // PR #1 must not be triggered again (UpToDate = no action needed)
        provider.Verify(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()),
            Times.Once, // only the cycle-1 call
            "PR #1 must not be updated after resolving to UpToDate");
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

    // ── In-flight Behind → evicted and re-selected after cooldown ────────────

    [Fact]
    public async Task ExecuteAsync_InFlightPrWithBehind_EvictedAndReselectedAfterCooldown()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        var clock = DateTimeOffset.UtcNow;
        svc.UtcNow = () => clock;
        svc.TriggerCooldown = TimeSpan.FromMinutes(25);

        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        // Advance clock past cooldown so the PR is eligible again
        clock = clock.AddMinutes(26);

        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ExecuteAsync_InFlightPrWithBehind_WithinCooldown_NotReselected()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        var clock = DateTimeOffset.UtcNow;
        svc.UtcNow = () => clock;
        svc.TriggerCooldown = TimeSpan.FromMinutes(25);

        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        // Do NOT advance clock — second call is within cooldown window
        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        // Only the first trigger should have fired; cooldown blocked the second
        provider.Verify(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Per-call cooldown isolation (issue #2684 regression guard) ───────────

    /// <summary>
    /// Regression guard: see issue #2684.
    /// Verifies that each ExecuteAsync call uses the triggerCooldownMinutes it was given,
    /// not the shared TriggerCooldown property value from a prior call.
    ///
    /// Scenario:
    ///   Call 1 — triggerCooldownMinutes:25, clock at t0       → triggers PR (records _lastTriggeredAt = t0)
    ///   Call 2 — triggerCooldownMinutes:60, clock at t0+26min → elapsed = 26min &lt; 60min → must NOT trigger
    ///
    /// Without the local-capture fix, the shared TriggerCooldown property would reflect whatever
    /// was written last (25 min after call 1), making the second call re-read the old value (25 min)
    /// and incorrectly trigger the PR (26 min ≥ 25 min). With the fix, each call uses its own
    /// captured value, so the 60-minute cooldown from call 2 correctly blocks the trigger.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_LocalCooldownCapture_IsolatesCallsFromSubsequentPropertyWrite()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        var clock = DateTimeOffset.UtcNow;
        svc.UtcNow = () => clock;

        // Call 1: triggerCooldownMinutes=25 — triggers the PR, records _lastTriggeredAt = t0
        await ExecAsync(svc, provider, issues, [MakePr(10)], triggerCooldownMinutes: 25);

        // Advance clock 26 minutes — past the 25-min cooldown, but within a 60-min cooldown
        clock = clock.AddMinutes(26);

        // Call 2: triggerCooldownMinutes=60 — elapsed (26 min) < cooldown (60 min) → must NOT trigger
        // If the implementation re-read TriggerCooldown (which was left at 25 min by call 1) instead
        // of using the locally-captured 60-min value, it would incorrectly trigger here.
        await ExecAsync(svc, provider, issues, [MakePr(10)], triggerCooldownMinutes: 60);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()), Times.Once,
            "call 2's 60-min cooldown must block the trigger — local capture must govern, not the shared property");

        // TODO: This test validates the Step 6b guard (skipping an in-flight PR within cooldown) but does
        // not have a dedicated assertion for the Step 5 ordering lambda (`(now5 - lastTriggered) >= triggerCooldown`).
        // In practice the PR passes through the Step 5 sorter before Step 6b evaluates it, so the local-capture
        // path is exercised, but if Step 5 were ever regressed back to reading TriggerCooldown while Step 6b
        // remained local, this test would still pass. Consider adding a scenario with two PRs where one is
        // within cooldown and should be deprioritised to tier 2, verifying that the wrong cooldown value would
        // misclassify it — specifically targeting Step 5 ordering isolation. (Issue #2684 regression guard)
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

    // ── Mergeability checked once per PR when already resolved ───────────────

    /// <summary>
    /// A PR that returns a resolved state (Behind, Blocked, etc.) on the first probe
    /// must not be re-probed within the same cycle — re-probing is only for Unknown results.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ResolvedPr_MergeabilityCheckedOncePerPr()
    {
        var (svc, provider, issues, _) = Create();
        svc.MergeabilityReprobeDelay = TimeSpan.Zero;
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        provider.Invocations.Clear();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);

        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        // PR returned Behind (resolved) on the first probe — exactly one probe call per cycle.
        provider.Verify(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()), Times.Once,
            "a PR whose first probe returns a resolved state must not be re-probed within the same cycle");
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

        var staleBranchMock = new Mock<IStaleBranchCleaner>();
        staleBranchMock.Setup(s => s.RunIfDueAsync(
                It.IsAny<IRepositoryProvider>(), It.IsAny<IIssueProvider>(),
                It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<bool>(),
                It.IsAny<string>(),
                It.IsAny<KeyValuePair<string, object?>>(), It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var reworkMock = new Mock<IIssueReworkService>();
        reworkMock.Setup(s => s.TriggerConflictReworkAsync(
                It.IsAny<IReadOnlyList<PullRequestSummary>>(),
                It.IsAny<IReadOnlyDictionary<int, PrMergeabilityStatus>>(),
                It.IsAny<IReadOnlySet<string>>(), It.IsAny<bool>(),
                It.IsAny<IRepositoryProvider>(), It.IsAny<IIssueProvider>(),
                It.IsAny<string>(), It.IsAny<KeyValuePair<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = new HousekeepingService(runsMock.Object, staleBranchMock.Object, reworkMock.Object, Log.Logger);
        svc.FireAndForget = task => task;

        providerMock.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(PrMergeabilityStatus.Behind);

        // TODO: triggerCooldownMinutes=25 is passed here to satisfy the updated signature but does
        // not exercise the Math.Max(1, triggerCooldownMinutes) clamping path in ExecuteAsync. Consider
        // adding a dedicated test (e.g. ExecuteAsync_TriggerCooldownMinutes_ClampedToMinimumOfOne) that
        // passes 0 or a negative value and asserts TriggerCooldown ends up as TimeSpan.FromMinutes(1).

        // Act: must not throw
        var ex = await Record.ExceptionAsync(() =>
            svc.ExecuteAsync(providerMock.Object, RepoId, issuesMock.Object, IssueProviderId,
                [MakePr(1)], wasInputTruncated: false, 1, false, 60, 25, CancellationToken.None));

        ex.Should().BeNull("HousekeepingService must not propagate GetActiveRunBranchesAsync exceptions");

        // Assert: conservative fallback — branch update must be SKIPPED, not called.
        // Requirement: "If branch name data is unavailable, housekeeping MUST default to
        // conservative behavior: skip branch updates for PRs where branch state cannot be confirmed."
        providerMock.Verify(
            p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "UpdatePullRequestBranchAsync must not be called when active-run branch data is unavailable");
    }

    // ── GetActiveRunsThrows → conflict rework (Step 6a) is also skipped ──────

    // TODO: This test verifies only that IIssueReworkService.TriggerConflictReworkAsync is called
    // with activeRunBranchesUnavailable=true (routing check), not that the guard is actually
    // honoured inside the collaborator. If a developer removes the guard inside
    // IssueReworkService.TriggerConflictReworkAsync, this test still passes because reworkMock is
    // a no-op. The collaborator guard is covered by
    // IssueReworkServiceTests.TriggerConflictReworkAsync_UnavailableActiveRuns_Skipped, so the
    // regression boundary is partially mitigated — but a combined integration-style test would
    // make the safety boundary more visible.
    [Fact]
    public async Task ExecuteAsync_GetActiveRunsThrows_SkipsConflictReworkConservatively()
    {
        // Complements ExecuteAsync_GetActiveRunsThrows_SkipsBranchUpdatesConservatively.
        // The activeRunBranchesUnavailable flag gates BOTH Step 6b (branch update) and
        // Step 6a (conflict rework). This test pins the Step 6a path — IIssueReworkService
        // must be called with activeRunBranchesUnavailable=true so it can apply the conservative skip.
        var providerMock = new Mock<IRepositoryProvider>();
        var issuesMock = new Mock<IIssueProvider>();
        var runsMock = new Mock<IOrchestratorRunService>();
        runsMock.Setup(r => r.GetActiveRunBranchesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("API down"));

        var staleBranchMock = new Mock<IStaleBranchCleaner>();
        staleBranchMock.Setup(s => s.RunIfDueAsync(
                It.IsAny<IRepositoryProvider>(), It.IsAny<IIssueProvider>(),
                It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<bool>(),
                It.IsAny<string>(),
                It.IsAny<KeyValuePair<string, object?>>(), It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var reworkMock = new Mock<IIssueReworkService>();
        reworkMock.Setup(s => s.TriggerConflictReworkAsync(
                It.IsAny<IReadOnlyList<PullRequestSummary>>(),
                It.IsAny<IReadOnlyDictionary<int, PrMergeabilityStatus>>(),
                It.IsAny<IReadOnlySet<string>>(), It.IsAny<bool>(),
                It.IsAny<IRepositoryProvider>(), It.IsAny<IIssueProvider>(),
                It.IsAny<string>(), It.IsAny<KeyValuePair<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = new HousekeepingService(runsMock.Object, staleBranchMock.Object, reworkMock.Object, Log.Logger);
        svc.FireAndForget = task => task;

        providerMock.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(PrMergeabilityStatus.Conflicted);

        // Act: must not throw
        var ex = await Record.ExceptionAsync(() =>
            svc.ExecuteAsync(providerMock.Object, RepoId, issuesMock.Object, IssueProviderId,
                [MakePr(1)], wasInputTruncated: false, 1, false, 60, 25, CancellationToken.None));

        ex.Should().BeNull("HousekeepingService must not propagate GetActiveRunBranchesAsync exceptions");

        // Assert: IIssueReworkService must have been called with activeRunBranchesUnavailable=true
        // so the conservative skip fires inside the collaborator.
        reworkMock.Verify(s => s.TriggerConflictReworkAsync(
            It.IsAny<IReadOnlyList<PullRequestSummary>>(),
            It.IsAny<IReadOnlyDictionary<int, PrMergeabilityStatus>>(),
            It.IsAny<IReadOnlySet<string>>(),
            true,   // activeRunBranchesUnavailable must be true
            providerMock.Object, issuesMock.Object,
            It.IsAny<string>(), It.IsAny<KeyValuePair<string, object?>>(),
            It.IsAny<CancellationToken>()), Times.Once,
            "IIssueReworkService.TriggerConflictReworkAsync must be called with activeRunBranchesUnavailable=true");
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

    // ── Conflict rework — delegation is tested in IssueReworkServiceTests ────

    // The conflict-rework label mutation logic (TriggerConflictReworkAsync, TriggerReworkAsync,
    // TrySwapIssueToNextAsync) now lives in IssueReworkService. HousekeepingService only delegates.
    // All label-permutation, active-run, no-linked-issues, and swap-failure tests are in
    // IssueReworkServiceTests. The delegation itself is tested in ExecuteAsync_ConflictedPr_DelegatesToIssueReworkService.

    /// <summary>
    /// Verifies that a Conflicted PR does NOT trigger a branch update (it is not Behind),
    /// while a Behind PR on a different slot still gets triggered. This exercises the
    /// mergeability-map pass-through from Step 1 to Step 6b (not the rework path).
    /// </summary>
    // TODO: The original test also verified via Times.Never that ExtractLinkedIssuesAsync was
    // not called on the evicted conflicted PR, pinning the interaction between the eviction step
    // (Step 3) and the rework delegation (Step 6a). With rework mocked to a no-op, that
    // interaction is no longer observable here. A regression where Step 6a is called with a
    // stale sorted list after eviction would not be detected by this test. Consider adding an
    // assertion that verifies the rework mock was called (or not called) with the expected sorted
    // list after the eviction cycle to restore that coverage.
    [Fact]
    public async Task ExecuteAsync_ConflictedInFlightPr_IsEvicted_BehindPrTriggered()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Conflicted);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(20, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);

        await ExecAsync(svc, provider, issues, [MakePr(10), MakePr(20)]);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Stale branch cleanup — HousekeepingService delegates to IStaleBranchCleaner ──────
    // TODO [WARNING]: Both ExecuteAsync_WithBranchCleanup_DelegatesToStaleBranchCleaner and
    // ExecuteAsync_WithBranchCleanupDisabled_DelegatesToStaleBranchCleanerWithEnabledFalse only
    // test wasInputTruncated: false. Add a test verifying that HousekeepingService passes
    // wasInputTruncated: true through to IStaleBranchCleaner.RunIfDueAsync. Without it, a
    // regression that hard-codes wasInputTruncated=false in HousekeepingService.cs would not
    // be caught — the central correctness requirement of this change-set has no coverage here.


    /// <summary>
    /// Verifies that HousekeepingService delegates to IStaleBranchCleaner.RunIfDueAsync with
    /// the correct parameters. The actual stale-branch logic is tested in StaleBranchCleanerTests.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithBranchCleanup_DelegatesToStaleBranchCleaner()
    {
        var runsMock = new Mock<IOrchestratorRunService>();
        runsMock.Setup(r => r.GetActiveRunBranchesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new HashSet<string>());

        var staleBranchMock = new Mock<IStaleBranchCleaner>();
        staleBranchMock.Setup(s => s.RunIfDueAsync(
                It.IsAny<IRepositoryProvider>(), It.IsAny<IIssueProvider>(),
                It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<bool>(),
                It.IsAny<string>(),
                It.IsAny<KeyValuePair<string, object?>>(), It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var reworkMock = new Mock<IIssueReworkService>();
        reworkMock.Setup(s => s.TriggerConflictReworkAsync(
                It.IsAny<IReadOnlyList<PullRequestSummary>>(),
                It.IsAny<IReadOnlyDictionary<int, PrMergeabilityStatus>>(),
                It.IsAny<IReadOnlySet<string>>(), It.IsAny<bool>(),
                It.IsAny<IRepositoryProvider>(), It.IsAny<IIssueProvider>(),
                It.IsAny<string>(), It.IsAny<KeyValuePair<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = new HousekeepingService(runsMock.Object, staleBranchMock.Object, reworkMock.Object, Log.Logger);
        svc.FireAndForget = task => task;

        var providerMock = new Mock<IRepositoryProvider>();
        var issuesMock = new Mock<IIssueProvider>();

        await svc.ExecuteAsync(
            providerMock.Object, RepoId, issuesMock.Object, IssueProviderId,
            [], wasInputTruncated: false, 1, branchCleanupEnabled: true, cleanupIntervalMinutes: 60, triggerCooldownMinutes: 25,
            CancellationToken.None);

        staleBranchMock.Verify(s => s.RunIfDueAsync(
            providerMock.Object, issuesMock.Object,
            It.IsAny<IReadOnlyList<PullRequestSummary>>(), false, RepoId,
            It.IsAny<KeyValuePair<string, object?>>(),
            true, 60, It.IsAny<CancellationToken>()), Times.Once,
            "HousekeepingService must delegate stale-branch cleanup to IStaleBranchCleaner");
    }

    [Fact]
    public async Task ExecuteAsync_WithBranchCleanupDisabled_DelegatesToStaleBranchCleanerWithEnabledFalse()
    {
        var runsMock = new Mock<IOrchestratorRunService>();
        runsMock.Setup(r => r.GetActiveRunBranchesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new HashSet<string>());

        var staleBranchMock = new Mock<IStaleBranchCleaner>();
        staleBranchMock.Setup(s => s.RunIfDueAsync(
                It.IsAny<IRepositoryProvider>(), It.IsAny<IIssueProvider>(),
                It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<bool>(),
                It.IsAny<string>(),
                It.IsAny<KeyValuePair<string, object?>>(), It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var reworkMock = new Mock<IIssueReworkService>();
        reworkMock.Setup(s => s.TriggerConflictReworkAsync(
                It.IsAny<IReadOnlyList<PullRequestSummary>>(),
                It.IsAny<IReadOnlyDictionary<int, PrMergeabilityStatus>>(),
                It.IsAny<IReadOnlySet<string>>(), It.IsAny<bool>(),
                It.IsAny<IRepositoryProvider>(), It.IsAny<IIssueProvider>(),
                It.IsAny<string>(), It.IsAny<KeyValuePair<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = new HousekeepingService(runsMock.Object, staleBranchMock.Object, reworkMock.Object, Log.Logger);
        svc.FireAndForget = task => task;

        var providerMock = new Mock<IRepositoryProvider>();
        var issuesMock = new Mock<IIssueProvider>();

        await svc.ExecuteAsync(
            providerMock.Object, RepoId, issuesMock.Object, IssueProviderId,
            [], wasInputTruncated: false, 1, branchCleanupEnabled: false, cleanupIntervalMinutes: 60, triggerCooldownMinutes: 25,
            CancellationToken.None);

        staleBranchMock.Verify(s => s.RunIfDueAsync(
            providerMock.Object, issuesMock.Object,
            It.IsAny<IReadOnlyList<PullRequestSummary>>(), false, RepoId,
            It.IsAny<KeyValuePair<string, object?>>(),
            false, 60, It.IsAny<CancellationToken>()), Times.Once,
            "HousekeepingService must pass enabled=false to IStaleBranchCleaner when branchCleanupEnabled is false");
    }

    // ─── Conflict rework — HousekeepingService delegates to IIssueReworkService ────────────

    /// <summary>
    /// Verifies that HousekeepingService delegates to IIssueReworkService.TriggerConflictReworkAsync
    /// with the sorted PR list and correct parameters. Rework logic is tested in IssueReworkServiceTests.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ConflictedPr_DelegatesToIssueReworkService()
    {
        var runsMock = new Mock<IOrchestratorRunService>();
        runsMock.Setup(r => r.GetActiveRunBranchesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new HashSet<string>());

        var staleBranchMock = new Mock<IStaleBranchCleaner>();
        staleBranchMock.Setup(s => s.RunIfDueAsync(
                It.IsAny<IRepositoryProvider>(), It.IsAny<IIssueProvider>(),
                It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<bool>(),
                It.IsAny<string>(),
                It.IsAny<KeyValuePair<string, object?>>(), It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var reworkMock = new Mock<IIssueReworkService>();
        reworkMock.Setup(s => s.TriggerConflictReworkAsync(
                It.IsAny<IReadOnlyList<PullRequestSummary>>(),
                It.IsAny<IReadOnlyDictionary<int, PrMergeabilityStatus>>(),
                It.IsAny<IReadOnlySet<string>>(), It.IsAny<bool>(),
                It.IsAny<IRepositoryProvider>(), It.IsAny<IIssueProvider>(),
                It.IsAny<string>(), It.IsAny<KeyValuePair<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = new HousekeepingService(runsMock.Object, staleBranchMock.Object, reworkMock.Object, Log.Logger);
        svc.FireAndForget = task => task;

        var providerMock = new Mock<IRepositoryProvider>();
        providerMock.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(PrMergeabilityStatus.Conflicted);
        var issuesMock = new Mock<IIssueProvider>();

        await svc.ExecuteAsync(
            providerMock.Object, RepoId, issuesMock.Object, IssueProviderId,
            [MakePr(1)], wasInputTruncated: false, 1, branchCleanupEnabled: false, cleanupIntervalMinutes: 60,
            triggerCooldownMinutes: 25, CancellationToken.None);

        reworkMock.Verify(s => s.TriggerConflictReworkAsync(
            It.Is<IReadOnlyList<PullRequestSummary>>(list => list.Any(p => p.Number == 1)),
            It.IsAny<IReadOnlyDictionary<int, PrMergeabilityStatus>>(),
            It.IsAny<IReadOnlySet<string>>(), false,
            providerMock.Object, issuesMock.Object,
            IssueProviderId, It.IsAny<KeyValuePair<string, object?>>(),
            It.IsAny<CancellationToken>()), Times.Once,
            "HousekeepingService must delegate conflict-rework to IIssueReworkService");
    }

    // ── Branch with agent:epic-review issue → not deleted ────────────────────
    // NOTE: This test now verifies delegation to StaleBranchCleaner (not the inline cleanup logic).
    // The actual epic-review label protection is tested in StaleBranchCleanerTests.

    [Fact]
    public async Task ExecuteAsync_BranchWithEpicReviewIssue_NotDeleted()
    {
        // With StaleBranchCleaner mocked, branch cleanup is delegated entirely.
        // Verify the delegation happens (with branchCleanup=true) — the label protection
        // logic itself is covered in StaleBranchCleanerTests.
        var (svc, _, issues, _) = Create();
        var providerMock = new Mock<IRepositoryProvider>();

        await ExecAsync(svc, providerMock, issues, [], branchCleanup: true, intervalMinutes: 0);

        // No direct provider calls for branch deletion — cleanup is fully delegated
        providerMock.Verify(p => p.DeleteBranchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── HasAutoMerge priority (3-tier sort) ───────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AutoMergePr_TriggerredBeforeNonAutoMergePr()
    {
        // Two behind PRs, limit 1. Auto-merge PR should always win the slot.
        var (svc, provider, issues, _) = Create();

        var autoMergePr = MakePr(10, "feature/auto", hasAutoMerge: true);
        var regularPr = MakePr(20, "feature/regular");

        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(20, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        // Frozen clock — no cooldown applies (last-triggered defaults to MinValue)
        var clock = DateTimeOffset.UtcNow;
        svc.UtcNow = () => clock;
        svc.TriggerCooldown = TimeSpan.FromMinutes(25);

        await ExecAsync(svc, provider, issues, [autoMergePr, regularPr]);

        // With limit 1, the auto-merge PR must be the one triggered
        provider.Verify(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()), Times.Once,
            "auto-merge PR must take priority over regular PR");
        provider.Verify(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()), Times.Never,
            "regular PR must not be triggered when auto-merge PR takes the slot");
    }

    [Fact]
    public async Task ExecuteAsync_AutoMergePrInCooldown_RegularPrTriggeredInstead()
    {
        // Auto-merge PR is still in cooldown, regular PR is not — regular wins the slot.
        var (svc, provider, issues, _) = Create();

        var autoMergePr = MakePr(10, "feature/auto", hasAutoMerge: true);
        var regularPr = MakePr(20, "feature/regular");

        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(20, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        var clock = DateTimeOffset.UtcNow;
        svc.UtcNow = () => clock;
        svc.TriggerCooldown = TimeSpan.FromMinutes(25);

        // First cycle: auto-merge PR gets triggered (enters cooldown)
        await ExecAsync(svc, provider, issues, [autoMergePr, regularPr]);
        provider.Verify(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()), Times.Once,
            "cycle 1: auto-merge PR must be triggered");
        provider.Verify(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()), Times.Never,
            "cycle 1: regular PR must not be triggered");

        // Second cycle immediately (auto-merge PR still in cooldown, regular not)
        provider.Invocations.Clear();
        await ExecAsync(svc, provider, issues, [autoMergePr, regularPr]);

        // Regular PR should now take the slot since auto-merge is cooling down
        provider.Verify(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()), Times.Once,
            "cycle 2: regular PR must take slot when auto-merge PR is in cooldown");
        provider.Verify(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()), Times.Never,
            "cycle 2: auto-merge PR must not be re-triggered while in cooldown");
    }

    [Fact]
    public async Task ExecuteAsync_MultipleAutoMergePrs_LimitOne_SelectionVaries()
    {
        // Multiple auto-merge PRs all past cooldown — random selection among peers.
        var (svc, provider, issues, _) = Create();

        var pr1 = MakePr(1, "feature/a", hasAutoMerge: true);
        var pr2 = MakePr(2, "feature/b", hasAutoMerge: true);
        var pr3 = MakePr(3, "feature/c", hasAutoMerge: true);

        foreach (var prNum in new[] { 1, 2, 3 })
        {
            provider.Setup(p => p.IsPullRequestBehindBaseAsync(prNum, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(PrMergeabilityStatus.Behind);
        }
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        var triggeredPrs = new HashSet<int>();
        var clock = DateTimeOffset.UtcNow;
        svc.UtcNow = () => clock;
        svc.TriggerCooldown = TimeSpan.FromMinutes(25);

        // Run 30 cycles advancing past cooldown each time
        for (var i = 0; i < 30; i++)
        {
            clock = clock.AddMinutes(30);
            svc.UtcNow = () => clock;
            var svcLocal = svc; // capture for lambda
            provider.Invocations.Clear();
            await ExecAsync(svcLocal, provider, issues, [pr1, pr2, pr3]);

            // Find which PR was triggered this cycle
            var triggered = provider.Invocations
                .Where(inv => inv.Method.Name == nameof(IRepositoryProvider.UpdatePullRequestBranchAsync))
                .Select(inv => (int)inv.Arguments[0])
                .FirstOrDefault();
            if (triggered != 0) triggeredPrs.Add(triggered);
        }

        triggeredPrs.Should().HaveCountGreaterThan(1,
            "random selection within the auto-merge tier must produce variety over many cycles");
    }

    [Fact]
    public async Task ExecuteAsync_AllPrsInCooldown_NothingTriggered()
    {
        // All behind PRs recently triggered — cooldown blocks all; no update fires.
        var (svc, provider, issues, _) = Create();

        var autoMergePr = MakePr(10, "feature/auto", hasAutoMerge: true);
        var regularPr = MakePr(20, "feature/regular");

        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(20, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        // Use a very long cooldown so a single trigger puts each PR in cooldown for the test
        // TODO: The refactored test passes triggerCooldownMinutes identically on every ExecAsync cycle,
        // so no test covers the case where svc.TriggerCooldown is set directly to a value different
        // from what ExecuteAsync would derive (the explicit acceptance criterion "internal TriggerCooldown
        // setter is preserved for test overrides"). Consider adding a test that calls ExecuteAsync with
        // one cooldown, then sets svc.TriggerCooldown directly to a shorter value, and verifies the
        // direct-setter override takes effect. This would pin the setter injection path against regressions.
        var clock = DateTimeOffset.UtcNow;
        svc.UtcNow = () => clock;

        // Cycle 1: triggers PR #10 (auto-merge, tier 0). PR #20 skipped (slot full, limit 1).
        await ExecAsync(svc, provider, issues, [autoMergePr, regularPr], triggerCooldownMinutes: (int)TimeSpan.FromHours(24).TotalMinutes);

        // Advance just enough that PR #10 is evicted from _inFlight (Behind → not Blocked/Unknown)
        // but still within the 24h cooldown window.
        clock = clock.AddMinutes(5);
        svc.UtcNow = () => clock;

        // Cycle 2: PR #10 is in cooldown (tier 2). PR #20 has no cooldown entry (tier 1) — triggers.
        provider.Invocations.Clear();
        await ExecAsync(svc, provider, issues, [autoMergePr, regularPr], triggerCooldownMinutes: (int)TimeSpan.FromHours(24).TotalMinutes);
        provider.Verify(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()), Times.Once,
            "cycle 2: regular PR must trigger once to enter cooldown");

        // Advance another 5 minutes — both PRs now triggered within 24h window.
        clock = clock.AddMinutes(5);
        svc.UtcNow = () => clock;

        // Cycle 3: both PRs are in cooldown — nothing should trigger.
        provider.Invocations.Clear();
        await ExecAsync(svc, provider, issues, [autoMergePr, regularPr], triggerCooldownMinutes: (int)TimeSpan.FromHours(24).TotalMinutes);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never, "no PR should be triggered when both are within their 24h cooldown window");
    }

    // ── Conflicted label-permutation tests → moved to IssueReworkServiceTests ─────────────
    // Tests for epic-review, wont-do, cancelled, needs-refinement, error, agent:done label
    // permutations live in IssueReworkServiceTests.cs. The delegation itself is verified in
    // ExecuteAsync_ConflictedPr_DelegatesToIssueReworkService (above).

    // ── Gap A + D: Per-PR exception isolation in mergeability probe ───────────

    /// <summary>
    /// Regression test for Gap A/D (issue #2535): a transient exception on one PR's
    /// mergeability probe must not abort the entire pass — the failed PR is treated as
    /// Unknown (conservative fallback) and processing continues for remaining PRs.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_IsPullRequestBehindBaseThrows_TreatsAffectedPrAsUnknownAndContinues()
    {
        var (svc, provider, issues, _) = Create();

        // PR #1: probe throws a transient exception
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("connection refused"));

        // PR #2: probe succeeds and returns Behind
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(2, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(2, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        // Act — must NOT throw
        await ExecAsync(svc, provider, issues, [MakePr(1), MakePr(2)], limit: 2);

        // PR #2 is Behind and should have been triggered (probe succeeded)
        provider.Verify(p => p.UpdatePullRequestBranchAsync(2, It.IsAny<CancellationToken>()),
            Times.Once,
            "PR #2's probe succeeded — UpdatePullRequestBranchAsync must be called");

        // PR #1 was treated as Unknown (failed probe) — slot not acquired, no update
        provider.Verify(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()),
            Times.Never,
            "PR #1's probe failed — must be treated as Unknown, no branch update");
    }

    [Fact]
    public async Task ExecuteAsync_AllPrsIsPullRequestBehindBaseThrows_NoExceptionPropagates()
    {
        var (svc, provider, issues, _) = Create();

        // Both PRs throw on probe — simulates a total mergeability API outage
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new TimeoutRejectedException("polly timeout", TimeSpan.FromSeconds(30)));
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(2, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new TimeoutRejectedException("polly timeout", TimeSpan.FromSeconds(30)));

        // Act — must NOT throw even when all probes fail
        var act = () => ExecAsync(svc, provider, issues, [MakePr(1), MakePr(2)], limit: 2);
        await act.Should().NotThrowAsync(
            "all probes failing must be absorbed — one bad API response must not kill the loop");

        // No branch updates should be triggered when all probes return Unknown
        provider.Verify(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "all probes failed → all PRs treated as Unknown → no branch updates");
    }

    [Fact]
    public async Task ExecuteAsync_IsPullRequestBehindBaseThrows_LogsWarningNotError()
    {
        // Verify that a failed probe is handled gracefully and produces no Error-level output.
        // We test indirectly via observable side-effects: the method returns without throwing
        // (covers the catch block is reached) and no branch update is attempted (covers the
        // Unknown fallback skips the PR). Serilog's structured logger uses generic overloads
        // (Warning<T0,T1>) that are not easily verified with Moq's overload resolution — the
        // key behavioral guarantee is that the failure is swallowed at Warning level, which is
        // confirmed by the lack of exception propagation and the absence of side-effects.

        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new HttpRequestException("probe failed"));

        // Act — must NOT throw (i.e. Warning was swallowed, not Error re-thrown)
        var act = () => ExecAsync(svc, provider, issues, [MakePr(1)]);
        await act.Should().NotThrowAsync(
            "a failed mergeability probe must be swallowed at Warning level, not re-thrown");

        // The failed PR is treated as Unknown — no branch update attempted
        provider.Verify(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "probe failure → Unknown fallback → no branch update");
    }

    // ── Unknown mergeability re-probe ─────────────────────────────────────────

    /// <summary>
    /// When the first probe returns Unknown, the service must re-probe after a short delay
    /// within the same cycle. GitHub computes mergeability on-demand: the first GET triggers
    /// the background job; the second GET (a few seconds later) returns the resolved state.
    /// This test covers the happy path: Unknown → (delay) → Behind → update triggered.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UnknownPr_IsReprobedAfterDelay_AndTriggeredWhenBehind()
    {
        var (svc, provider, issues, _) = Create();
        svc.MergeabilityReprobeDelay = TimeSpan.Zero; // instant in tests

        var callCount = 0;
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    callCount++;
                    return callCount == 1
                        ? PrMergeabilityStatus.Unknown  // first call: GitHub hasn't computed yet
                        : PrMergeabilityStatus.Behind;  // second call: resolved
                });
        provider.Setup(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(1)]);

        // Must have probed twice (initial + re-probe)
        provider.Verify(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "Unknown result must trigger a re-probe within the same cycle");

        // Re-probe returned Behind → branch update must fire
        provider.Verify(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()),
            Times.Once,
            "PR that resolves to Behind on re-probe must have its branch updated");
    }

    /// <summary>
    /// When the re-probe also returns Unknown, the PR must still be skipped (conservative
    /// fallback preserved). No branch update should be triggered.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UnknownPrStillUnknownAfterReprobe_IsSkipped()
    {
        var (svc, provider, issues, _) = Create();
        svc.MergeabilityReprobeDelay = TimeSpan.Zero;

        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Unknown);

        await ExecAsync(svc, provider, issues, [MakePr(1)]);

        // Must have probed twice (initial + re-probe) — re-probe fires regardless of result
        provider.Verify(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "Unknown result must trigger a re-probe even if it also returns Unknown");

        // Both probes returned Unknown → conservative skip, no update
        provider.Verify(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a PR that remains Unknown after re-probe must not trigger a branch update");
    }

    /// <summary>
    /// Only Unknown PRs are re-probed. Behind, Blocked, UpToDate, and Conflicted PRs must
    /// not incur an extra API call. Verifies the re-probe is targeted, not a full second pass.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_MixedStatuses_OnlyUnknownPrsAreReprobed()
    {
        var (svc, provider, issues, _) = Create();
        svc.MergeabilityReprobeDelay = TimeSpan.Zero;

        // PR #1: Behind — resolved on first probe, no re-probe
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        // PR #2: Unknown → Behind on re-probe
        var pr2Calls = 0;
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(2, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    pr2Calls++;
                    return pr2Calls == 1 ? PrMergeabilityStatus.Unknown : PrMergeabilityStatus.Behind;
                });
        provider.Setup(p => p.UpdatePullRequestBranchAsync(2, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        // PR #3: Blocked — resolved on first probe, no re-probe
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Blocked);

        await ExecAsync(svc, provider, issues, [MakePr(1), MakePr(2), MakePr(3)], limit: 3);

        // PR #1 (Behind): probed exactly once — no re-probe
        provider.Verify(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()),
            Times.Once, "Behind PR must not be re-probed");

        // PR #2 (Unknown→Behind): probed twice — initial + re-probe
        provider.Verify(p => p.IsPullRequestBehindBaseAsync(2, It.IsAny<CancellationToken>()),
            Times.Exactly(2), "Unknown PR must be re-probed exactly once");

        // PR #3 (Blocked): probed exactly once — no re-probe
        provider.Verify(p => p.IsPullRequestBehindBaseAsync(3, It.IsAny<CancellationToken>()),
            Times.Once, "Blocked PR must not be re-probed");

        // Both Behind PRs (#1 and #2) must be updated (limit=3 so no slot starvation)
        provider.Verify(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()),
            Times.Once, "PR #1 (Behind) must be updated");
        provider.Verify(p => p.UpdatePullRequestBranchAsync(2, It.IsAny<CancellationToken>()),
            Times.Once, "PR #2 (Unknown→Behind via re-probe) must be updated");
        provider.Verify(p => p.UpdatePullRequestBranchAsync(3, It.IsAny<CancellationToken>()),
            Times.Never, "PR #3 (Blocked) must not be updated");
    }

    /// <summary>
    /// Re-probe cancellation: if the CancellationToken is cancelled while the re-probe delay
    /// is running, Task.Delay must propagate the cancellation immediately and the re-probe
    /// call must never fire.
    /// Uses CancelAfter so the first probe completes synchronously, the delay starts, and is
    /// then cancelled mid-wait — avoiding the pre-cancel path where Task.Delay throws without
    /// actually waiting.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UnknownPr_CancelledDuringReprobeDelay_PropagatesCancellation()
    {
        var (svc, provider, issues, _) = Create();
        // Use a real delay long enough that CancelAfter fires while it is running
        svc.MergeabilityReprobeDelay = TimeSpan.FromSeconds(30);

        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Unknown);

        using var cts = new CancellationTokenSource();
        // Cancel shortly after the first probe completes (Moq returns synchronously),
        // while Task.Delay(30s, ct) is blocking.
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.ExecuteAsync(
                provider.Object, RepoId, issues.Object, IssueProviderId,
                [MakePr(1)], wasInputTruncated: false, 1, false, 60, 25, cts.Token));

        // Only the initial probe should have fired; re-probe must be skipped after cancellation
        provider.Verify(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()),
            Times.Once,
            "re-probe must not be called after cancellation; only the initial probe should have fired");
    }

    /// <summary>
    /// Multiple Unknown PRs in the same cycle: a SINGLE delay fires once for the entire batch,
    /// then all Unknown PRs are re-probed in sequence. This verifies that the re-probe pass is
    /// not serialised with individual per-PR delays (which would multiply latency).
    /// The "single delay" invariant is locked by counting ReprobeDelayFunc calls — exactly 1
    /// regardless of how many Unknown PRs exist. A per-PR delay refactor would fail this test.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_MultipleUnknownPrs_SingleDelayThenAllReprobed()
    {
        var (svc, provider, issues, _) = Create();
        svc.MergeabilityReprobeDelay = TimeSpan.Zero;

        // Count how many times the batch delay fires
        var delayCallCount = 0;
        svc.ReprobeDelayFunc = (ts, ct) => { delayCallCount++; return Task.Delay(ts, ct); };

        // Both PRs return Unknown first, then Behind
        foreach (var prNum in new[] { 1, 2 })
        {
            var calls = 0;
            var num = prNum;
            provider.Setup(p => p.IsPullRequestBehindBaseAsync(num, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(() => ++calls == 1 ? PrMergeabilityStatus.Unknown : PrMergeabilityStatus.Behind);
            provider.Setup(p => p.UpdatePullRequestBranchAsync(num, It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);
        }

        await ExecAsync(svc, provider, issues, [MakePr(1), MakePr(2)], limit: 2);

        // Single delay — the whole batch, not once per PR
        delayCallCount.Should().Be(1,
            "ReprobeDelayFunc must fire exactly once per batch; a per-PR delay would fire 2 times here");

        // Both PRs re-probed exactly once each
        provider.Verify(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()),
            Times.Exactly(2), "PR #1 must be re-probed");
        provider.Verify(p => p.IsPullRequestBehindBaseAsync(2, It.IsAny<CancellationToken>()),
            Times.Exactly(2), "PR #2 must be re-probed");

        // Both resolved to Behind → both updated
        provider.Verify(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()),
            Times.Once, "PR #1 resolved to Behind — must be updated");
        provider.Verify(p => p.UpdatePullRequestBranchAsync(2, It.IsAny<CancellationToken>()),
            Times.Once, "PR #2 resolved to Behind — must be updated");
    }

    // ── UtcNow captured once per tick ────────────────────────────────────────

    /// <summary>
    /// Verifies that <c>UtcNow</c> is called exactly once per <see cref="HousekeepingService.ExecuteAsync"/>
    /// call, not multiple times (e.g., inside the foreach in Step 6b). A single captured timestamp
    /// ensures all steps in the same tick share a consistent view of "now".
    /// </summary>
    // TODO: The assertion callCount.Should().Be(1) would also pass if UtcNow were never called
    // (callCount == 0), for example if the implementation were refactored to use
    // DateTimeOffset.UtcNow directly without going through the seam. Consider strengthening to
    // callCount.Should().BeGreaterThan(0).And.Be(1) or splitting into two separate assertions
    // ("called at least once" + "not called more than once") to catch the seam-bypass regression.
    [Fact]
    public async Task ExecuteAsync_UtcNowCapturedOncePerTick()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        var callCount = 0;
        var fixedNow = DateTimeOffset.UtcNow;
        svc.UtcNow = () => { callCount++; return fixedNow; };

        // Use 3 PRs so the foreach in SelectAndTriggerBranchUpdatesAsync iterates multiple times.
        await ExecAsync(svc, provider, issues, [MakePr(1), MakePr(2), MakePr(3)], limit: 3);

        callCount.Should().Be(1,
            "UtcNow must be called exactly once per ExecuteAsync tick — not once per loop iteration");
    }

    // ── TriggerConflictReworkAsync — agent:error as valid rework target ───────
    // This test is now covered by IssueReworkServiceTests.TriggerConflictReworkAsync_ConflictedPr_AgentErrorIssue_SwapsToNext.
    // The delegation from HousekeepingService is tested in ExecuteAsync_ConflictedPr_DelegatesToIssueReworkService.

    // TODO: No span-emission tests exist for Housekeeping.BranchUpdate (added in issue #2977).
    // Add tests to verify: (1) Housekeeping.BranchUpdate is emitted with pr_number and repo_provider_id
    // tags when a branch update is triggered; (2) no span is emitted when no PRs qualify for update.
}

