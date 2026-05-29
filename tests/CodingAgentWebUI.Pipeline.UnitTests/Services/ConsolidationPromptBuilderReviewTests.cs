// Feature: 024-adversarial-review-loops, Task 10: Unit tests for review and refinement prompt builder methods
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Prompts;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests verifying ConsolidationPromptBuilder review and refinement prompts
/// contain required file paths, severity markers, and behavioral instructions.
/// **Validates: Requirement 5**
/// </summary>
public class ConsolidationPromptBuilderReviewTests
{
    [Fact]
    public void BuildRefactoringReviewPrompt_ContainsProposalsFilePath()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringReviewPrompt();

        result.Should().Contain(".agent/refactoring-proposals.json");
    }

    [Fact]
    public void BuildRefactoringReviewPrompt_ContainsReviewOutputPath()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringReviewPrompt();

        result.Should().Contain(".agent/refactoring-review.md");
    }

    [Fact]
    public void BuildRefactoringReviewPrompt_ContainsSeverityMarkers()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringReviewPrompt();

        result.Should().Contain("[CRITICAL]");
        result.Should().Contain("[WARNING]");
        result.Should().Contain("[SUGGESTION]");
    }

    [Fact]
    public void BuildRefactoringReviewPrompt_ContainsReadOnlyInstruction()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringReviewPrompt();

        result.Should().Contain("Do NOT modify source files");
    }

    [Fact]
    public void BuildRefactoringReviewPrompt_ContainsDoNotInventInstruction()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringReviewPrompt();

        result.Should().Contain("Do NOT invent findings");
    }

    [Fact]
    public void BuildRefactoringReviewPrompt_ContainsDoNotEchoMarkersInstruction()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringReviewPrompt();

        result.Should().Contain("do NOT echo severity marker syntax");
    }

    [Fact]
    public void BuildBrainConsolidationReviewPrompt_ContainsDiffFilePath()
    {
        var result = ConsolidationPromptBuilder.BuildBrainConsolidationReviewPrompt();

        result.Should().Contain(".agent/brain-consolidation-diff.md");
    }

    [Fact]
    public void BuildBrainConsolidationReviewPrompt_ContainsReviewOutputPath()
    {
        var result = ConsolidationPromptBuilder.BuildBrainConsolidationReviewPrompt();

        result.Should().Contain(".agent/brain-consolidation-review.md");
    }

    [Fact]
    public void BuildHarnessSuggestionsReviewPrompt_ContainsSuggestionsFilePath()
    {
        var result = ConsolidationPromptBuilder.BuildHarnessSuggestionsReviewPrompt();

        result.Should().Contain(".agent/harness-suggestions-output.json");
    }

    [Fact]
    public void BuildHarnessSuggestionsReviewPrompt_ContainsFeedbackDataReference()
    {
        var result = ConsolidationPromptBuilder.BuildHarnessSuggestionsReviewPrompt();

        result.Should().Contain("feedback-data.json");
    }

    [Fact]
    public void BuildRefactoringRefinementPrompt_ContainsReviewFilePath()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringRefinementPrompt();

        result.Should().Contain(".agent/refactoring-review.md");
    }

    [Fact]
    public void BuildBrainConsolidationDiffPrompt_ContainsDiffOutputPath()
    {
        var result = ConsolidationPromptBuilder.BuildBrainConsolidationDiffPrompt();

        result.Should().Contain(".agent/brain-consolidation-diff.md");
    }
}
