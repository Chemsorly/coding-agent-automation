using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Prompts;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for <see cref="DecompositionPromptBuilder"/> verifying prompt structure,
/// parameterization, and cross-repo extension behavior.
/// </summary>
public class DecompositionPromptBuilderTests
{
    // ── BuildAnalysisPrompt ──────────────────────────────────────────────

    [Fact]
    public void BuildAnalysisPrompt_ContainsMaxSubIssuesConstraint()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(7);
        prompt.Should().Contain("at most **7**");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsRequiredSections()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5);

        prompt.Should().Contain("# Epic Decomposition Analysis");
        prompt.Should().Contain("## Exploration Strategy");
        prompt.Should().Contain("## Deduplication Check");
        prompt.Should().Contain("## Sub-Issue Sizing Constraints");
        prompt.Should().Contain("## Output");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsFileLimit()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5);
        prompt.Should().Contain("5 files");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsDependencyOrdering()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5);
        prompt.Should().Contain("dependencies always point backward");
    }

    [Fact]
    public void BuildAnalysisPrompt_DifferentMaxSubIssues_ProducesDifferentContent()
    {
        var prompt3 = DecompositionPromptBuilder.BuildAnalysisPrompt(3);
        var prompt10 = DecompositionPromptBuilder.BuildAnalysisPrompt(10);

        prompt3.Should().Contain("at most **3**");
        prompt10.Should().Contain("at most **10**");
        prompt3.Should().NotBe(prompt10);
    }

    [Fact]
    public void BuildAnalysisPrompt_WithNullProjectContext_ReturnsSameAsWithout()
    {
        var without = DecompositionPromptBuilder.BuildAnalysisPrompt(5);
        var withNull = DecompositionPromptBuilder.BuildAnalysisPrompt(5, null);

        withNull.Should().Be(without);
    }

    [Fact]
    public void BuildAnalysisPrompt_WithProjectContext_AppendsCrossRepoInstructions()
    {
        var context = CreateTestProjectContext();
        var withContext = DecompositionPromptBuilder.BuildAnalysisPrompt(5, context);
        var without = DecompositionPromptBuilder.BuildAnalysisPrompt(5);

        withContext.Length.Should().BeGreaterThan(without.Length);
        withContext.Should().StartWith(without);
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsGateRejectionSection()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5);
        prompt.Should().Contain("## Gate Rejection Concerns");
        prompt.Should().Contain("agent:gate-rejection");
    }

    // ── BuildDecompositionPrompt ─────────────────────────────────────────

    [Fact]
    public void BuildDecompositionPrompt_ContainsMaxSubIssuesConstraint()
    {
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(8);
        prompt.Should().Contain("at most **8**");
    }

    [Fact]
    public void BuildDecompositionPrompt_ContainsJsonSchema()
    {
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(5);

        prompt.Should().Contain("\"title\":");
        prompt.Should().Contain("\"body\":");
        prompt.Should().Contain("\"dependencies\":");
        prompt.Should().Contain("\"labels\":");
    }

    [Fact]
    public void BuildDecompositionPrompt_ContainsRequiredSections()
    {
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(5);

        prompt.Should().Contain("# Epic Decomposition — Sub-Issue Generation");
        prompt.Should().Contain("## Context");
        prompt.Should().Contain("## Output Format");
        prompt.Should().Contain("## Issue Body Template");
        prompt.Should().Contain("## Dependency Ordering");
        prompt.Should().Contain("## Constraints");
    }

    [Fact]
    public void BuildDecompositionPrompt_ContainsIssueBodyTemplateSections()
    {
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(5);

        prompt.Should().Contain("## Summary");
        prompt.Should().Contain("## Affected Components");
        prompt.Should().Contain("## Requirements");
        prompt.Should().Contain("## Acceptance Criteria");
    }

    [Fact]
    public void BuildDecompositionPrompt_WithNullProjectContext_ReturnsSameAsWithout()
    {
        var without = DecompositionPromptBuilder.BuildDecompositionPrompt(5);
        var withNull = DecompositionPromptBuilder.BuildDecompositionPrompt(5, null);

        withNull.Should().Be(without);
    }

    [Fact]
    public void BuildDecompositionPrompt_WithProjectContext_AppendsRoutingInstructions()
    {
        var context = CreateTestProjectContext();
        var withContext = DecompositionPromptBuilder.BuildDecompositionPrompt(5, context);
        var without = DecompositionPromptBuilder.BuildDecompositionPrompt(5);

        withContext.Length.Should().BeGreaterThan(without.Length);
    }

    // ── BuildReviewPrompt ────────────────────────────────────────────────

    [Fact]
    public void BuildReviewPrompt_ContainsEvaluationCriteria()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt();

        prompt.Should().Contain("# Decomposition Plan Review");
        prompt.Should().Contain("### 1. Overlap Check");
        prompt.Should().Contain("### 2. Sizing Validation");
        prompt.Should().Contain("### 3. Acyclic Dependencies");
        prompt.Should().Contain("### 4. Coverage Check");
        prompt.Should().Contain("### 5. Duplicate Title Check");
    }

    [Fact]
    public void BuildReviewPrompt_ContainsSeverityMarkers()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt();

        prompt.Should().Contain("[CRITICAL]");
        prompt.Should().Contain("[WARNING]");
        prompt.Should().Contain("[SUGGESTION]");
    }

    [Fact]
    public void BuildReviewPrompt_ContainsNoFalsePositiveRule()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt();
        prompt.Should().Contain("Do NOT invent findings");
    }

    [Fact]
    public void BuildReviewPrompt_WithNullProjectContext_ReturnsSameAsWithout()
    {
        var without = DecompositionPromptBuilder.BuildReviewPrompt();
        var withNull = DecompositionPromptBuilder.BuildReviewPrompt(null);

        withNull.Should().Be(without);
    }

    [Fact]
    public void BuildReviewPrompt_WithProjectContext_AppendsCrossRepoReviewAdditions()
    {
        var context = CreateTestProjectContext();
        var withContext = DecompositionPromptBuilder.BuildReviewPrompt(context);
        var without = DecompositionPromptBuilder.BuildReviewPrompt();

        withContext.Length.Should().BeGreaterThan(without.Length);
    }

    // ── BuildRefinementPrompt ────────────────────────────────────────────

    [Fact]
    public void BuildRefinementPrompt_ContainsRequiredSections()
    {
        var prompt = DecompositionPromptBuilder.BuildRefinementPrompt();

        prompt.Should().Contain("# Decomposition Plan Refinement");
        prompt.Should().Contain("## Input");
        prompt.Should().Contain("## Instructions");
        prompt.Should().Contain("## Output");
    }

    [Fact]
    public void BuildRefinementPrompt_AddressesCriticalAndWarning()
    {
        var prompt = DecompositionPromptBuilder.BuildRefinementPrompt();

        prompt.Should().Contain("`[CRITICAL]` findings");
        prompt.Should().Contain("`[WARNING]` findings");
    }

    [Fact]
    public void BuildRefinementPrompt_PreservesOriginalConstraints()
    {
        var prompt = DecompositionPromptBuilder.BuildRefinementPrompt();

        // Ensures the refinement prompt reminds the agent of sizing constraints
        prompt.Should().Contain("5 files");
        prompt.Should().Contain("one verification criterion");
        prompt.Should().Contain("one agent run");
    }

    // ── Idempotence (Property-like) ──────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(15)]
    public void BuildAnalysisPrompt_IsDeterministic(int maxSubIssues)
    {
        var first = DecompositionPromptBuilder.BuildAnalysisPrompt(maxSubIssues);
        var second = DecompositionPromptBuilder.BuildAnalysisPrompt(maxSubIssues);

        first.Should().Be(second);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(15)]
    public void BuildDecompositionPrompt_IsDeterministic(int maxSubIssues)
    {
        var first = DecompositionPromptBuilder.BuildDecompositionPrompt(maxSubIssues);
        var second = DecompositionPromptBuilder.BuildDecompositionPrompt(maxSubIssues);

        first.Should().Be(second);
    }

    [Fact]
    public void BuildReviewPrompt_IsDeterministic()
    {
        var first = DecompositionPromptBuilder.BuildReviewPrompt();
        var second = DecompositionPromptBuilder.BuildReviewPrompt();

        first.Should().Be(second);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static DecompositionProjectContext CreateTestProjectContext() => new()
    {
        ProjectName = "TestProject",
        Repositories =
        [
            new RepositoryTarget
            {
                TemplateName = "frontend",
                Description = "React frontend app",
                DecompositionEnabled = true,
                Labels = ["typescript", "react"]
            },
            new RepositoryTarget
            {
                TemplateName = "backend",
                Description = ".NET API service",
                DecompositionEnabled = true,
                Labels = ["csharp", "dotnet"]
            }
        ]
    };
}
