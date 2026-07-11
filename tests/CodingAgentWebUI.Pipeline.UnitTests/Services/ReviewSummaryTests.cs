using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for the review summary feature:
/// - ReviewSummaryParser output parsing
/// - ReviewFindingsFormatter rendering with summaries
/// - PipelineFormatting.AppendCodeReviewSection rendering with summaries
/// - Sentence-boundary truncation
/// </summary>
public class ReviewSummaryTests
{
    // ─── ReviewSummaryParser Tests ──────────────────────────────────────────────

    [Fact]
    public void Parse_WellFormedOutput_ExtractsBothSections()
    {
        var output = """
            ## Change Summary
            Added Serilog bootstrap logger to capture early startup messages. 6 lines in Program.cs.

            ## Review Verdict
            Clean implementation following standard patterns. One warning about missing test coverage.
            """;

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().Contain("Added Serilog bootstrap logger");
        verdictSummary.Should().Contain("Clean implementation");
    }

    [Fact]
    public void Parse_PartialOutput_ChangeSummaryOnly_ReturnsChangeAndNullVerdict()
    {
        var output = """
            ## Change Summary
            Refactored the drain service to use pre-reservation slots.
            """;

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().Contain("Refactored the drain service");
        verdictSummary.Should().BeNull();
    }

    [Fact]
    public void Parse_PartialOutput_VerdictOnly_ReturnsNullChangeAndVerdict()
    {
        var output = """
            ## Review Verdict
            Two race conditions found in the pre-reservation flow. Both fixed.
            """;

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().BeNull();
        verdictSummary.Should().Contain("Two race conditions");
    }

    [Fact]
    public void Parse_MalformedOutput_NoHeadings_ReturnsBothNull()
    {
        var output = "This is some random agent output without any expected headings.";

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().BeNull();
        verdictSummary.Should().BeNull();
    }

    [Fact]
    public void Parse_EmptyString_ReturnsBothNull()
    {
        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse("");

        changeSummary.Should().BeNull();
        verdictSummary.Should().BeNull();
    }

    [Fact]
    public void Parse_NullInput_ReturnsBothNull()
    {
        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(null);

        changeSummary.Should().BeNull();
        verdictSummary.Should().BeNull();
    }

    [Fact]
    public void Parse_WhitespaceOnly_ReturnsBothNull()
    {
        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse("   \n\n  ");

        changeSummary.Should().BeNull();
        verdictSummary.Should().BeNull();
    }

    [Fact]
    public void Parse_HeadingsWithEmptyContent_ReturnsBothNull()
    {
        var output = """
            ## Change Summary

            ## Review Verdict

            """;

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().BeNull();
        verdictSummary.Should().BeNull();
    }

    [Fact]
    public void Parse_CaseInsensitiveHeadings_ExtractsContent()
    {
        var output = """
            ## change summary
            Lower case heading content.

            ## review verdict
            Lower case verdict content.
            """;

        var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

        changeSummary.Should().Contain("Lower case heading content");
        verdictSummary.Should().Contain("Lower case verdict content");
    }

    // ─── TruncateAtSentenceBoundary Tests ───────────────────────────────────────

    [Fact]
    public void TruncateAtSentenceBoundary_ShortText_ReturnsUnchanged()
    {
        var text = "Short text within limit.";

        var result = ReviewSummaryParser.TruncateAtSentenceBoundary(text, 500);

        result.Should().Be(text);
    }

    [Fact]
    public void TruncateAtSentenceBoundary_NullInput_ReturnsNull()
    {
        var result = ReviewSummaryParser.TruncateAtSentenceBoundary(null);

        result.Should().BeNull();
    }

    [Fact]
    public void TruncateAtSentenceBoundary_LongText_TruncatesAtSentenceBoundary()
    {
        var sentence1 = "First sentence here. ";
        var sentence2 = "Second sentence is a bit longer. ";
        // Make text longer than 50 chars to test truncation
        var text = sentence1 + sentence2 + "Third sentence continues beyond the limit and goes on.";

        var result = ReviewSummaryParser.TruncateAtSentenceBoundary(text, 50);

        // TODO: Assertion is too weak — should assert exact expected output (e.g., "First sentence here....")
        // instead of a loose length bound that would pass even with substantially broken truncation logic.
        // Should truncate at the last ". " before 50 chars
        result.Should().EndWith("...");
        result!.Length.Should().BeLessThanOrEqualTo(55); // sentence end + "..."
    }

    [Fact]
    public void TruncateAtSentenceBoundary_NoSentenceBoundary_HardTruncates()
    {
        var text = new string('a', 600); // No periods at all

        var result = ReviewSummaryParser.TruncateAtSentenceBoundary(text, 500);

        result.Should().HaveLength(503); // 500 + "..."
        result.Should().EndWith("...");
    }

