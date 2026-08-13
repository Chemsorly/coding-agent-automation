using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
using Moq;
using Serilog;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="AutoUpdatePrBranchService"/> (spec 040, task 6.2).
/// All tests written BEFORE implementation — must fail until the service is implemented.
/// </summary>
public class AutoUpdatePrBranchServiceTests
{
    private const string RepoId = "rp-1";

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

    private static (AutoUpdatePrBranchService Service,
                    Mock<IRepositoryProvider> ProviderMock,
                    Mock<IOrchestratorRunService> RunsMock)
        Create(IEnumerable<PipelineRun>? activeRuns = null)
    {
        var providerMock = new Mock<IRepositoryProvider>();
        var runsMock = new Mock<IOrchestratorRunService>();
        runsMock.Setup(r => r.GetActiveRuns())
                .Returns((activeRuns ?? Enumerable.Empty<PipelineRun>()).ToList().AsReadOnly());

        var svc = new AutoUpdatePrBranchService(runsMock.Object, Log.Logger);
        // Override fire-and-forget to await synchronously — eliminates Task.Delay flakiness in tests.
        svc.FireAndForget = task => task;
        return (svc, providerMock, runsMock);
    }

    private static PipelineRun ActiveRun(string branch) => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = new IssueIdentifier("1"),
        IssueTitle = "Test",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        BranchName = branch
    };

    // ── Draft PRs are skipped ─────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_DraftPr_IsSkipped()
    {
        var (svc, provider, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(1, isDraft: true)], 1, CancellationToken.None);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Active rework branch is excluded ─────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ActiveReworkBranch_IsSkipped()
    {
        var (svc, provider, _) = Create(activeRuns: [ActiveRun("feature/pr-1")]);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(1, branch: "feature/pr-1")], 1, CancellationToken.None);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Null mergeability → skipped ───────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NullMergeability_IsSkipped()
    {
        var (svc, provider, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync((bool?)null);

        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(1)], 1, CancellationToken.None);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── False mergeability → skipped ─────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_FalseMergeability_IsSkipped()
    {
        var (svc, provider, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);

        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(1)], 1, CancellationToken.None);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── True mergeability → update triggered, PR added to in-flight ──────────

    [Fact]
    public async Task ExecuteAsync_TrueMergeability_TriggersUpdate()
    {
        var (svc, provider, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(1)], 1, CancellationToken.None);

        // Give fire-and-forget task time to complete — not needed with synchronous FireAndForget seam
        provider.Verify(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Concurrency limit = 1: only first (lowest number) PR triggered ────────

    [Fact]
    public async Task ExecuteAsync_LimitOne_OnlyLowestNumberTriggered()
    {
        var (svc, provider, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(20), MakePr(10)], 1, CancellationToken.None);

        await Task.Delay(50); // removed — FireAndForget seam makes this unnecessary
        provider.Verify(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()), Times.Once);
        provider.Verify(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Concurrency limit = 2: both eligible PRs triggered ────────────────────

    [Fact]
    public async Task ExecuteAsync_LimitTwo_BothEligiblePrsTriggered()
    {
        var (svc, provider, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(10), MakePr(20)], 2, CancellationToken.None);

        await Task.Delay(50); // removed — FireAndForget seam makes this unnecessary
        provider.Verify(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()), Times.Once);
        provider.Verify(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── In-flight PR is kept while mergeability is null ──────────────────────

    [Fact]
    public async Task ExecuteAsync_InFlightPrWithNullMergeability_KeptInSet()
    {
        var (svc, provider, _) = Create();
        // First tick: trigger update on PR #10
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(10)], 1, CancellationToken.None);
        await Task.Delay(50); // removed — FireAndForget seam makes this unnecessary

        // Second tick: mergeability is null (CI running) → keep in set, slot still occupied
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync((bool?)null);

        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(10), MakePr(20)], 1, CancellationToken.None);
        await Task.Delay(50); // removed — FireAndForget seam makes this unnecessary

        // PR #20 should NOT be triggered because #10 still occupies the slot
        provider.Verify(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── In-flight PR with false mergeability → evicted, slot freed ───────────

    [Fact]
    public async Task ExecuteAsync_InFlightPrWithFalseMergeability_EvictedAndSlotFreed()
    {
        var (svc, provider, _) = Create();
        // First tick: trigger update on PR #10
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(10)], 1, CancellationToken.None);
        await Task.Delay(50); // removed — FireAndForget seam makes this unnecessary

        // Second tick: #10 CI done (false) → evict, free slot → #20 triggered
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(20, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(10), MakePr(20)], 1, CancellationToken.None);
        await Task.Delay(50); // removed — FireAndForget seam makes this unnecessary

        provider.Verify(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── In-flight PR with true mergeability → evicted, re-selected same tick ─

    [Fact]
    public async Task ExecuteAsync_InFlightPrWithTrueMergeability_EvictedAndReselected()
    {
        var (svc, provider, _) = Create();
        // First tick: trigger update on PR #10
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(10)], 1, CancellationToken.None);
        await Task.Delay(50); // removed — FireAndForget seam makes this unnecessary

        // Second tick: base moved again (true) → evict AND re-trigger in the same tick
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(10)], 1, CancellationToken.None);
        await Task.Delay(50); // removed — FireAndForget seam makes this unnecessary

        // Total calls to UpdatePullRequestBranchAsync = 2 (once per tick)
        provider.Verify(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ── In-flight PR absent from agentDonePrs → evicted ──────────────────────

    [Fact]
    public async Task ExecuteAsync_InFlightPrNotInList_Evicted()
    {
        var (svc, provider, _) = Create();
        // First tick: trigger #10
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(10)], 1, CancellationToken.None);
        await Task.Delay(50); // removed — FireAndForget seam makes this unnecessary

        // Second tick: #10 has merged — not in list anymore, new PR #20 present
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(20, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(20)], 1, CancellationToken.None);
        await Task.Delay(50); // removed — FireAndForget seam makes this unnecessary

        // #10 evicted → slot freed → #20 triggered
        provider.Verify(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── After eviction, freed slot allows new PR ──────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AfterEviction_FreedSlotAllowsNewPr()
    {
        var (svc, provider, _) = Create();
        // Tick 1: PR #10 behind → triggered, fills slot
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(10), MakePr(20)], 1, CancellationToken.None);
        await Task.Delay(50); // removed — FireAndForget seam makes this unnecessary
        provider.Verify(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()), Times.Once);

        // Tick 2: #10 CI done (false) → evicted; #20 now eligible
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(false);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(20, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(10), MakePr(20)], 1, CancellationToken.None);
        await Task.Delay(50); // removed — FireAndForget seam makes this unnecessary

        provider.Verify(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── IsPullRequestBehindBaseAsync called once per PR (result reused) ───────

    [Fact]
    public async Task ExecuteAsync_MergeabilityCheckedOncePerPr()
    {
        var (svc, provider, _) = Create();
        // PR #10 is both in-flight AND in the candidate list → checked once for eviction,
        // same result reused for selection (should not be called twice).
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        // Tick 1: trigger
        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(10)], 1, CancellationToken.None);
        await Task.Delay(50); // removed — FireAndForget seam makes this unnecessary

        // Reset the call count
        provider.Invocations.Clear();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

        // Tick 2: #10 is in-flight and in list → exactly ONE IsPullRequestBehindBaseAsync call
        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(10)], 1, CancellationToken.None);
        await Task.Delay(50); // removed — FireAndForget seam makes this unnecessary

        provider.Verify(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── UpdateAsync exception → warning logged, PR stays in-flight ───────────

    [Fact]
    public async Task ExecuteAsync_UpdateThrows_WarningLoggedAndPrStaysInFlight()
    {
        var (svc, provider, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Network error"));
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(20, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(10)], 1, CancellationToken.None);
        await Task.Delay(100); // removed — FireAndForget seam makes this unnecessary

        // Next tick: #10 still in-flight (null mergeability), slot occupied, #20 blocked
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync((bool?)null);

        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(10), MakePr(20)], 1, CancellationToken.None);
        await Task.Delay(50); // removed — FireAndForget seam makes this unnecessary

        provider.Verify(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── GetActiveRuns() throws → warning, processing continues ───────────────

    [Fact]
    public async Task ExecuteAsync_GetActiveRunsThrows_ContinuesWithEmptySet()
    {
        var providerMock = new Mock<IRepositoryProvider>();
        var runsMock = new Mock<IOrchestratorRunService>();
        runsMock.Setup(r => r.GetActiveRuns()).Throws(new InvalidOperationException("DB down"));

        var svc = new AutoUpdatePrBranchService(runsMock.Object, Log.Logger);
        svc.FireAndForget = task => task;

        providerMock.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(true);
        providerMock.Setup(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);

        // Should not throw; should still trigger the update (active set treated as empty)
        await svc.ExecuteAsync(providerMock.Object, RepoId, [MakePr(1)], 1, CancellationToken.None);
        await Task.Delay(50); // removed — FireAndForget seam makes this unnecessary

        providerMock.Verify(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Limit = 0 → clamped to 1 ─────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_LimitZero_ClampedToOne()
    {
        var (svc, provider, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(10), MakePr(20)], 0, CancellationToken.None);
        await Task.Delay(50); // removed — FireAndForget seam makes this unnecessary

        // Only one update should fire (limit clamped to 1)
        provider.Verify(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Empty agentDonePrs: eviction still runs ───────────────────────────────

    [Fact]
    public async Task ExecuteAsync_EmptyList_EvictsInFlightEntries()
    {
        var (svc, provider, _) = Create();
        // Tick 1: trigger #10
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(10)], 1, CancellationToken.None);
        await Task.Delay(50); // removed — FireAndForget seam makes this unnecessary

        // Tick 2: empty list → #10 not in currentPrNumbers → evicted (no IsPullRequestBehindBaseAsync called for it)
        provider.Invocations.Clear();

        await svc.ExecuteAsync(provider.Object, RepoId, [], 1, CancellationToken.None);

        provider.Verify(p => p.IsPullRequestBehindBaseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never,
            "No PRs in list means no API calls needed — eviction by absence");

        // Tick 3: PR #10 again (as if it re-appeared) — slot should be free now
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await svc.ExecuteAsync(provider.Object, RepoId, [MakePr(10)], 1, CancellationToken.None);
        await Task.Delay(50); // removed — FireAndForget seam makes this unnecessary

        provider.Verify(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()), Times.Once);
    }
}
