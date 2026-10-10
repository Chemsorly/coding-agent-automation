using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

public class RefactoringProposalDeserializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void Deserialize_WithAllNewFields_PopulatesCorrectly()
    {
        var json = """
            [
                {
                    "title": "Extract validation",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Extract shared logic",
                    "rationale": "DRY violation",
                    "prerequisites": ["Add tests for A.cs", "Review B.cs"],
                    "estimatedEffort": "medium",
                    "riskLevel": "low",
                    "technique": "Extract Method",
                    "category": "bug"
                }
            ]
            """;

        var proposals = JsonSerializer.Deserialize<List<RefactoringProposal>>(json, JsonOptions);

        proposals.Should().HaveCount(1);
        var p = proposals![0];
        p.Prerequisites.Should().BeEquivalentTo(["Add tests for A.cs", "Review B.cs"]);
        p.EstimatedEffort.Should().Be("medium");
        p.RiskLevel.Should().Be("low");
        p.Technique.Should().Be("Extract Method");
        p.Category.Should().Be("bug");
    }

    [Fact]
    public void Deserialize_WithoutNewFields_NewFieldsAreNull()
    {
        var json = """
            [
                {
                    "title": "Rename methods",
                    "affectedFiles": ["src/X.cs"],
                    "description": "Inconsistent naming",
                    "rationale": "Convention violation"
                }
            ]
            """;

        var proposals = JsonSerializer.Deserialize<List<RefactoringProposal>>(json, JsonOptions);

        proposals.Should().HaveCount(1);
        var p = proposals![0];
        p.Prerequisites.Should().BeNull();
        p.EstimatedEffort.Should().BeNull();
        p.RiskLevel.Should().BeNull();
        p.Technique.Should().BeNull();
        p.Category.Should().BeNull();
    }

    [Fact]
    public void Deserialize_WithUnknownDependsOnField_IgnoresFieldAndPreservesOtherValues()
    {
        // Requirement 5: backward compatibility with older agent output that still emits "dependsOn".
        // System.Text.Json silently drops unknown JSON properties by default (this is the .NET
        // framework default, not a feature specific to PipelineJsonOptions.Lenient). Using
        // PipelineJsonOptions.Lenient here confirms the actual parser path also tolerates it.
        var json = """
            [
                {
                    "title": "Extract class from service",
                    "affectedFiles": ["src/Service.cs"],
                    "description": "Extract IDispatchRunCreator",
                    "rationale": "Too many responsibilities",
                    "dependsOn": ["Remove dead code cluster"]
                }
            ]
            """;

        var proposals = JsonSerializer.Deserialize<List<RefactoringProposal>>(json, PipelineJsonOptions.Lenient);

        proposals.Should().HaveCount(1);
        var p = proposals![0];
        p.Title.Should().Be("Extract class from service");
        p.AffectedFiles.Should().BeEquivalentTo(["src/Service.cs"]);
        p.Description.Should().Be("Extract IDispatchRunCreator");
        p.Rationale.Should().Be("Too many responsibilities");
        // TODO: The four assertions above cover scalar fields only. A corruption where the unknown
        // "dependsOn" value leaks into a list-typed field (e.g., Prerequisites or EvidenceSources)
        // would pass undetected. Add p.Prerequisites.Should().BeNull() and
        // p.EvidenceSources.Should().BeNull() to close this gap.
    }

    [Fact]
    public void Deserialize_WithAcceptanceCriteria_PopulatesCorrectly()
    {
        var json = """
            [
                {
                    "title": "Extract validation",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Extract shared logic",
                    "rationale": "DRY violation",
                    "acceptanceCriteria": ["Zero references to old name remain", "New class registered in DI"]
                }
            ]
            """;

        var proposals = JsonSerializer.Deserialize<List<RefactoringProposal>>(json, JsonOptions);

        proposals.Should().HaveCount(1);
        proposals![0].AcceptanceCriteria.Should().BeEquivalentTo(["Zero references to old name remain", "New class registered in DI"]);
    }

    [Fact]
    public void Deserialize_WithoutAcceptanceCriteria_FieldIsNull()
    {
        var json = """
            [
                {
                    "title": "Simple rename",
                    "affectedFiles": ["src/X.cs"],
                    "description": "Rename method",
                    "rationale": "Naming convention"
                }
            ]
            """;

        var proposals = JsonSerializer.Deserialize<List<RefactoringProposal>>(json, JsonOptions);

        proposals.Should().HaveCount(1);
        proposals![0].AcceptanceCriteria.Should().BeNull();
    }

    [Fact]
    public void Deserialize_WithEmptyAcceptanceCriteria_PopulatesEmptyList()
    {
        var json = """
            [
                {
                    "title": "Independent refactoring",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Standalone change",
                    "rationale": "No criteria needed",
                    "acceptanceCriteria": []
                }
            ]
            """;

        var proposals = JsonSerializer.Deserialize<List<RefactoringProposal>>(json, JsonOptions);

        proposals.Should().HaveCount(1);
        proposals![0].AcceptanceCriteria.Should().BeEmpty();
    }
}