    [Fact]
    public void TruncateAtSentenceBoundary_ExactlyAtLimit_ReturnsUnchanged()
    {
        var text = new string('x', 500);

        var result = ReviewSummaryParser.TruncateAtSentenceBoundary(text, 500);

        result.Should().Be(text);
    }

    // ─── ReviewFindingsFormatter with Summaries ─────────────────────────────────

    [Fact]
    public void Format_WithBothSummaries_RendersChangesAndVerdict()
    {
        var run = CreateRunWithSummaries(
            changeSummary: "Added new validation logic to the user registration flow.",
            verdictSummary: "Clean implementation with one minor warning about error handling.");

        var result = ReviewFindingsFormatter.Format(run);

        result.Should().Contain("**Changes**: Added new validation logic");
        result.Should().Contain("**Review verdict**: Clean implementation");
        // Summaries should appear before the agents line
        var changesIdx = result.IndexOf("**Changes**:");
        var agentsIdx = result.IndexOf("**Review Agents**:");
        changesIdx.Should().BeLessThan(agentsIdx);
    }

    [Fact]
    public void Format_WithNullSummaries_NoSummaryLinesRendered()
    {
        var run = CreateRunWithSummaries(changeSummary: null, verdictSummary: null);

        var result = ReviewFindingsFormatter.Format(run);

        result.Should().NotContain("**Changes**:");
        result.Should().NotContain("**Review verdict**:");
        // Existing content still present
        result.Should().Contain("**Review Agents**:");
    }

    [Fact]
    public void Format_WithVerdictOnly_RendersVerdictWithoutChanges()
    {
        var run = CreateRunWithSummaries(changeSummary: null, verdictSummary: "No issues found.");

        var result = ReviewFindingsFormatter.Format(run);

        result.Should().NotContain("**Changes**:");
        result.Should().Contain("**Review verdict**: No issues found.");
    }

    [Fact]
    public void Format_WithLongSummary_TruncatesAtSentenceBoundary()
    {
        var longSummary = string.Join(". ", Enumerable.Range(1, 50).Select(i => $"Sentence number {i} with some padding text")) + ".";

        var run = CreateRunWithSummaries(changeSummary: longSummary, verdictSummary: null);

        var result = ReviewFindingsFormatter.Format(run);

        var changesLine = result.Split('\n').First(l => l.StartsWith("**Changes**:"));
        // TODO: Assertion too loose — should assert BeLessThanOrEqualTo(503) (500 + "...") to properly
        // validate the 500-char cap requirement, and verify the result ends with "...".
        // The rendered content (after "**Changes**: ") should be capped at 500 + "..."
        var summaryContent = changesLine.Replace("**Changes**: ", "");
        summaryContent.Length.Should().BeLessThanOrEqualTo(510); // 500 + sentence + "..."
    }

    // ─── PipelineFormatting.AppendCodeReviewSection with Summaries ───────────────

    [Fact]
    public void GeneratePrBody_WithVerdictSummary_RendersVerdictBeforeNoFindings()
    {
        var review = new CodeReviewSummary(
            AgentsRun: ["agent-1"],
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
            TestsPassed = 10,
            TestsFailed = 0,
            TestsSkipped = 0,
            CoveragePercent = null,
            FileChanges = [],
            IssueTitle = "Test",
            CodeReviewSummary = review,
        });

