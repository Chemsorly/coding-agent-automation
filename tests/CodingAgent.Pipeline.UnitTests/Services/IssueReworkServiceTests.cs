using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;
using Serilog;
using Xunit;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="IssueReworkService"/> extracted from HousekeepingService.
/// Tests exercise <see cref="IssueReworkService.TriggerConflictReworkAsync"/> directly.
/// </summary>
public class IssueReworkServiceTests
{
    private const string RepoId = "rp-rework";
    private const string IssueProviderId = "ip-rework";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IssueReworkService Create() => new(Log.Logger);

    private static PullRequestSummary MakePr(int number, string branch = "feature/pr-42", bool isDraft = false)
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
        };

    private static IssueDetail MakeIssue(string id, params string[] labels) => new()
    {
        Identifier = id,
        Title = $"Issue {id}",
        Description = string.Empty,
        Labels = labels
    };

    private static readonly KeyValuePair<string, object?> RepoTag =
        new("repo_provider_id", RepoId);

    private static Task InvokeAsync(
        IssueReworkService svc,
        IReadOnlyList<PullRequestSummary> sorted,
        Mock<IRepositoryProvider> repo,
        Mock<IIssueProvider> issues,
        IReadOnlyDictionary<int, PrMergeabilityStatus> mergeabilityMap,
        IReadOnlySet<string>? activeRunBranches = null,
        bool activeRunBranchesUnavailable = false)
    {
        return svc.TriggerConflictReworkAsync(
            sorted,
            mergeabilityMap,
            activeRunBranches ?? new HashSet<string>(),
            activeRunBranchesUnavailable,
            repo.Object,
            issues.Object,
            IssueProviderId,
            RepoTag,
            CancellationToken.None);
    }

    private static IReadOnlyDictionary<int, PrMergeabilityStatus> MakeMap(
        params (int prNumber, PrMergeabilityStatus status)[] entries)
    {
        var dict = new Dictionary<int, PrMergeabilityStatus>();
        foreach (var (n, s) in entries)
            dict[n] = s;
        return dict;
    }

    // ── Non-conflicted PR → no rework ────────────────────────────────────────

    [Fact]
    public async Task TriggerConflictReworkAsync_NonConflictedPr_NoRework()
    {
        var svc = Create();
        var repo = new Mock<IRepositoryProvider>();
        var issues = new Mock<IIssueProvider>();

        await InvokeAsync(svc, [MakePr(1)], repo, issues,
            MakeMap((1, PrMergeabilityStatus.Behind)));

        repo.Verify(p => p.ExtractLinkedIssuesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        issues.Verify(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Active run on branch → skip ───────────────────────────────────────────

    // TODO: When the active-run branch guard fires, the test verifies ExtractLinkedIssuesAsync
    // is never called (correct) but does not verify GetIssueAsync is never called either. If a
    // future refactor accidentally calls GetIssueAsync before the branch guard, this test would
    // not detect it. Consider adding a GetIssueAsync(Times.Never) assertion to fully pin the
    // guard boundary (no issue fetch occurs before the branch check). Same gap exists in
    // TriggerConflictReworkAsync_UnavailableActiveRuns_Skipped.
    [Fact]
    public async Task TriggerConflictReworkAsync_ActiveRunOnBranch_Skipped()
    {
        var svc = Create();
        var repo = new Mock<IRepositoryProvider>();
        var issues = new Mock<IIssueProvider>();

        var activeBranches = new HashSet<string> { "feature/pr-42" };

        await InvokeAsync(svc, [MakePr(1, "feature/pr-42")], repo, issues,
            MakeMap((1, PrMergeabilityStatus.Conflicted)),
            activeRunBranches: activeBranches);

        repo.Verify(p => p.ExtractLinkedIssuesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never,
            "branch has an active run — TriggerReworkAsync must not be called");
        issues.Verify(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Active-run data unavailable → skip all ────────────────────────────────

    [Fact]
    public async Task TriggerConflictReworkAsync_UnavailableActiveRuns_Skipped()
    {
        var svc = Create();
        var repo = new Mock<IRepositoryProvider>();
        var issues = new Mock<IIssueProvider>();

        await InvokeAsync(svc, [MakePr(1)], repo, issues,
            MakeMap((1, PrMergeabilityStatus.Conflicted)),
            activeRunBranchesUnavailable: true);

        repo.Verify(p => p.ExtractLinkedIssuesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never,
            "active-run data unavailable — all rework must be skipped conservatively");
    }

    // ── Conflicted + agent:done → swap to agent:next ──────────────────────────

    [Fact]
    public async Task TriggerConflictReworkAsync_ConflictedPr_AgentDoneIssue_SwapsToNext()
    {
        var svc = Create();
        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["42"]);

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.Done));
        issues.Setup(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);
        issues.Setup(i => i.RemoveLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        await InvokeAsync(svc, [MakePr(1)], repo, issues,
            MakeMap((1, PrMergeabilityStatus.Conflicted)));

        issues.Verify(i => i.AddLabelAsync(
            It.Is<IssueIdentifier>(id => id.Value == "42"),
            AgentLabels.Next, It.IsAny<CancellationToken>()), Times.Once,
            "agent:done issue must be re-queued — open conflicted PR needs rework");
        issues.Verify(i => i.RemoveLabelAsync(
            It.Is<IssueIdentifier>(id => id.Value == "42"),
            AgentLabels.Done, It.IsAny<CancellationToken>()), Times.Once,
            "agent:done label must be removed as part of the swap");
    }

    // ── Conflicted + agent:error → swap to agent:next ─────────────────────────

    [Fact]
    public async Task TriggerConflictReworkAsync_ConflictedPr_AgentErrorIssue_SwapsToNext()
    {
        var svc = Create();
        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["42"]);

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.Error));
        issues.Setup(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);
        issues.Setup(i => i.RemoveLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        await InvokeAsync(svc, [MakePr(1)], repo, issues,
            MakeMap((1, PrMergeabilityStatus.Conflicted)));

        issues.Verify(i => i.AddLabelAsync(
            It.Is<IssueIdentifier>(id => id.Value == "42"),
            AgentLabels.Next, It.IsAny<CancellationToken>()), Times.Once,
            "agent:error is a valid rework target — open conflicted PR always needs another run");
        issues.Verify(i => i.RemoveLabelAsync(
            It.Is<IssueIdentifier>(id => id.Value == "42"),
            AgentLabels.Error, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Conflicted + agent:next → skip ───────────────────────────────────────

    [Fact]
    public async Task TriggerConflictReworkAsync_ConflictedPr_AgentNextIssue_Skipped()
    {
        var svc = Create();
        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["42"]);

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.Next));

        await InvokeAsync(svc, [MakePr(1)], repo, issues,
            MakeMap((1, PrMergeabilityStatus.Conflicted)));

        issues.Verify(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "agent:next is an active label — must not re-queue");
    }

    // ── Conflicted + agent:in-progress → skip ────────────────────────────────

    [Fact]
    public async Task TriggerConflictReworkAsync_ConflictedPr_AgentInProgressIssue_Skipped()
    {
        var svc = Create();
        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["42"]);

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.InProgress));

        await InvokeAsync(svc, [MakePr(1)], repo, issues,
            MakeMap((1, PrMergeabilityStatus.Conflicted)));

        issues.Verify(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "agent:in-progress is an active label — must not re-queue");
    }

    // ── Conflicted + agent:wont-do → skip (terminal rework blocker) ───────────

    [Fact]
    public async Task TriggerConflictReworkAsync_ConflictedPr_WontDoIssue_Skipped()
    {
        var svc = Create();
        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["42"]);

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.WontDo));

        await InvokeAsync(svc, [MakePr(1)], repo, issues,
            MakeMap((1, PrMergeabilityStatus.Conflicted)));

        issues.Verify(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "agent:wont-do is an abandonment label — must not re-queue");
        issues.Verify(i => i.RemoveLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Conflicted + agent:cancelled → skip (terminal rework blocker) ─────────

    [Fact]
    public async Task TriggerConflictReworkAsync_ConflictedPr_CancelledIssue_Skipped()
    {
        var svc = Create();
        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["42"]);

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.Cancelled));

        await InvokeAsync(svc, [MakePr(1)], repo, issues,
            MakeMap((1, PrMergeabilityStatus.Conflicted)));

        issues.Verify(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "agent:cancelled is an abandonment label — must not re-queue");
        issues.Verify(i => i.RemoveLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Conflicted + agent:epic-review → skip (active label) ─────────────────

    [Fact]
    public async Task TriggerConflictReworkAsync_ConflictedPr_EpicReviewIssue_Skipped()
    {
        var svc = Create();
        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["42"]);

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.EpicReview));

        await InvokeAsync(svc, [MakePr(1)], repo, issues,
            MakeMap((1, PrMergeabilityStatus.Conflicted)));

        issues.Verify(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never, "agent:epic-review is an active label — must not re-queue");
    }

    // ── Conflicted + agent:needs-refinement → swap proceeds ──────────────────

    [Fact]
    public async Task TriggerConflictReworkAsync_ConflictedPr_NeedsRefinementIssue_SwapsToNext()
    {
        var svc = Create();
        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["42"]);

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.NeedsRefinement));
        issues.Setup(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);
        issues.Setup(i => i.RemoveLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        await InvokeAsync(svc, [MakePr(1)], repo, issues,
            MakeMap((1, PrMergeabilityStatus.Conflicted)));

        issues.Verify(i => i.AddLabelAsync(
            It.Is<IssueIdentifier>(id => id.Value == "42"),
            AgentLabels.Next, It.IsAny<CancellationToken>()), Times.Once,
            "agent:needs-refinement is an intentional rework target — label swap must proceed");
    }

    // ── ExtractLinkedIssues fails → no crash ─────────────────────────────────

    [Fact]
    public async Task TriggerConflictReworkAsync_ExtractLinkedIssuesFails_NoSwap()
    {
        var svc = Create();
        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("API error"));

        var issues = new Mock<IIssueProvider>();

        var ex = await Record.ExceptionAsync(() =>
            InvokeAsync(svc, [MakePr(1)], repo, issues,
                MakeMap((1, PrMergeabilityStatus.Conflicted))));

        ex.Should().BeNull("ExtractLinkedIssues failure must be swallowed");
        issues.Verify(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── No linked issues → no swap, no crash ─────────────────────────────────

    [Fact]
    public async Task TriggerConflictReworkAsync_NoLinkedIssues_NoSwap()
    {
        var svc = Create();
        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[]);

        var issues = new Mock<IIssueProvider>();

        var ex = await Record.ExceptionAsync(() =>
            InvokeAsync(svc, [MakePr(1)], repo, issues,
                MakeMap((1, PrMergeabilityStatus.Conflicted))));

        ex.Should().BeNull();
        issues.Verify(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Label-swap fails → swallowed, no crash ────────────────────────────────

    [Fact]
    public async Task TriggerConflictReworkAsync_LabelSwapFails_Swallowed()
    {
        var svc = Create();
        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["42"]);

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.Error));
        issues.Setup(i => i.RemoveLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);
        issues.Setup(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("API error on add"));

        var ex = await Record.ExceptionAsync(() =>
            InvokeAsync(svc, [MakePr(1)], repo, issues,
                MakeMap((1, PrMergeabilityStatus.Conflicted))));

        ex.Should().BeNull("label-swap failure must be swallowed");
    }

    // ── Different branch active → only that PR's branch is guarded ──────────

    [Fact]
    public async Task TriggerConflictReworkAsync_DifferentBranchActive_ProceedsForConflictedPr()
    {
        var svc = Create();
        var repo = new Mock<IRepositoryProvider>();
        repo.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["42"]);

        var issues = new Mock<IIssueProvider>();
        issues.Setup(i => i.GetIssueAsync(new IssueIdentifier("42"), It.IsAny<CancellationToken>()))
              .ReturnsAsync(MakeIssue("42", AgentLabels.Error));
        issues.Setup(i => i.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);
        issues.Setup(i => i.RemoveLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
              .Returns(Task.CompletedTask);

        // Active run is on a DIFFERENT branch
        var activeBranches = new HashSet<string> { "feature/pr-99-other" };

        await InvokeAsync(svc, [MakePr(1, "feature/pr-42")], repo, issues,
            MakeMap((1, PrMergeabilityStatus.Conflicted)),
            activeRunBranches: activeBranches);

        issues.Verify(i => i.AddLabelAsync(
            It.Is<IssueIdentifier>(id => id.Value == "42"),
            AgentLabels.Next, It.IsAny<CancellationToken>()), Times.Once,
            "active run on a different branch must not block the swap for this PR");
    }
}
