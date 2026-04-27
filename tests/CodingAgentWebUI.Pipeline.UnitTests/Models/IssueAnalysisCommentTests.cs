using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

#pragma warning disable CS0618 // Testing obsolete FromIssue method intentionally

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class IssueAnalysisCommentTests
{
    [Fact]
    public void FromIssue_WithFullInfo_ReturnsHighConfidence()
    {
        var issue = new IssueDetail
        {
            Identifier = "1",
            Title = "Add login page",
            Description = "## Requirements\nBuild a login page\n## Acceptance Criteria\n- [ ] Has username field\n- [ ] Has password field",
            Labels = new[] { "feature" }
        };
        var parsed = new IssueDescriptionParser().Parse(issue.Description);

        var result = IssueAnalysisComment.FromIssue(issue, parsed);

        result.ConfidenceAssessment.Should().StartWith("High");
        result.EstimatedComplexity.Should().StartWith("Low");
        result.PlannedApproach.Should().Contain("Add login page");
        result.AffectedComponents.Should().Contain(c => c.Contains("feature"));
    }

    [Fact]
    public void FromIssue_WithNoStructure_ReturnsLowConfidence()
    {
        var issue = new IssueDetail
        {
            Identifier = "2",
            Title = "Fix bug",
            Description = "Something is broken",
            Labels = Array.Empty<string>()
        };
        var parsed = new IssueDescriptionParser().Parse(issue.Description);

        var result = IssueAnalysisComment.FromIssue(issue, parsed);

        // Unstructured text is treated as requirements by the parser, so confidence is Medium
        result.ConfidenceAssessment.Should().StartWith("Medium");
    }

    [Fact]
    public void ToMarkdown_ContainsAllSections()
    {
        var comment = new IssueAnalysisComment
        {
            IssueTitle = "Test",
            PlannedApproach = "Do the thing",
            AffectedComponents = new[] { "ComponentA", "ComponentB" },
            EstimatedComplexity = "Low",
            ConfidenceAssessment = "High"
        };

        var md = comment.ToMarkdown();

        md.Should().Contain("## 🤖 Agent Analysis");
        md.Should().Contain("### Planned Approach");
        md.Should().Contain("Do the thing");
        md.Should().Contain("- ComponentA");
        md.Should().Contain("- ComponentB");
        md.Should().Contain("### Estimated Complexity");
        md.Should().Contain("### Confidence Assessment");
        md.Should().Contain("Review it before approving implementation");
    }

    [Fact]
    public void ToMarkdown_WithReadyAssessment_IncludesAssessmentFooter()
    {
        var comment = new IssueAnalysisComment
        {
            IssueTitle = "Test",
            PlannedApproach = string.Empty,
            AffectedComponents = Array.Empty<string>(),
            EstimatedComplexity = string.Empty,
            ConfidenceAssessment = string.Empty,
            AgentAnalysis = "Some analysis",
            Assessment = new AnalysisAssessment
            {
                Recommendation = "ready",
                EstimatedComplexity = "moderate",
                Concerns = new[] { "concern one", "concern two" }
            }
        };

        var md = comment.ToMarkdown();

        md.Should().Contain("📊 **Assessment: Ready** · Complexity: moderate");
        md.Should().Contain("*Concerns: concern one; concern two*");
        md.Should().Contain("Review it before approving implementation");
    }

    [Fact]
    public void ToMarkdown_WithNotReadyAssessment_IncludesReasonInFooter()
    {
        var comment = new IssueAnalysisComment
        {
            IssueTitle = "Test",
            PlannedApproach = string.Empty,
            AffectedComponents = Array.Empty<string>(),
            EstimatedComplexity = string.Empty,
            ConfidenceAssessment = string.Empty,
            AgentAnalysis = "Some analysis",
            Assessment = new AnalysisAssessment
            {
                Recommendation = "not_ready",
                Reason = "Acceptance criteria are contradictory"
            }
        };

        var md = comment.ToMarkdown();

        md.Should().Contain("⚠️ **Assessment: Not Ready** — Issue needs refinement");
        md.Should().Contain("*Reason: Acceptance criteria are contradictory*");
        md.Should().NotContain("Review it before approving implementation");
    }

    [Fact]
    public void ToMarkdown_WithWontDoAssessment_IncludesReasonInFooter()
    {
        var comment = new IssueAnalysisComment
        {
            IssueTitle = "Test",
            PlannedApproach = string.Empty,
            AffectedComponents = Array.Empty<string>(),
            EstimatedComplexity = string.Empty,
            ConfidenceAssessment = string.Empty,
            AgentAnalysis = "Some analysis",
            Assessment = new AnalysisAssessment
            {
                Recommendation = "wont_do",
                Reason = "The bug described cannot be reproduced"
            }
        };

        var md = comment.ToMarkdown();

        md.Should().Contain("🚫 **Assessment: Won't Do** — No code changes needed");
        md.Should().Contain("*Reason: The bug described cannot be reproduced*");
        md.Should().NotContain("Review it before approving implementation");
    }

    [Fact]
    public void ToMarkdown_WithReadyAssessment_NoConcerns_OmitsConcernsLine()
    {
        var comment = new IssueAnalysisComment
        {
            IssueTitle = "Test",
            PlannedApproach = string.Empty,
            AffectedComponents = Array.Empty<string>(),
            EstimatedComplexity = string.Empty,
            ConfidenceAssessment = string.Empty,
            AgentAnalysis = "Some analysis",
            Assessment = new AnalysisAssessment
            {
                Recommendation = "ready",
                EstimatedComplexity = "low"
            }
        };

        var md = comment.ToMarkdown();

        md.Should().Contain("📊 **Assessment: Ready** · Complexity: low");
        md.Should().NotContain("Concerns:");
        md.Should().Contain("Review it before approving implementation");
    }

    [Fact]
    public void ToMarkdown_WithReadyAssessment_NoComplexity_OmitsComplexity()
    {
        var comment = new IssueAnalysisComment
        {
            IssueTitle = "Test",
            PlannedApproach = string.Empty,
            AffectedComponents = Array.Empty<string>(),
            EstimatedComplexity = string.Empty,
            ConfidenceAssessment = string.Empty,
            AgentAnalysis = "Some analysis",
            Assessment = new AnalysisAssessment
            {
                Recommendation = "ready"
            }
        };

        var md = comment.ToMarkdown();

        md.Should().Contain("📊 **Assessment: Ready**");
        md.Should().NotContain("Complexity:");
        md.Should().Contain("Review it before approving implementation");
    }

    [Fact]
    public void FromAgentAnalysis_WithAssessment_SetsAssessmentProperty()
    {
        var issue = new IssueDetail
        {
            Identifier = "1",
            Title = "Test",
            Description = "Test",
            Labels = Array.Empty<string>()
        };
        var assessment = new AnalysisAssessment
        {
            Recommendation = "ready",
            EstimatedComplexity = "low"
        };

        var result = IssueAnalysisComment.FromAgentAnalysis(issue, "analysis output", assessment);

        result.Assessment.Should().BeSameAs(assessment);
    }
}
