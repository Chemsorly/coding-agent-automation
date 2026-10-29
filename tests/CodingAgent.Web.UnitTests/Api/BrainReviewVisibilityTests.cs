using AwesomeAssertions;
using CodingAgent.Api.Ui;
using CodingAgent.Contracts.Auth;
using CodingAgent.Pipeline.Models;
using Moq;
using Xunit;

namespace CodingAgent.Web.UnitTests.Api;

/// <summary>
/// Unit tests for <see cref="BrainReviewVisibility"/> (spec 054, 8A.2, DD13, req 12.5).
/// </summary>
public sealed class BrainReviewVisibilityTests
{
    private static IUserAccess GlobalAdmin()
    {
        var m = new Mock<IUserAccess>();
        m.Setup(a => a.Grant).Returns(new AccessGrant(AccessRole.Admin, new Dictionary<string, AccessRole>(), []));
        return m.Object;
    }

    private static IUserAccess GlobalReader()
    {
        var m = new Mock<IUserAccess>();
        m.Setup(a => a.Grant).Returns(new AccessGrant(AccessRole.ReadOnly, new Dictionary<string, AccessRole>(), []));
        return m.Object;
    }

    private static IUserAccess ScopedUser()
    {
        var m = new Mock<IUserAccess>();
        m.Setup(a => a.Grant).Returns(new AccessGrant(AccessRole.None, new Dictionary<string, AccessRole> { ["proj-1"] = AccessRole.ReadOnly }, []));
        return m.Object;
    }

    private static PipelineRunSummary RunWithOutcome(BrainConsolidationOutcome outcome) => new PipelineRunSummary
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = new IssueIdentifier("1"),
        IssueTitle = "Test",
        FinalStep = PipelineStep.Completed,
        RunType = PipelineRunType.Consolidation,
        StartedAtOffset = DateTimeOffset.UtcNow,
        BrainConsolidationOutcome = outcome,
    };

    private static BrainConsolidationOutcome FullOutcome() => new BrainConsolidationOutcome
    {
        Promoted = 3,
        Appended = 1,
        Retired = 2,
        MissedAgain = 0,
        Lines = [new BrainReviewLine { Action = "promoted", Scope = "general", File = "general/SKILL.md", Text = "a lesson" }],
        Checks = [new BrainReviewCheck { Cause = "missing null check", SuggestedHome = "quality_gate" }],
        FlakyTests = [new BrainFlakyTest { Name = "Test.Flaky", Count = 3 }],
        PushedCommitSha = "abc123",
        PushedCommitUrl = "https://github.com/org/brain/commit/abc123",
    };

    // ── Apply(run, access) ────────────────────────────────────────────────

    [Fact]
    public void Apply_RunWithNoOutcome_ReturnsUnchanged()
    {
        var run = new PipelineRunSummary
        {
            RunId = "r1", IssueIdentifier = new IssueIdentifier("1"), IssueTitle = "T",
            FinalStep = PipelineStep.Completed, RunType = PipelineRunType.Consolidation,
            StartedAtOffset = DateTimeOffset.UtcNow,
            BrainConsolidationOutcome = null,
        };
        var result = BrainReviewVisibility.Apply(run, ScopedUser());
        result.Should().BeSameAs(run, "when there is no outcome, the same instance is returned");
    }

    [Fact]
    public void Apply_GlobalAdmin_ReturnsUnchanged()
    {
        var run = RunWithOutcome(FullOutcome());
        var result = BrainReviewVisibility.Apply(run, GlobalAdmin());
        result.Should().BeSameAs(run, "global admin sees the full outcome unchanged");
    }

    [Fact]
    public void Apply_GlobalReadOnly_ReturnsUnchanged()
    {
        var run = RunWithOutcome(FullOutcome());
        var result = BrainReviewVisibility.Apply(run, GlobalReader());
        result.Should().BeSameAs(run, "global readonly user sees the full outcome unchanged");
    }

    [Fact]
    public void Apply_ScopedUser_RedactsDetail_PreservesAggregates()
    {
        var run = RunWithOutcome(FullOutcome());
        var result = BrainReviewVisibility.Apply(run, ScopedUser());

        result.Should().NotBeSameAs(run, "scoped user gets a copy, not the original");

        var outcome = result.BrainConsolidationOutcome!;
        // Aggregates preserved
        outcome.Promoted.Should().Be(3);
        outcome.Appended.Should().Be(1);
        outcome.Retired.Should().Be(2);
        outcome.MissedAgain.Should().Be(0);
        // Detail redacted
        outcome.Lines.Should().BeEmpty("lines are redacted for project-scoped users");
        outcome.Checks.Should().BeEmpty("checks are redacted");
        outcome.FlakyTests.Should().BeEmpty("flaky tests are redacted");
        outcome.PushedCommitSha.Should().BeNull("commit SHA is redacted");
        outcome.PushedCommitUrl.Should().BeNull("commit URL is redacted");
        outcome.LinesTruncated.Should().BeFalse();
    }

    [Fact]
    public void Apply_ScopedUser_OriginalNotMutated()
    {
        // T19: the original summary must not be modified
        var run = RunWithOutcome(FullOutcome());
        _ = BrainReviewVisibility.Apply(run, ScopedUser());

        run.BrainConsolidationOutcome!.Lines.Should().HaveCount(1, "original must be unchanged");
        run.BrainConsolidationOutcome.PushedCommitSha.Should().Be("abc123", "original SHA must survive the redacted read");
    }

    // ── Apply(page, access) ───────────────────────────────────────────────

    [Fact]
    public void Apply_PagedResult_GlobalUser_ReturnsSamePage()
    {
        var page = new PagedResult<PipelineRunSummary>
        {
            Items = [RunWithOutcome(FullOutcome())],
            Page = 1, PageSize = 10, HasMore = false
        };
        var result = BrainReviewVisibility.Apply(page, GlobalAdmin());
        result.Should().BeSameAs(page);
    }

    [Fact]
    public void Apply_PagedResult_ScopedUser_RedactsAll()
    {
        var page = new PagedResult<PipelineRunSummary>
        {
            Items = [RunWithOutcome(FullOutcome()), RunWithOutcome(FullOutcome())],
            Page = 1, PageSize = 10, HasMore = false
        };
        var result = BrainReviewVisibility.Apply(page, ScopedUser());

        result.Should().NotBeSameAs(page);
        result.Items.Should().AllSatisfy(r =>
        {
            r.BrainConsolidationOutcome!.Lines.Should().BeEmpty();
            r.BrainConsolidationOutcome.Promoted.Should().Be(3);
        });
        result.Page.Should().Be(1);
        result.HasMore.Should().BeFalse();
    }
}
