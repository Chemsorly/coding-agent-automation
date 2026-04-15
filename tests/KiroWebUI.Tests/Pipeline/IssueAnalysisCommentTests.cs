using FluentAssertions;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Services;

namespace KiroWebUI.Tests.Pipeline;

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
            Labels = new[] { "feature" },
            AcceptanceCriteria = Array.Empty<string>()
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
            Labels = Array.Empty<string>(),
            AcceptanceCriteria = Array.Empty<string>()
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
        md.Should().Contain("course-correct");
    }
}
