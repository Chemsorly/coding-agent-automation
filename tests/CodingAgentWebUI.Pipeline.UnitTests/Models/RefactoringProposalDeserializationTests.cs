using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

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
    public void Deserialize_WithDependsOn_PopulatesTitleList()
    {
        var json = """
            [
                {
                    "title": "Extract class from service",
                    "affectedFiles": ["src/Service.cs"],
                    "description": "Extract IDispatchRunCreator",
                    "rationale": "Too many responsibilities",
                    "dependsOn": ["Remove dead code cluster", "Extract shared run-metadata resolution"]
                }
            ]
            """;

        var proposals = JsonSerializer.Deserialize<List<RefactoringProposal>>(json, JsonOptions);

        proposals.Should().HaveCount(1);
        var p = proposals![0];
        p.DependsOn.Should().BeEquivalentTo(["Remove dead code cluster", "Extract shared run-metadata resolution"]);
    }

    [Fact]
    public void Deserialize_WithoutDependsOn_FieldIsNull()
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
        proposals![0].DependsOn.Should().BeNull();
    }

    [Fact]
    public void Deserialize_WithEmptyDependsOn_PopulatesEmptyList()
    {
        var json = """
            [
                {
                    "title": "Independent refactoring",
                    "affectedFiles": ["src/A.cs"],
                    "description": "Standalone change",
                    "rationale": "No dependencies",
                    "dependsOn": []
                }
            ]
            """;

        var proposals = JsonSerializer.Deserialize<List<RefactoringProposal>>(json, JsonOptions);

        proposals.Should().HaveCount(1);
        proposals![0].DependsOn.Should().BeEmpty();
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
