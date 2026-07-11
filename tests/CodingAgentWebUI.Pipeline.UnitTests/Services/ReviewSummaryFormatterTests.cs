using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class ReviewSummaryFormatterTests
{
    // --- PipelineFormatting.AppendCodeReviewSection tests ---

    [Fact]
    public void AppendCodeReviewSection_WithVerdictSummary_RendersVerdictBeforeTable()
    {
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
        {
            IssueReference = "#42",
            TestsPassed = 10,
            TestsFailed = 0,
            TestsSkipped = 0,
            CoveragePercent = null,
            FileChanges = Array.Empty<FileChangeSummary>(),
            IssueTitle = "Fix bug",
            CodeReviewSummary = new CodeReviewSummary(
                AgentsRun: new[] { "Correctness" },
                CriticalCount: 1,
                WarningCount: 0,
                SuggestionCount: 0,
                AgentFindings: new[] { new AgentFindings("Correctness", "Found a race condition") })
            {
                VerdictSummary = "Found 1 race condition in the drain service. Critical fixed."
            },
        });

        body.Should().Contain("**Review verdict**: Found 1 race condition in the drain service. Critical fixed.");
        // Verdict should appear before the agents line
        var verdictIndex = body.IndexOf("**Review verdict**:", StringComparison.Ordinal);
        var agentsIndex = body.IndexOf("**Agents**: Correctness", StringComparison.Ordinal);
        verdictIndex.Should().BeLessThan(agentsIndex);
    }

    [Fact]
    public void AppendCodeReviewSection_WithVerdictSummary_NoFindings_RendersVerdictBeforeNoFindingsMessage()
    {
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
        {
            IssueReference = "#42",
            TestsPassed = 10,
            TestsFailed = 0,
            TestsSkipped = 0,
            CoveragePercent = null,
            FileChanges = Array.Empty<FileChangeSummary>(),
            IssueTitle = "Fix bug",
            CodeReviewSummary = new CodeReviewSummary(
                AgentsRun: new[] { "Correctness" },
                CriticalCount: 0,
                WarningCount: 0,
                SuggestionCount: 0,
                AgentFindings: Array.Empty<AgentFindings>())
            {
                VerdictSummary = "No issues found, implementation follows standard patterns."
            },
        });

        body.Should().Contain("**Review verdict**: No issues found, implementation follows standard patterns.");
        body.Should().Contain("Code review: no findings");
        // Verdict before no-findings message
        var verdictIndex = body.IndexOf("**Review verdict**:", StringComparison.Ordinal);
        var noFindingsIndex = body.IndexOf("Code review: no findings", StringComparison.Ordinal);
        verdictIndex.Should().BeLessThan(noFindingsIndex);
    }

    [Fact]
    public void AppendCodeReviewSection_NullVerdictSummary_DoesNotRenderVerdict()
    {
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
        {
            IssueReference = "#42",
            TestsPassed = 10,
            TestsFailed = 0,
            TestsSkipped = 0,
            CoveragePercent = null,
            FileChanges = Array.Empty<FileChangeSummary>(),
            IssueTitle = "Fix bug",
            CodeReviewSummary = new CodeReviewSummary(
                AgentsRun: new[] { "Correctness" },
                CriticalCount: 1,
                WarningCount: 0,
                SuggestionCount: 0,
                AgentFindings: new[] { new AgentFindings("Correctness", "Found issue") })
            {
                VerdictSummary = null
            },
        });

        body.Should().NotContain("**Review verdict**:");
    }

    [Fact]
    public void AppendCodeReviewSection_DoesNotRenderChangeSummary()
    {
        // Implementation PR body should NOT render ChangeSummary (it's already in the blockquote)
        var body = PipelineFormatting.GeneratePrBody(new PrBodyParameters
        {
            IssueReference = "#42",
            TestsPassed = 10,
            TestsFailed = 0,
            TestsSkipped = 0,
            CoveragePercent = null,
            FileChanges = Array.Empty<FileChangeSummary>(),
            IssueTitle = "Fix bug",
            CodeReviewSummary = new CodeReviewSummary(
                AgentsRun: new[] { "Correctness" },
                CriticalCount: 0,
                WarningCount: 0,
                SuggestionCount: 0,
                AgentFindings: Array.Empty<AgentFindings>())
            {
                ChangeSummary = "Added pagination to the users endpoint.",
                VerdictSummary = "Clean implementation."
            },
        });

        body.Should().NotContain("**Changes**:");
    }

    // --- ReviewFindingsFormatter.Format tests ---

    [Fact]
    public void ReviewFindingsFormatter_WithBothSummaries_RendersChangesAndVerdict()
    {
        var run = CreateTestRun();
        run.CodeReviewChangeSummary = "Added Serilog bootstrap logger to capture early startup messages.";
        run.CodeReviewVerdictSummary = "Clean implementation. One warning about missing test coverage.";
        run.CodeReviewAgentsRun = new[] { "Correctness", "SecurityReviewer" };

        var result = ReviewFindingsFormatter.Format(run);

        result.Should().Contain("**Changes**: Added Serilog bootstrap logger to capture early startup messages.");
        result.Should().Contain("**Review verdict**: Clean implementation. One warning about missing test coverage.");
        // Both should appear before the agents line
        var changesIndex = result.IndexOf("**Changes**:", StringComparison.Ordinal);
        var verdictIndex = result.IndexOf("**Review verdict**:", StringComparison.Ordinal);
        var agentsIndex = result.IndexOf("**Review Agents**:", StringComparison.Ordinal);
        changesIndex.Should().BeLessThan(agentsIndex);
        verdictIndex.Should().BeLessThan(agentsIndex);
    }

    [Fact]
    public void ReviewFindingsFormatter_NullSummaries_DoesNotRenderSummaryLines()
    {
        var run = CreateTestRun();
        run.CodeReviewChangeSummary = null;
        run.CodeReviewVerdictSummary = null;
        run.CodeReviewAgentsRun = new[] { "Correctness" };

        var result = ReviewFindingsFormatter.Format(run);

        result.Should().NotContain("**Changes**:");
        result.Should().NotContain("**Review verdict**:");
        result.Should().Contain("**Review Agents**: Correctness");
    }

    [Fact]
    public void ReviewFindingsFormatter_OnlyVerdictSummary_RendersVerdictOnly()
    {
        var run = CreateTestRun();
        run.CodeReviewChangeSummary = null;
        run.CodeReviewVerdictSummary = "No issues found.";
        run.CodeReviewAgentsRun = new[] { "Correctness" };

        var result = ReviewFindingsFormatter.Format(run);

        result.Should().NotContain("**Changes**:");
        result.Should().Contain("**Review verdict**: No issues found.");
    }

    [Fact]
    public void ReviewFindingsFormatter_LongSummary_TruncatesAtSentenceBoundary()
    {
        var run = CreateTestRun();
        run.CodeReviewChangeSummary = "First sentence. " + new string('x', 500);
        run.CodeReviewAgentsRun = new[] { "Correctness" };

        var result = ReviewFindingsFormatter.Format(run);

        result.Should().Contain("**Changes**: First sentence....");
        // The raw long text should NOT appear in full
        result.Should().NotContain(new string('x', 500));
    }

    // --- Non-fatal failure path ---

    [Fact]
    public void ReviewFindingsFormatter_NullSummaries_BackwardCompat_NoSummaryLines()
    {
        // Simulates the scenario where agent exception occurred → summaries are null
        var run = CreateTestRun();
        run.CodeReviewChangeSummary = null;
        run.CodeReviewVerdictSummary = null;
        run.CodeReviewAgentsRun = new[] { "Correctness", "DotNetSpecialist" };
        run.CodeReviewAgentFindings["Correctness"] = "[CRITICAL] Found an issue";

        var result = ReviewFindingsFormatter.Format(run);

        // Should still render normally without summary lines
        result.Should().Contain("## 🤖 Automated Code Review");
        result.Should().Contain("**Review Agents**: Correctness, DotNetSpecialist");
        result.Should().Contain("[CRITICAL]");
        result.Should().NotContain("**Changes**:");
        result.Should().NotContain("**Review verdict**:");
    }

    private static PipelineRun CreateTestRun() => new()
    {
        RunId = "test-run",
        IssueIdentifier = "42",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1"
    };
}
