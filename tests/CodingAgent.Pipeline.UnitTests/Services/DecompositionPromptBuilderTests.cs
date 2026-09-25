using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services.Prompts;

namespace CodingAgent.Pipeline.UnitTests.Services;

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
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(7, 12);
        prompt.Should().Contain("at most **7**");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsRequiredSections()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12);

        prompt.Should().Contain("# Epic Decomposition Analysis");
        prompt.Should().Contain("## Exploration Strategy");
        prompt.Should().Contain("## Deduplication Check");
        prompt.Should().Contain("## Sub-Issue Sizing Constraints");
        prompt.Should().Contain("## Output");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsFileLimit()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12);
        prompt.Should().Contain("**12 files**");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsDependencyOrdering()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12);
        prompt.Should().Contain("dependencies always point backward");
    }

    [Fact]
    public void BuildAnalysisPrompt_DifferentMaxSubIssues_ProducesDifferentContent()
    {
        var prompt3 = DecompositionPromptBuilder.BuildAnalysisPrompt(3, 12);
        var prompt10 = DecompositionPromptBuilder.BuildAnalysisPrompt(10, 12);

        prompt3.Should().Contain("at most **3**");
        prompt10.Should().Contain("at most **10**");
        prompt3.Should().NotBe(prompt10);
    }

    [Fact]
    public void BuildAnalysisPrompt_WithNullProjectContext_ReturnsSameAsWithout()
    {
        var without = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12);
        var withNull = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12, null);

        withNull.Should().Be(without);
    }

    [Fact]
    public void BuildAnalysisPrompt_WithProjectContext_AppendsCrossRepoInstructions()
    {
        var context = CreateTestProjectContext();
        var withContext = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12, context);
        var without = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12);

        withContext.Length.Should().BeGreaterThan(without.Length);
        withContext.Should().StartWith(without);
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsGateRejectionSection()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12);
        prompt.Should().Contain("## Gate Rejection Concerns");
        prompt.Should().Contain("agent:gate-rejection");
    }

    // ── BuildDecompositionPrompt ─────────────────────────────────────────

    [Fact]
    public void BuildDecompositionPrompt_ContainsMaxSubIssuesConstraint()
    {
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(8, 12);
        prompt.Should().Contain("at most **8**");
    }

    [Fact]
    public void BuildDecompositionPrompt_ContainsJsonSchema()
    {
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(5, 12);

        prompt.Should().Contain("\"title\":");
        prompt.Should().Contain("\"body\":");
        prompt.Should().Contain("\"dependencies\":");
        prompt.Should().Contain("\"labels\":");
    }

    [Fact]
    public void BuildDecompositionPrompt_ContainsRequiredSections()
    {
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(5, 12);

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
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(5, 12);

        prompt.Should().Contain("## Summary");
        prompt.Should().Contain("## Affected Components");
        prompt.Should().Contain("## Requirements");
        prompt.Should().Contain("## Acceptance Criteria");
    }

    [Fact]
    public void BuildDecompositionPrompt_WithNullProjectContext_ReturnsSameAsWithout()
    {
        var without = DecompositionPromptBuilder.BuildDecompositionPrompt(5, 12);
        var withNull = DecompositionPromptBuilder.BuildDecompositionPrompt(5, 12, null);

        withNull.Should().Be(without);
    }

    [Fact]
    public void BuildDecompositionPrompt_WithProjectContext_AppendsRoutingInstructions()
    {
        var context = CreateTestProjectContext();
        var withContext = DecompositionPromptBuilder.BuildDecompositionPrompt(5, 12, context);
        var without = DecompositionPromptBuilder.BuildDecompositionPrompt(5, 12);

        withContext.Length.Should().BeGreaterThan(without.Length);
    }

    // ── BuildReviewPrompt ────────────────────────────────────────────────

    [Fact]
    public void BuildReviewPrompt_ContainsEvaluationCriteria()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt(12);

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
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt(12);

        prompt.Should().Contain("[CRITICAL]");
        prompt.Should().Contain("[WARNING]");
        prompt.Should().Contain("[SUGGESTION]");
    }

    [Fact]
    public void BuildReviewPrompt_ContainsNoFalsePositiveRule()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt(12);
        prompt.Should().Contain("Do NOT invent findings");
    }

    [Fact]
    public void BuildReviewPrompt_WithNullProjectContext_ReturnsSameAsWithout()
    {
        var without = DecompositionPromptBuilder.BuildReviewPrompt(12);
        var withNull = DecompositionPromptBuilder.BuildReviewPrompt(12, (DecompositionProjectContext?)null);

        withNull.Should().Be(without);
    }

    [Fact]
    public void BuildReviewPrompt_WithProjectContext_AppendsCrossRepoReviewAdditions()
    {
        var context = CreateTestProjectContext();
        var withContext = DecompositionPromptBuilder.BuildReviewPrompt(12, context);
        var without = DecompositionPromptBuilder.BuildReviewPrompt(12);

        withContext.Length.Should().BeGreaterThan(without.Length);
    }

    // ── BuildRefinementPrompt ────────────────────────────────────────────

    [Fact]
    public void BuildRefinementPrompt_ContainsRequiredSections()
    {
        var prompt = DecompositionPromptBuilder.BuildRefinementPrompt(12);

        prompt.Should().Contain("# Decomposition Plan Refinement");
        prompt.Should().Contain("## Input");
        prompt.Should().Contain("## Instructions");
        prompt.Should().Contain("## Output");
    }

    [Fact]
    public void BuildRefinementPrompt_AddressesCriticalAndWarning()
    {
        var prompt = DecompositionPromptBuilder.BuildRefinementPrompt(12);

        prompt.Should().Contain("`[CRITICAL]` findings");
        prompt.Should().Contain("`[WARNING]` findings");
    }

    [Fact]
    public void BuildRefinementPrompt_PreservesOriginalConstraints()
    {
        var prompt = DecompositionPromptBuilder.BuildRefinementPrompt(12);

        // Ensures the refinement prompt reminds the agent of sizing constraints
        prompt.Should().Contain("≤12 files");
        prompt.Should().Contain("one verification criterion");
        prompt.Should().Contain("one agent run");
    }

    // ── BuildReviewPrompt (maxSubIssues) ─────────────────────────────────

    [Fact]
    public void BuildReviewPrompt_WithMaxSubIssues_ContainsCapCriticalInstruction()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt(maxFiles: 12, maxSubIssues: 10);

        // Should include a section about the sub-issue cap
        prompt.Should().Contain("10");
        // TODO: This assertion is tautological for the cap concern — [CRITICAL] already appears in
        // the base review prompt (sections 1–5), so it passes regardless of whether the cap section
        // is present. The companion BuildReviewPrompt_WithMaxSubIssues_NamesToSection test (which
        // checks for "Sub-Issue Cap") is the real signal. Consider replacing this with an assertion
        // that verifies the cap section specifically flags the count as [CRITICAL], e.g.:
        // prompt.Should().Contain("Sub-Issue Cap").And.Contain("[CRITICAL]") within that section.
        prompt.Should().Contain("[CRITICAL]");
    }

    [Fact]
    public void BuildReviewPrompt_WithMaxSubIssues_NamesToSection()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt(maxFiles: 12, maxSubIssues: 7);

        // The sub-issue cap section should be present
        prompt.Should().Contain("Sub-Issue Cap");
        prompt.Should().Contain("7");
    }

    [Fact]
    public void BuildReviewPrompt_WithMaxSubIssues_InstructsRebalancingNotAddingSubIssues()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt(maxFiles: 12, maxSubIssues: 10);

        // Should instruct to rebalance/merge rather than add sub-issues
        prompt.Should().Contain("merging");
    }

    [Fact]
    public void BuildReviewPrompt_WithNullMaxSubIssues_ReturnsSameAsOneArgOverload()
    {
        var oneArg = DecompositionPromptBuilder.BuildReviewPrompt(12);
        var twoArg = DecompositionPromptBuilder.BuildReviewPrompt(12, maxSubIssues: null, projectContext: null);

        twoArg.Should().Be(oneArg);
    }

    [Fact]
    public void BuildReviewPrompt_WithMaxSubIssues_IsLongerThanWithout()
    {
        var without = DecompositionPromptBuilder.BuildReviewPrompt(12, maxSubIssues: null, projectContext: null);
        var with = DecompositionPromptBuilder.BuildReviewPrompt(12, maxSubIssues: 10, projectContext: null);

        with.Length.Should().BeGreaterThan(without.Length);
    }

    [Fact]
    public void BuildReviewPrompt_WithMaxSubIssuesAndProjectContext_ContainsBothExtensions()
    {
        var context = CreateTestProjectContext();
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt(12, maxSubIssues: 8, context);

        // Should contain sub-issue cap instruction
        prompt.Should().Contain("Sub-Issue Cap");
        prompt.Should().Contain("8");

        // Should also contain cross-repo routing validation
        prompt.Should().Contain("Cross-Repo Routing Validation");
    }

    [Fact]
    public void BuildReviewPrompt_WithMaxSubIssuesNullProjectContext_SameAsMaxSubIssuesOnly()
    {
        var prompt1 = DecompositionPromptBuilder.BuildReviewPrompt(12, maxSubIssues: 5, projectContext: null);
        var prompt2 = DecompositionPromptBuilder.BuildReviewPrompt(12, maxSubIssues: 5);

        prompt1.Should().Be(prompt2);
    }

    // ── BuildRefinementPrompt (maxSubIssues) ──────────────────────────────

    [Fact]
    public void BuildRefinementPrompt_WithMaxSubIssues_ConstraintListContainsSubIssueCap()
    {
        var prompt = DecompositionPromptBuilder.BuildRefinementPrompt(maxFiles: 12, maxSubIssues: 10);

        // The constraint list must mention the sub-issue cap
        prompt.Should().Contain("At most 10 sub-issues");
    }

    [Fact]
    public void BuildRefinementPrompt_WithMaxSubIssues_ProhibitsSplittingBeyondCap()
    {
        var prompt = DecompositionPromptBuilder.BuildRefinementPrompt(maxFiles: 12, maxSubIssues: 8);

        // Should warn about not resolving sizing by adding sub-issues
        prompt.Should().Contain("merging");
        prompt.Should().Contain("8");
    }

    [Fact]
    public void BuildRefinementPrompt_WithNullMaxSubIssues_ReturnsSameAsOneArgOverload()
    {
        var oneArg = DecompositionPromptBuilder.BuildRefinementPrompt(12);
        var twoArg = DecompositionPromptBuilder.BuildRefinementPrompt(12, maxSubIssues: null);

        twoArg.Should().Be(oneArg);
    }

    [Fact]
    public void BuildRefinementPrompt_WithMaxSubIssues_IsLongerThanWithout()
    {
        var without = DecompositionPromptBuilder.BuildRefinementPrompt(12, maxSubIssues: null);
        var with = DecompositionPromptBuilder.BuildRefinementPrompt(12, maxSubIssues: 10);

        with.Length.Should().BeGreaterThan(without.Length);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(20)]
    public void BuildRefinementPrompt_WithMaxSubIssues_NamedCapAppears(int cap)
    {
        var prompt = DecompositionPromptBuilder.BuildRefinementPrompt(12, maxSubIssues: cap);

        prompt.Should().Contain(cap.ToString());
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsMaxFilesConstraint()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(10, 15);
        prompt.Should().Contain("**15 files**");
    }

    [Fact]
    public void BuildDecompositionPrompt_ContainsMaxFilesConstraint()
    {
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(10, 8);
        prompt.Should().Contain("**8 files**");
    }

    [Fact]
    public void BuildReviewPrompt_ContainsMaxFilesConstraint()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt(20);
        prompt.Should().Contain("≤20 files");
    }

    [Fact]
    public void BuildRefinementPrompt_ContainsMaxFilesConstraint()
    {
        var prompt = DecompositionPromptBuilder.BuildRefinementPrompt(7);
        prompt.Should().Contain("≤7 files");
    }

    // ── Idempotence (Property-like) ──────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(15)]
    public void BuildAnalysisPrompt_IsDeterministic(int maxSubIssues)
    {
        var first = DecompositionPromptBuilder.BuildAnalysisPrompt(maxSubIssues, 12);
        var second = DecompositionPromptBuilder.BuildAnalysisPrompt(maxSubIssues, 12);

        first.Should().Be(second);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(15)]
    public void BuildDecompositionPrompt_IsDeterministic(int maxSubIssues)
    {
        var first = DecompositionPromptBuilder.BuildDecompositionPrompt(maxSubIssues, 12);
        var second = DecompositionPromptBuilder.BuildDecompositionPrompt(maxSubIssues, 12);

        first.Should().Be(second);
    }

    [Fact]
    public void BuildReviewPrompt_IsDeterministic()
    {
        var first = DecompositionPromptBuilder.BuildReviewPrompt(12);
        var second = DecompositionPromptBuilder.BuildReviewPrompt(12);

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
