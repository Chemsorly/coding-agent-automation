using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for the analysis confidence gate: AnalysisAssessment deserialization,
/// comment builders, and gate decision logic in AgentPhaseExecutor.
/// </summary>
public class AnalysisConfidenceGateTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // --- AnalysisAssessment deserialization ---

    [Fact]
    public void AnalysisAssessment_Deserializes_ReadyRecommendation()
    {
        var json = """
        {
            "recommendation": "ready",
            "reason": "Issue is well-scoped",
            "concerns": ["Minor concern"],
            "blockingIssues": [],
            "plannedApproach": "Add retry logic",
            "estimatedComplexity": "moderate"
        }
        """;

        var assessment = JsonSerializer.Deserialize<AnalysisAssessment>(json, JsonOptions)!;

        assessment.Recommendation.Should().Be("ready");
        assessment.Reason.Should().Be("Issue is well-scoped");
        assessment.Concerns.Should().ContainSingle().Which.Should().Be("Minor concern");
        assessment.BlockingIssues.Should().BeEmpty();
        assessment.PlannedApproach.Should().Be("Add retry logic");
        assessment.EstimatedComplexity.Should().Be("moderate");
    }

    [Fact]
    public void AnalysisAssessment_Deserializes_NotReadyRecommendation()
    {
        var json = """
        {
            "recommendation": "not_ready",
            "reason": "Issue is too vague",
            "concerns": [],
            "blockingIssues": ["No acceptance criteria", "Contradictory requirements"]
        }
        """;

        var assessment = JsonSerializer.Deserialize<AnalysisAssessment>(json, JsonOptions)!;

        assessment.Recommendation.Should().Be("not_ready");
        assessment.BlockingIssues.Should().HaveCount(2);
    }

    [Fact]
    public void AnalysisAssessment_Deserializes_WontDoRecommendation()
    {
        var json = """
        {
            "recommendation": "wont_do",
            "reason": "Bug is already fixed in PR #134",
            "concerns": [],
            "blockingIssues": []
        }
        """;

        var assessment = JsonSerializer.Deserialize<AnalysisAssessment>(json, JsonOptions)!;

        assessment.Recommendation.Should().Be("wont_do");
        assessment.Reason.Should().Contain("already fixed");
    }

    [Fact]
    public void AnalysisAssessment_Deserializes_MinimalJson()
    {
        var json = """{"recommendation": "ready"}""";

        var assessment = JsonSerializer.Deserialize<AnalysisAssessment>(json, JsonOptions)!;

        assessment.Recommendation.Should().Be("ready");
        assessment.Reason.Should().BeNull();
        assessment.Concerns.Should().BeEmpty();
        assessment.BlockingIssues.Should().BeEmpty();
        assessment.PlannedApproach.Should().BeNull();
        assessment.EstimatedComplexity.Should().BeNull();
    }

    // --- BuildNotReadyComment ---

    [Fact]
    public void BuildNotReadyComment_IncludesBlockingIssuesAndConcerns()
    {
        var assessment = new AnalysisAssessment
        {
            Recommendation = "not_ready",
            Reason = "Issue needs more detail",
            BlockingIssues = new[] { "No AC", "Missing context" },
            Concerns = new[] { "Might affect perf" }
        };

        var comment = AgentPhaseExecutor.BuildNotReadyComment(assessment);

        comment.Should().Contain("## ⚠️ Analysis Gate: Needs Refinement");
        comment.Should().Contain("Issue needs more detail");
        comment.Should().Contain("### Blocking Issues");
        comment.Should().Contain("- No AC");
        comment.Should().Contain("- Missing context");
        comment.Should().Contain("### Concerns");
        comment.Should().Contain("- Might affect perf");
        comment.Should().Contain("agent:needs-refinement");
        comment.Should().Contain("<!-- agent:gate-rejection -->");
    }

    [Fact]
    public void BuildNotReadyComment_WithNoBlockingIssues_OmitsSection()
    {
        var assessment = new AnalysisAssessment
        {
            Recommendation = "not_ready",
            Reason = "Vague issue"
        };

        var comment = AgentPhaseExecutor.BuildNotReadyComment(assessment);

        comment.Should().NotContain("### Blocking Issues");
        comment.Should().Contain("<!-- agent:gate-rejection -->");
    }

    // --- BuildWontDoComment ---

    [Fact]
    public void BuildWontDoComment_IncludesReasonAndConcerns()
    {
        var assessment = new AnalysisAssessment
        {
            Recommendation = "wont_do",
            Reason = "Already fixed in PR #134",
            Concerns = new[] { "Related test coverage is thin" }
        };

        var comment = AgentPhaseExecutor.BuildWontDoComment(assessment);

        comment.Should().Contain("## 🚫 Analysis Gate: Won't Do");
        comment.Should().Contain("Already fixed in PR #134");
        comment.Should().Contain("### Concerns");
        comment.Should().Contain("- Related test coverage is thin");
        comment.Should().Contain("agent:wont-do");
        comment.Should().Contain("<!-- agent:gate-wont-do -->");
    }

    [Fact]
    public void BuildWontDoComment_WithNoConcerns_OmitsSection()
    {
        var assessment = new AnalysisAssessment
        {
            Recommendation = "wont_do",
            Reason = "Feature already implemented"
        };

        var comment = AgentPhaseExecutor.BuildWontDoComment(assessment);

        comment.Should().NotContain("### Concerns");
        comment.Should().Contain("<!-- agent:gate-wont-do -->");
    }

    // --- ExcludedCommentMarkers ---

    [Fact]
    public void ExcludedCommentMarkers_ContainsGateMarkers()
    {
        PromptBuilder.ExcludedCommentMarkers.Should().Contain("<!-- agent:gate-rejection -->");
        PromptBuilder.ExcludedCommentMarkers.Should().Contain("<!-- agent:gate-wont-do -->");
    }

    // --- PromptBuilder constants ---

    [Fact]
    public void AnalysisAssessmentFilePath_IsCorrect()
    {
        PromptBuilder.AnalysisAssessmentFilePath.Should().Be(".agent/analysis-assessment.json");
    }

    [Fact]
    public void BuildAnalysisPrompt_IncludesAssessmentInstructions()
    {
        var issue = new IssueDetail { Identifier = "1", Title = "Test", Description = "Desc", Labels = Array.Empty<string>() };
        var parsed = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = Array.Empty<string>() };

        var prompt = PromptBuilder.BuildAnalysisPrompt(PipelineConfiguration.DefaultAnalysisPrompt, issue, parsed);

        prompt.Should().Contain("analysis-assessment.json");
        prompt.Should().Contain("\"ready\"");
        prompt.Should().Contain("\"not_ready\"");
        prompt.Should().Contain("\"wont_do\"");
        prompt.Should().Contain("plannedApproach");
        prompt.Should().Contain("estimatedComplexity");
    }

    // --- AgentLabels ---

    [Fact]
    public void AgentLabels_WontDo_IsCorrect()
    {
        AgentLabels.WontDo.Should().Be("agent:wont-do");
        AgentLabels.All.Should().Contain("agent:wont-do");
        AgentLabels.Definitions.Should().Contain(d => d.Name == "agent:wont-do");
    }

    // --- PipelineRun tracking fields ---

    [Fact]
    public void PipelineRun_AnalysisFields_DefaultCorrectly()
    {
        var run = new PipelineRun
        {
            RunId = "test",
            IssueIdentifier = "1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };

        run.AnalysisRecommendation.Should().BeNull();
        run.AnalysisConcerns.Should().BeEmpty();
        run.AnalysisBlockingIssues.Should().BeEmpty();
    }

    [Fact]
    public void PipelineRunSummary_IncludesAnalysisRecommendation()
    {
        var run = new PipelineRun
        {
            RunId = "test",
            IssueIdentifier = "1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            AnalysisRecommendation = "wont_do"
        };

        var summary = run.ToSummary();
        summary.AnalysisRecommendation.Should().Be("wont_do");
    }
}
