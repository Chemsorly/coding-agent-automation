using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class IssueAnalysisCommentTests
{
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
