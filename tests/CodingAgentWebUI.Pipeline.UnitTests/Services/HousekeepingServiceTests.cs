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
/// Unit tests for <see cref="HousekeepingService"/> (spec 040 / conflict-rework extension).
/// All tests use the synchronous <c>FireAndForget</c> seam to eliminate non-determinism.
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
        runsMock.Setup(r => r.GetActiveRuns())
                .Returns((activeRuns ?? Enumerable.Empty<PipelineRun>()).ToList().AsReadOnly());

        var svc = new HousekeepingService(runsMock.Object, Log.Logger);
        // Override fire-and-forget to await synchronously — eliminates flakiness in tests.
        svc.FireAndForget = task => task;
        return (svc, providerMock, issueProviderMock, runsMock);
    }

    /// <summary>Executes the service with standard test defaults.</summary>
    private static Task ExecAsync(
        HousekeepingService svc,
        Mock<IRepositoryProvider> repo,
        Mock<IIssueProvider> issues,
        IReadOnlyList<PullRequestSummary> prs,
        int limit = 1)
        => svc.ExecuteAsync(repo.Object, RepoId, issues.Object, IssueProviderId, prs, limit, CancellationToken.None);

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

    // ── Blocked / Unknown mergeability → skipped (slot stays occupied) ────────

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

        // Tick 1: trigger #1, fills slot
        await ExecAsync(svc, provider, issues, [MakePr(1)]);

        // Tick 2: CI still running → slot stays occupied, PR #2 blocked
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(status);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(2, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);

        await ExecAsync(svc, provider, issues, [MakePr(1), MakePr(2)]);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(2, It.IsAny<CancellationToken>()), Times.Never,
            $"{status} should keep slot occupied — PR #2 must not be triggered");
    }

    // ── UpToDate mergeability → skipped (no update needed) ───────────────────

    [Fact]
    public async Task ExecuteAsync_UpToDate_IsSkipped()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.UpToDate);

        await ExecAsync(svc, provider, issues, [MakePr(1)]);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Behind → update triggered, PR added to in-flight ─────────────────────

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

    // ── Concurrency limit = 1: only first (lowest number) PR triggered ────────

    [Fact]
    public async Task ExecuteAsync_LimitOne_OnlyLowestNumberTriggered()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(20), MakePr(10)], limit: 1);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()), Times.Once);
        provider.Verify(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Concurrency limit = 2: both eligible PRs triggered ────────────────────

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

    // ── In-flight PR with UpToDate → evicted, slot freed ─────────────────────

    [Fact]
    public async Task ExecuteAsync_InFlightPrWithUpToDate_EvictedAndSlotFreed()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        // Second tick: #10 CI done (UpToDate) → evict, free slot → #20 triggered
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.UpToDate);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(20, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(10), MakePr(20)]);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── In-flight PR with Behind → evicted, re-selected same tick ────────────

    [Fact]
    public async Task ExecuteAsync_InFlightPrWithBehind_EvictedAndReselected()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        // Second tick: base moved again (Behind) → evict AND re-trigger in the same tick
        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        // Total calls = 2 (once per tick)
        provider.Verify(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ── In-flight PR absent from agentDonePrs → evicted ──────────────────────

    [Fact]
    public async Task ExecuteAsync_InFlightPrNotInList_Evicted()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        // Second tick: #10 merged — not in list; #20 now eligible
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(20, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);

        await ExecAsync(svc, provider, issues, [MakePr(20)]);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── IsPullRequestBehindBaseAsync called once per PR (result reused) ───────

    [Fact]
    public async Task ExecuteAsync_MergeabilityCheckedOncePerPr()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        // Tick 1
        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        provider.Invocations.Clear();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);

        // Tick 2: #10 in-flight AND in list → exactly ONE IsPullRequestBehindBaseAsync call
        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        provider.Verify(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── UpdateAsync exception → warning logged, PR stays in-flight ───────────

    [Fact]
    public async Task ExecuteAsync_UpdateThrows_WarningLoggedAndPrStaysInFlight()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("Network error"));

        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        // Next tick: #10 still in-flight (Blocked), slot occupied, #20 blocked
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Blocked);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(20, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);

        await ExecAsync(svc, provider, issues, [MakePr(10), MakePr(20)]);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── GetActiveRuns() throws → warning, processing continues ───────────────

    [Fact]
    public async Task ExecuteAsync_GetActiveRunsThrows_ContinuesWithEmptySet()
    {
        var providerMock = new Mock<IRepositoryProvider>();
        var issuesMock = new Mock<IIssueProvider>();
        var runsMock = new Mock<IOrchestratorRunService>();
        runsMock.Setup(r => r.GetActiveRuns()).Throws(new InvalidOperationException("DB down"));

        var svc = new HousekeepingService(runsMock.Object, Log.Logger);
        svc.FireAndForget = task => task;

        providerMock.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(PrMergeabilityStatus.Behind);
        providerMock.Setup(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);

        await svc.ExecuteAsync(providerMock.Object, RepoId, issuesMock.Object, IssueProviderId,
            [MakePr(1)], 1, CancellationToken.None);

        providerMock.Verify(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()), Times.Once);
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

    // ── Empty agentDonePrs: eviction still runs ───────────────────────────────

    [Fact]
    public async Task ExecuteAsync_EmptyList_EvictsInFlightEntries()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        // Tick 2: empty list → #10 not in currentPrNumbers → evicted
        provider.Invocations.Clear();

        await ExecAsync(svc, provider, issues, []);

        provider.Verify(p => p.IsPullRequestBehindBaseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never,
            "No PRs in list means no API calls needed — eviction by absence");

        // Tick 3: PR #10 again — slot should be free now
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);

        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Conflicted PR: ExtractLinkedIssuesAsync is called ────────────────────

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

    // ── Conflicted PR with agent:done issue → label swap triggered ────────────

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
            AgentLabels.Next,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Conflicted PR with agent:next issue → label swap NOT triggered ────────

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

    // ── Conflicted PR with agent:in-progress issue → label swap NOT triggered ─

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

    // ── Conflicted PR with empty linked issues → no swap, no crash ───────────

    [Fact]
    public async Task ExecuteAsync_ConflictedPr_NoLinkedIssues_NoSwap()
    {
        var (svc, provider, issues, _) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Conflicted);
        provider.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string>)[]);

        var ex = await Record.ExceptionAsync(() => ExecAsync(svc, provider, issues, [MakePr(1)]));

        ex.Should().BeNull("no linked issues is a valid case — service must not throw");
        issues.Verify(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Conflicted PR: ExtractLinkedIssuesAsync throws → warning, continues ──

    [Fact]
    public async Task ExecuteAsync_ConflictedPr_ExtractLinkedIssuesThrows_ContinuesProcessing()
    {
        var (svc, provider, issues, _) = Create();
        // PR #1 conflicted, extract throws
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Conflicted);
        provider.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("API error"));
        // PR #2 behind — should still be processed
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(2, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(2, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        var ex = await Record.ExceptionAsync(
            () => ExecAsync(svc, provider, issues, [MakePr(1), MakePr(2)]));

        ex.Should().BeNull("ExtractLinkedIssues failure must be swallowed, not propagated");
        provider.Verify(p => p.UpdatePullRequestBranchAsync(2, It.IsAny<CancellationToken>()), Times.Once,
            "processing must continue for subsequent PRs after the extract failure");
    }

    // ── Conflicted PR → evicted from in-flight set (slot freed) ──────────────

    [Fact]
    public async Task ExecuteAsync_ConflictedInFlightPr_IsEvicted()
    {
        var (svc, provider, issues, _) = Create();
        // Tick 1: trigger #10 (Behind)
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(10, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(10)]);

        // Tick 2: #10 now Conflicted → evicted; slot freed → #20 (Behind) triggered
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Conflicted);
        provider.Setup(p => p.ExtractLinkedIssuesAsync(10, It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<string>)[]);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(20, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);

        await ExecAsync(svc, provider, issues, [MakePr(10), MakePr(20)]);

        provider.Verify(p => p.UpdatePullRequestBranchAsync(20, It.IsAny<CancellationToken>()), Times.Once,
            "Conflicted eviction must free the slot so PR #20 can be triggered");
    }

    // ── UpToDate PR → no rework swap (not conflicted) ─────────────────────────

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
}