        result.Should().Contain("**Review verdict**: No issues found");
        result.Should().Contain("Code review: no findings");
        // Verdict should come before "no findings"
        var verdictIdx = result.IndexOf("**Review verdict**:");
        var noFindingsIdx = result.IndexOf("Code review: no findings");
        verdictIdx.Should().BeLessThan(noFindingsIdx);
    }

    [Fact]
    public void GeneratePrBody_WithVerdictSummary_RendersVerdictBeforeTable()
    {
        var review = new CodeReviewSummary(
            AgentsRun: ["security-agent"],
            CriticalCount: 2,
            WarningCount: 1,
            SuggestionCount: 0,
            AgentFindings: [new AgentFindings("security-agent", "SQL injection found")])
        {
            VerdictSummary = "Found 2 critical SQL injection vulnerabilities in the data layer."
        };

        var result = PipelineFormatting.GeneratePrBody(new PrBodyParameters
        {
            IssueReference = "#1",
            TestsPassed = 10,
            TestsFailed = 0,
            TestsSkipped = 0,
            CoveragePercent = null,
            FileChanges = [],
            IssueTitle = "Test",
            CodeReviewSummary = review,
        });

        result.Should().Contain("**Review verdict**: Found 2 critical SQL injection");
        // Verdict should come before severity table
        var verdictIdx = result.IndexOf("**Review verdict**:");
        var tableIdx = result.IndexOf("| Severity | Count | Action |");
        verdictIdx.Should().BeLessThan(tableIdx);
    }

    [Fact]
    public void GeneratePrBody_WithNullVerdictSummary_NoVerdictRendered()
    {
        var review = new CodeReviewSummary(
            AgentsRun: ["agent-1"],
            CriticalCount: 1,
            WarningCount: 0,
            SuggestionCount: 0,
            AgentFindings: [new AgentFindings("agent-1", "Some finding")])
        {
            VerdictSummary = null
        };

        var result = PipelineFormatting.GeneratePrBody(new PrBodyParameters
        {
            IssueReference = "#1",
            TestsPassed = 10,
            TestsFailed = 0,
            TestsSkipped = 0,
            CoveragePercent = null,
            FileChanges = [],
            IssueTitle = "Test",
            CodeReviewSummary = review,
        });

        result.Should().NotContain("**Review verdict**:");
        // But existing content still present
        result.Should().Contain("| Severity | Count | Action |");
    }

    [Fact]
    public void GeneratePrBody_CodeReviewSummary_DoesNotRenderChangeSummary()
    {
        // Implementation PR body should NOT render ChangeSummary (it's already in the blockquote)
        var review = new CodeReviewSummary(
            AgentsRun: ["agent-1"],
            CriticalCount: 0,
            WarningCount: 0,
            SuggestionCount: 0,
            AgentFindings: [])
        {
            ChangeSummary = "This change adds a new feature.",
            VerdictSummary = "Clean review."
        };

        var result = PipelineFormatting.GeneratePrBody(new PrBodyParameters
        {
            IssueReference = "#1",
            TestsPassed = 10,
            TestsFailed = 0,
            TestsSkipped = 0,
            CoveragePercent = null,
            FileChanges = [],
            IssueTitle = "Test",
            CodeReviewSummary = review,
        });

        result.Should().NotContain("**Changes**:");
        result.Should().Contain("**Review verdict**: Clean review.");
    }

    // ─── Non-Fatal Failure Path ─────────────────────────────────────────────────

    // TODO: This test only verifies the formatter's behavior with null fields — it does not actually
    // simulate an agent exception through ExecuteCodeReviewAsync. Add an integration-level test that
    // mocks the summary agent to throw and verifies run.CodeReviewChangeSummary/VerdictSummary remain null.
    [Fact]
    public void Format_AgentExceptionScenario_NullSummaries_NoRendering()
    {
        // Simulates: agent threw exception → both fields are null → no summary lines rendered
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "1",
            IssueTitle = "Test PR",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Review,
            CodeReviewAgentsRun = ["Correctness", "Security"],
            CodeReviewChangeSummary = null,
            CodeReviewVerdictSummary = null
        };
        run.SetCodeReviewCounts(1, 0, 0);
        run.CodeReviewAgentFindings["Correctness"] = "Found a bug";

        var result = ReviewFindingsFormatter.Format(run);

        result.Should().NotContain("**Changes**:");
        result.Should().NotContain("**Review verdict**:");
        // But findings are still rendered normally
        result.Should().Contain("[CRITICAL]");
        result.Should().Contain("Found a bug");
    }

    // ─── Backward Compatibility ─────────────────────────────────────────────────

    [Fact]
    public void CodeReviewSummary_PositionalConstructor_StillCompiles()
    {
        // Ensure existing positional call sites compile without change
        var summary = new CodeReviewSummary(
            AgentsRun: ["agent"],
            CriticalCount: 0,
            WarningCount: 0,
            SuggestionCount: 0,
            AgentFindings: []);

        summary.ChangeSummary.Should().BeNull();
        summary.VerdictSummary.Should().BeNull();
    }

    [Fact]
    public void CodeReviewSummary_WithInitProperties_SetsValues()
    {
        var summary = new CodeReviewSummary(
            AgentsRun: ["agent"],
            CriticalCount: 1,
            WarningCount: 0,
            SuggestionCount: 0,
            AgentFindings: [])
        {
            ChangeSummary = "Change summary text",
            VerdictSummary = "Verdict text"
        };

        summary.ChangeSummary.Should().Be("Change summary text");
        summary.VerdictSummary.Should().Be("Verdict text");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

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
            CodeReviewAgentsRun = ["Correctness", "DotNetSpecialist"],
            CodeReviewChangeSummary = changeSummary,
            CodeReviewVerdictSummary = verdictSummary
        };
        run.SetCodeReviewCounts(1, 2, 0);
        run.CodeReviewAgentFindings["Correctness"] = "[CRITICAL] Bug found";

        return run;
    }
}
