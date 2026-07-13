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

    // ─── PipelineFormatting.AppendCodeReviewSection: with verdict ──────────────

    [Fact]
    public void AppendCodeReviewSection_WithVerdict_RendersBeforeTable()
    {
        var summary = new CodeReviewSummary(
            AgentsRun: ["Agent1"],
            CriticalCount: 1,
            WarningCount: 0,
            SuggestionCount: 0,
            AgentFindings: [new AgentFindings("Agent1", "Found issue")])
        {
            VerdictSummary = "Found 1 critical race condition in the drain service."
        };

        var result = PipelineFormatting.GeneratePrBody(new PrBodyParameters
        {
            IssueReference = "#1",
            TestsPassed = 0,
            TestsFailed = 0,
            TestsSkipped = 0,
            CoveragePercent = null,
            FileChanges = [],
            IssueTitle = "Test",
            CodeReviewSummary = summary,
        });

        result.Should().Contain("**Review verdict**: Found 1 critical race condition");
        var verdictIndex = result.IndexOf("**Review verdict**:", StringComparison.Ordinal);
        var tableIndex = result.IndexOf("| Severity |", StringComparison.Ordinal);
        verdictIndex.Should().BeLessThan(tableIndex);
    }

    [Fact]
    public void AppendCodeReviewSection_VerdictRendersBeforeNoFindings()
    {
        var summary = new CodeReviewSummary(
            AgentsRun: ["Agent1"],
            CriticalCount: 0,
            WarningCount: 0,
            SuggestionCount: 0,
            AgentFindings: [])
        {
            VerdictSummary = "No issues found, implementation follows standard patterns."
        };

        var result = PipelineFormatting.GeneratePrBody(new PrBodyParameters
        {
            IssueReference = "#1",
            TestsPassed = 0,
            TestsFailed = 0,
            TestsSkipped = 0,
            CoveragePercent = null,
            FileChanges = [],
            IssueTitle = "Test",
            CodeReviewSummary = summary,
        });

        result.Should().Contain("**Review verdict**: No issues found");
        result.Should().Contain("Code review: no findings");
        // Verdict should appear before "no findings"
        var verdictIndex = result.IndexOf("**Review verdict**:", StringComparison.Ordinal);
        var noFindingsIndex = result.IndexOf("Code review: no findings", StringComparison.Ordinal);
        verdictIndex.Should().BeLessThan(noFindingsIndex);
    }

    [Fact]
    public void AppendCodeReviewSection_NullVerdict_NoVerdictLine()
    {
        var summary = new CodeReviewSummary(
            AgentsRun: ["Agent1"],
            CriticalCount: 1,
            WarningCount: 0,
            SuggestionCount: 0,
            AgentFindings: [new AgentFindings("Agent1", "Found issue")])
        {
            VerdictSummary = null
        };

        var result = PipelineFormatting.GeneratePrBody(new PrBodyParameters
        {
            IssueReference = "#1",
            TestsPassed = 0,
            TestsFailed = 0,
            TestsSkipped = 0,
            CoveragePercent = null,
            FileChanges = [],
            IssueTitle = "Test",
            CodeReviewSummary = summary,
        });

        result.Should().NotContain("**Review verdict**:");
        // Still renders the table
        result.Should().Contain("CRITICAL | 1 | Fixed");
    }

    [Fact]
    public void AppendCodeReviewSection_DoesNotRenderChangeSummary()
    {
        // Implementation PR body should NOT have ChangeSummary — it's already in the blockquote
        var summary = new CodeReviewSummary(
            AgentsRun: ["Agent1"],
            CriticalCount: 0,
            WarningCount: 0,
            SuggestionCount: 0,
            AgentFindings: [])
        {
            ChangeSummary = "This should not appear in the implementation PR body.",
            VerdictSummary = "Verdict text."
        };

        var result = PipelineFormatting.GeneratePrBody(new PrBodyParameters
        {
            IssueReference = "#1",
            TestsPassed = 0,
            TestsFailed = 0,
            TestsSkipped = 0,
            CoveragePercent = null,
            FileChanges = [],
            IssueTitle = "Test",
            CodeReviewSummary = summary,
        });

        result.Should().NotContain("**Changes**:");
        result.Should().Contain("**Review verdict**: Verdict text.");
    }

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

    [Fact]
    public void PipelineFormatting_NullSummariesAfterFailure_BackwardCompatible()
    {
        // Simulates: agent threw exception → summaries are null → formatter still works
        var summary = new CodeReviewSummary(
            AgentsRun: ["Agent1"],
            CriticalCount: 2,
            WarningCount: 1,
            SuggestionCount: 0,
            AgentFindings: [new AgentFindings("Agent1", "Found issues")])
        {
            ChangeSummary = null,
            VerdictSummary = null
        };

        var result = PipelineFormatting.GeneratePrBody(new PrBodyParameters
        {
            IssueReference = "#1",
            TestsPassed = 5,
            TestsFailed = 0,
            TestsSkipped = 0,
            CoveragePercent = 80.0,
            FileChanges = [],
            IssueTitle = "Test",
            CodeReviewSummary = summary,
        });

        result.Should().NotContain("**Review verdict**:");
        result.Should().Contain("CRITICAL | 2 | Fixed");
        result.Should().Contain("WARNING | 1 | Reported");
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
