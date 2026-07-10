using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

public class ReviewSummaryFormatterTests
{
    // --- PipelineFormatting.AppendCodeReviewSection (via GeneratePrBody) ---

    [Fact]
    public void GeneratePrBody_WithVerdictSummary_RendersVerdictBeforeTable()
    {
        var review = new CodeReviewSummary(
            AgentsRun: ["security-agent"],
            CriticalCount: 1,
            WarningCount: 0,
            SuggestionCount: 0,
            AgentFindings: [new AgentFindings("security-agent", "SQL injection risk")])
        {
            VerdictSummary = "Found one critical SQL injection in UserRepository.cs."
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
            CodeReviewSummary = review,
        });

        result.Should().Contain("**Review verdict**: Found one critical SQL injection");
        // Verdict should appear before agents line
        var verdictIndex = result.IndexOf("**Review verdict**:", StringComparison.Ordinal);
        var agentsIndex = result.IndexOf("**Agents**:", StringComparison.Ordinal);
        verdictIndex.Should().BeLessThan(agentsIndex);
    }

    [Fact]
    public void GeneratePrBody_WithVerdictSummary_ZeroFindings_RendersVerdictBeforeNoFindings()
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
            TestsPassed = 0,
            TestsFailed = 0,
            TestsSkipped = 0,
            CoveragePercent = null,
            FileChanges = [],
            IssueTitle = "Test",
            CodeReviewSummary = review,
        });

        result.Should().Contain("**Review verdict**: No issues found");
        result.Should().Contain("Code review: no findings");
        // Verdict renders before "no findings" message
        var verdictIndex = result.IndexOf("**Review verdict**:", StringComparison.Ordinal);
        var noFindingsIndex = result.IndexOf("Code review: no findings", StringComparison.Ordinal);
        verdictIndex.Should().BeLessThan(noFindingsIndex);
    }

    [Fact]
    public void GeneratePrBody_WithNullSummaries_NoSummaryLinesRendered()
    {
        var review = new CodeReviewSummary(
            AgentsRun: ["agent-1"],
            CriticalCount: 1,
            WarningCount: 0,
            SuggestionCount: 0,
            AgentFindings: [new AgentFindings("agent-1", "Finding text")]);

        var result = PipelineFormatting.GeneratePrBody(new PrBodyParameters
        {
            IssueReference = "#1",
            TestsPassed = 0,
            TestsFailed = 0,
            TestsSkipped = 0,
            CoveragePercent = null,
            FileChanges = [],
            IssueTitle = "Test",
            CodeReviewSummary = review,
        });

        result.Should().NotContain("**Review verdict**:");
        result.Should().NotContain("**Changes**:");
    }

    [Fact]
    public void GeneratePrBody_WithChangeSummary_DoesNotRenderChangeSummary()
    {
        // Per spec: ChangeSummary is omitted from implementation PR body
        var review = new CodeReviewSummary(
            AgentsRun: ["agent-1"],
            CriticalCount: 0,
            WarningCount: 0,
            SuggestionCount: 0,
            AgentFindings: [])
        {
            ChangeSummary = "Added new endpoint for user profile updates.",
            VerdictSummary = "Clean implementation."
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
            CodeReviewSummary = review,
        });

        result.Should().NotContain("**Changes**:");
        result.Should().Contain("**Review verdict**:");
    }

    // --- ReviewFindingsFormatter.Format ---

    [Fact]
    public void Format_WithBothSummaries_RendersBothBeforeAgents()
    {
        var run = CreateRunWithSummaries(
            changeSummary: "Added Serilog bootstrap logger to capture early startup messages.",
            verdictSummary: "Clean implementation, one warning about missing test coverage.");

        var result = ReviewFindingsFormatter.Format(run);

        result.Should().Contain("**Changes**: Added Serilog bootstrap logger");
        result.Should().Contain("**Review verdict**: Clean implementation");
        // Both should appear before the agents line
        var changesIndex = result.IndexOf("**Changes**:", StringComparison.Ordinal);
        var verdictIndex = result.IndexOf("**Review verdict**:", StringComparison.Ordinal);
        var agentsIndex = result.IndexOf("**Review Agents**:", StringComparison.Ordinal);
        changesIndex.Should().BeLessThan(agentsIndex);
        verdictIndex.Should().BeLessThan(agentsIndex);
    }

    [Fact]
    public void Format_WithNullSummaries_NoSummaryLinesRendered()
    {
        var run = CreateRunWithSummaries(changeSummary: null, verdictSummary: null);

        var result = ReviewFindingsFormatter.Format(run);

        result.Should().NotContain("**Changes**:");
        result.Should().NotContain("**Review verdict**:");
    }

    [Fact]
    public void Format_WithOnlyVerdict_RendersVerdictWithoutChanges()
    {
        // TODO: [REV-04] Test does not verify ordering of verdict relative to agents line.
        //   Other tests validate ordering but this partial-summary scenario could silently regress
        //   to rendering verdict AFTER the agents line without detection. Consider adding an ordering assertion.
        var run = CreateRunWithSummaries(changeSummary: null, verdictSummary: "No issues found.");

        var result = ReviewFindingsFormatter.Format(run);

        result.Should().NotContain("**Changes**:");
        result.Should().Contain("**Review verdict**: No issues found.");
    }

    [Fact]
    public void Format_LongSummary_TruncatesAt500Chars()
    {
        var longSummary = new string('a', 200) + ". " + new string('b', 400);
        var run = CreateRunWithSummaries(changeSummary: longSummary, verdictSummary: null);

        var result = ReviewFindingsFormatter.Format(run);

        // The rendered line should be shorter than original
        result.Should().Contain("**Changes**:");
        result.Should().Contain("...");
        // The long original should NOT appear in full
        result.Should().NotContain(longSummary);
    }

    private static PipelineRun CreateRunWithSummaries(string? changeSummary, string? verdictSummary)
    {
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "1",
            IssueTitle = "Test PR",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            CodeReviewChangeSummary = changeSummary,
            CodeReviewVerdictSummary = verdictSummary,
            CodeReviewAgentsRun = ["TestAgent"]
        };
        run.CodeReviewAgentFindings.TryAdd("TestAgent", "[WARNING] Some finding");
        return run;
    }
}
