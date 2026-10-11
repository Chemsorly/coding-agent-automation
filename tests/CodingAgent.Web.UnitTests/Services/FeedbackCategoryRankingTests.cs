using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="FeedbackCategoryRanking.Rank"/>.
/// All test requirements from issue #3580 requirement 5.1.
/// </summary>
public class FeedbackCategoryRankingTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static PipelineRunSummary Run(
        string runId,
        DateTimeOffset startedAt,
        RunFeedback? feedback,
        // TODO: The 'outcome' parameter here is never used — PipelineRunSummary has no Outcome field;
        // the entry's Outcome is drawn from feedback.Outcome (set via HarnessFeedback/IssueFeedback
        // helpers). Any caller passing outcome directly to Run() will silently have no effect.
        // Pass outcome through HarnessFeedback(outcome: ...) or IssueFeedback(outcome: ...) instead.
        FeedbackOutcome outcome = FeedbackOutcome.Success) => new()
    {
        RunId = runId,
        IssueIdentifier = "1",
        IssueTitle = "t",
        FinalStep = PipelineStep.Completed,
        StartedAtOffset = startedAt,
        InitiatedBy = "manual",
        Feedback = feedback,
    };

    private static RunFeedback HarnessFeedback(
        string? category = null,
        string? stuckReason = null,
        string[]? missingContext = null,
        string[]? missingCapabilities = null,
        string[]? promptIssues = null,
        string[]? suggestions = null,
        FeedbackOutcome outcome = FeedbackOutcome.Success) => new()
    {
        Outcome = outcome,
        CollectedAtUtc = DateTime.UtcNow,
        Harness = new HarnessFeedback
        {
            Category = category,
            StuckReason = stuckReason,
            MissingContext = missingContext ?? [],
            MissingCapabilities = missingCapabilities ?? [],
            PromptIssues = promptIssues ?? [],
            Suggestions = suggestions ?? [],
        },
    };

    private static RunFeedback IssueFeedback(
        string? category = null,
        string? description = null,
        string? humanActionNeeded = null,
        FeedbackOutcome outcome = FeedbackOutcome.Success) => new()
    {
        Outcome = outcome,
        CollectedAtUtc = DateTime.UtcNow,
        Harness = new HarnessFeedback(),
        Issue = new IssueFeedback
        {
            Category = category,
            Description = description,
            HumanActionNeeded = humanActionNeeded,
        },
    };

    // ── tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Rank_Harness_GroupsCategoriesIgnoringCase_AndUsesNewestSpelling()
    {
        var older = Run("r1", DateTimeOffset.UtcNow.AddHours(-2),
            HarnessFeedback(category: "Missing file context"));
        var newer = Run("r2", DateTimeOffset.UtcNow.AddHours(-1),
            HarnessFeedback(category: " missing file context "));

        var result = FeedbackCategoryRanking.Rank([older, newer], FeedbackKind.Harness);

        result.Should().HaveCount(1);
        result[0].Category.Should().Be("missing file context");
        result[0].Count.Should().Be(2);
    }

    [Fact]
    public void Rank_Harness_OrdersByCountThenNewest()
    {
        var now = DateTimeOffset.UtcNow;

        // Category A: 2 runs
        var a1 = Run("a1", now.AddHours(-4), HarnessFeedback(category: "A"));
        var a2 = Run("a2", now.AddHours(-3), HarnessFeedback(category: "A"));

        // Category B: 1 run, older
        var b1 = Run("b1", now.AddHours(-5), HarnessFeedback(category: "B"));

        // Category C: 1 run, newer than B
        var c1 = Run("c1", now.AddHours(-2), HarnessFeedback(category: "C"));

        var result = FeedbackCategoryRanking.Rank([a1, a2, b1, c1], FeedbackKind.Harness);

        result.Should().HaveCount(3);
        result[0].Category.Should().Be("A"); // count 2 wins
        result[1].Category.Should().Be("C"); // count 1, newer than B
        result[2].Category.Should().Be("B"); // count 1, older
    }

    [Fact]
    public void Rank_Harness_BlankCategory_GoesToUncategorized()
    {
        var run = Run("r1", DateTimeOffset.UtcNow,
            HarnessFeedback(category: "   ", stuckReason: "something"));

        var result = FeedbackCategoryRanking.Rank([run], FeedbackKind.Harness);

        result.Should().HaveCount(1);
        result[0].Category.Should().Be(FeedbackCategoryRanking.Uncategorized);
    }

    [Fact]
    public void Rank_Harness_SkipsRunsWithoutFeedbackOrContent()
    {
        // Run with null Feedback
        var noFeedback = Run("r1", DateTimeOffset.UtcNow, feedback: null);

        // Run with Feedback but all harness fields empty/null
        var emptyHarness = Run("r2", DateTimeOffset.UtcNow, new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback
            {
                Category = null,
                StuckReason = null,
                MissingContext = [],
                MissingCapabilities = [],
                PromptIssues = [],
                Suggestions = [],
            },
        });

        var result = FeedbackCategoryRanking.Rank([noFeedback, emptyHarness], FeedbackKind.Harness);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Rank_Harness_TextPrefersStuckReasonThenFirstListItem()
    {
        // Only MissingCapabilities set — Text should be first item
        var capOnly = Run("r1", DateTimeOffset.UtcNow.AddHours(-2),
            HarnessFeedback(category: "A", missingCapabilities: ["db query tool"]));

        // StuckReason set along with other fields — StuckReason wins
        var withStuck = Run("r2", DateTimeOffset.UtcNow.AddHours(-1),
            HarnessFeedback(category: "B", stuckReason: "got stuck here",
                missingCapabilities: ["other tool"]));

        // TODO: This test does not verify that MissingContext[0] (priority 2) beats
        // MissingCapabilities[0] (priority 3). A bug that swapped those two positions in
        // FeedbackCategoryRanking.Rank would not be caught. Add a case with only MissingContext set
        // and assert its text is preferred over a run with only MissingCapabilities.
        var result = FeedbackCategoryRanking.Rank([capOnly, withStuck], FeedbackKind.Harness);

        result.Should().HaveCount(2);
        var groupA = result.First(g => g.Category == "A");
        var groupB = result.First(g => g.Category == "B");
        groupA.Entries[0].Text.Should().Be("db query tool");
        groupB.Entries[0].Text.Should().Be("got stuck here");
    }

    [Fact]
    public void Rank_TruncatesLongText()
    {
        var longReason = new string('x', 300);
        var run = Run("r1", DateTimeOffset.UtcNow,
            HarnessFeedback(category: "A", stuckReason: longReason));

        var result = FeedbackCategoryRanking.Rank([run], FeedbackKind.Harness);

        result.Should().HaveCount(1);
        result[0].Entries[0].Text.Should().HaveLength(FeedbackCategoryRanking.MaxTextLength + 1); // 200 chars + ellipsis
        result[0].Entries[0].Text.Should().EndWith("…");
        result[0].Entries[0].Text[..FeedbackCategoryRanking.MaxTextLength].Should().Be(new string('x', 200));
    }

    [Fact]
    public void Rank_Issue_UsesIssueFeedbackOnly()
    {
        // Run with null Issue — should be skipped
        var noIssue = Run("r1", DateTimeOffset.UtcNow,
            HarnessFeedback(category: "harness-only"));

        // Run with Issue feedback containing description
        var withIssue = Run("r2", DateTimeOffset.UtcNow,
            IssueFeedback(category: "missing context", description: "The issue is unclear"));

        // TODO: The HumanActionNeeded fallback (second candidate in FirstNonBlank) is not tested here.
        // A bug that dropped or reordered the HumanActionNeeded candidate in FeedbackCategoryRanking.Rank
        // would not be detected. Add a case with Description = null and HumanActionNeeded set, asserting
        // the text equals the HumanActionNeeded value.
        var result = FeedbackCategoryRanking.Rank([noIssue, withIssue], FeedbackKind.Issue);

        result.Should().HaveCount(1);
        result[0].Category.Should().Be("missing context");
        result[0].Entries[0].Text.Should().Be("The issue is unclear");
    }

    [Fact]
    public void Rank_CountsFailures()
    {
        var now = DateTimeOffset.UtcNow;

        var success = Run("r1", now.AddHours(-2),
            HarnessFeedback(category: "A", outcome: FeedbackOutcome.Success));
        var failure = Run("r2", now.AddHours(-1),
            HarnessFeedback(category: "A", outcome: FeedbackOutcome.Failure));

        var result = FeedbackCategoryRanking.Rank([success, failure], FeedbackKind.Harness);

        result.Should().HaveCount(1);
        result[0].Count.Should().Be(2);
        result[0].FailureCount.Should().Be(1);
    }
}
