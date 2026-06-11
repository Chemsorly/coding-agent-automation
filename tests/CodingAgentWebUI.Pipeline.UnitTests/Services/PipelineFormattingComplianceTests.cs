using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class PipelineFormattingComplianceTests
{
    [Fact]
    public void GeneratePrBody_WithComplianceReport_RendersTable()
    {
        var report = new AcceptanceCriteriaReport
        {
            Criteria = new[]
            {
                new CriterionResult { Criterion = "REST endpoint works", Status = CriterionStatus.Compliant, Evidence = "Implemented in Controller.cs" },
                new CriterionResult { Criterion = "Filtering by role", Status = CriterionStatus.NonCompliant, Reasoning = "Not implemented" },
                new CriterionResult { Criterion = "Schema migration", Status = CriterionStatus.NotApplicable, Reasoning = "Out of scope" }
            },
            Summary = "1 of 3 criteria addressed."
        };

        var body = PipelineFormatting.GeneratePrBody(
            "#42", 10, 0, 0, 95.0, [], "Test Issue",
            complianceReport: report);

        body.Should().Contain("## Acceptance Criteria Compliance");
        body.Should().Contain("| ✅ | REST endpoint works | Implemented in Controller.cs |");
        body.Should().Contain("| ❌ | Filtering by role | Not implemented |");
        body.Should().Contain("| ⚠️ | Schema migration | Out of scope |");
        body.Should().Contain("*1 of 3 criteria addressed.*");
    }

    [Fact]
    public void GeneratePrBody_NullReport_NoComplianceSection()
    {
        var body = PipelineFormatting.GeneratePrBody(
            "#42", 10, 0, 0, 95.0, [], "Test Issue",
            complianceReport: null);

        body.Should().NotContain("Acceptance Criteria Compliance");
    }

    [Fact]
    public void GeneratePrBody_EmptyCriteria_NoComplianceSection()
    {
        var report = new AcceptanceCriteriaReport
        {
            Criteria = [],
            Summary = "No acceptance criteria found."
        };

        var body = PipelineFormatting.GeneratePrBody(
            "#42", 10, 0, 0, 95.0, [], "Test Issue",
            complianceReport: report);

        body.Should().NotContain("Acceptance Criteria Compliance");
    }

    [Fact]
    public void ReviewFindingsFormatter_WithComplianceReport_RendersSection()
    {
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AcceptanceCriteriaReport = new AcceptanceCriteriaReport
            {
                Criteria = new[]
                {
                    new CriterionResult { Criterion = "Feature X works", Status = CriterionStatus.Compliant, Evidence = "Tests pass" }
                },
                Summary = "1 of 1 criteria addressed."
            }
        };

        var result = ReviewFindingsFormatter.Format(run);

        result.Should().Contain("### 📋 Acceptance Criteria Compliance");
        result.Should().Contain("| ✅ | Feature X works | Tests pass |");
        result.Should().Contain("*1 of 1 criteria addressed.*");
    }

    [Fact]
    public void ReviewFindingsFormatter_NullReport_NoSection()
    {
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            AcceptanceCriteriaReport = null
        };

        var result = ReviewFindingsFormatter.Format(run);

        result.Should().NotContain("Acceptance Criteria Compliance");
    }
}
