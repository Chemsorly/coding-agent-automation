using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

public class ReviewSummaryFormatterTests
{
    // ─── ReviewFindingsFormatter: with summaries ───────────────────────────────

    [Fact]
    public void ReviewFindingsFormatter_WithBothSummaries_RendersChangesAndVerdict()
    {
        var run = CreateRunWithSummaries(
            changeSummary: "Added pagination to the users endpoint. Affected UserController.cs and UserRepository.cs.",
            verdictSummary: "Clean implementation with no critical issues. 1 suggestion about response caching.");

        var result = ReviewFindingsFormatter.Format(run);

        result.Should().Contain("**Changes**: Added pagination to the users endpoint.");
        result.Should().Contain("**Review verdict**: Clean implementation with no critical issues.");
    }

    [Fact]
    public void ReviewFindingsFormatter_WithSummaries_RendersBeforeAgentsLine()
    {
        var run = CreateRunWithSummaries(
            changeSummary: "Short change.",
            verdictSummary: "Short verdict.");

        var result = ReviewFindingsFormatter.Format(run);

        var changesIndex = result.IndexOf("**Changes**:", StringComparison.Ordinal);
        var verdictIndex = result.IndexOf("**Review verdict**:", StringComparison.Ordinal);
        var agentsIndex = result.IndexOf("**Review Agents**:", StringComparison.Ordinal);

        changesIndex.Should().BeGreaterThan(-1);
        verdictIndex.Should().BeGreaterThan(changesIndex);
        agentsIndex.Should().BeGreaterThan(verdictIndex);
    }

    [Fact]
    public void ReviewFindingsFormatter_NullSummaries_NoSummaryLines()
    {
        var run = CreateRunWithSummaries(changeSummary: null, verdictSummary: null);

        var result = ReviewFindingsFormatter.Format(run);

        result.Should().NotContain("**Changes**:");
        result.Should().NotContain("**Review verdict**:");
        // Should still have the standard content
        result.Should().Contain("Automated Code Review");
        result.Should().Contain("**Review Agents**:");
    }

    [Fact]
    public void ReviewFindingsFormatter_OnlyVerdict_RendersVerdictOnly()
    {
        var run = CreateRunWithSummaries(changeSummary: null, verdictSummary: "No issues found.");

        var result = ReviewFindingsFormatter.Format(run);

        result.Should().NotContain("**Changes**:");
        result.Should().Contain("**Review verdict**: No issues found.");
    }

    [Fact]
    public void ReviewFindingsFormatter_LongSummary_TruncatesAt500Chars()
    {
        var longSummary = "First sentence. " + new string('x', 600);
        var run = CreateRunWithSummaries(changeSummary: longSummary, verdictSummary: null);

        var result = ReviewFindingsFormatter.Format(run);

        // Should contain the truncated version, not the full thing
        // TODO: Strengthen assertion — verify the **Changes** line contains no 'x' characters,
        // or assert the exact rendered value, to ensure truncation actually removed overflow
        // (current partial-match assertion could pass even if truncation were broken).
        result.Should().Contain("**Changes**: First sentence....");
        result.Should().NotContain(new string('x', 600));
    }

    // Deleted (behavior removed): AppendCodeReviewSection_WithVerdict_RendersBeforeTable
    // Deleted (behavior removed): AppendCodeReviewSection_VerdictRendersBeforeNoFindings
    // Deleted (behavior removed): AppendCodeReviewSection_NullVerdict_NoVerdictLine
    // Deleted (behavior removed): AppendCodeReviewSection_DoesNotRenderChangeSummary
    // Deleted (behavior removed): PipelineFormatting_NullSummariesAfterFailure_BackwardCompatible
    // All five tested AppendCodeReviewSection via PipelineFormatting.GeneratePrBody — that method
    // no longer calls AppendCodeReviewSection; the section has been removed from the PR body.

    // ─── Non-fatal failure path ────────────────────────────────────────────────

    [Fact]
    public void ReviewFindingsFormatter_NullSummariesAfterFailure_BackwardCompatible()
    {
        // Simulates: agent threw exception → summaries are null → formatter still works
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "42",
            IssueTitle = "Test PR",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Review,
            CodeReviewAgentsRun = ["Correctness"],
            CodeReviewChangeSummary = null,
            CodeReviewVerdictSummary = null,
        };
        run.SetCodeReviewCounts(1, 0, 0);
        run.CodeReviewAgentFindings["Correctness"] = "[CRITICAL] — Race condition in X";

        var result = ReviewFindingsFormatter.Format(run);

        result.Should().NotContain("**Changes**:");
        result.Should().NotContain("**Review verdict**:");
        result.Should().Contain("[CRITICAL] | 1 |");
        result.Should().Contain("Race condition in X");
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private static PipelineRun CreateRunWithSummaries(string? changeSummary, string? verdictSummary)
    {
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "42",
            IssueTitle = "Test PR",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Review,
            CodeReviewAgentsRun = ["Correctness", "Security"],
            CodeReviewChangeSummary = changeSummary,
            CodeReviewVerdictSummary = verdictSummary,
        };
        run.SetCodeReviewCounts(0, 1, 0);
        run.CodeReviewAgentFindings["Correctness"] = "[WARNING] — Minor issue";

        return run;
    }
}
