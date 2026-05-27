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
}
