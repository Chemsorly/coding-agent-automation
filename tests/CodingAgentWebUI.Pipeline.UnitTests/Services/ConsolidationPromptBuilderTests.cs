// Feature: 021-consolidation-loops, Task 5.4: Unit tests for ConsolidationPromptBuilder content
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests verifying ConsolidationPromptBuilder produces prompts with correct content.
/// **Validates: Requirements 4.2, 5.2, 5.4, 7.3, 7.4**
/// </summary>
public class ConsolidationPromptBuilderTests
{
    /// <summary>
    /// Brain consolidation prompt includes all 4 phases: Orient, Gather Signal, Consolidate, Prune.
    /// **Validates: Requirements 4.2**
    /// </summary>
    [Fact]
    public void BuildBrainConsolidationPrompt_IncludesFourPhases()
    {
        var result = ConsolidationPromptBuilder.BuildBrainConsolidationPrompt(lastConsolidationUtc: null);

        result.Should().Contain("Phase 1: Orient");
        result.Should().Contain("Phase 2: Gather Signal");
        result.Should().Contain("Phase 3: Consolidate");
        result.Should().Contain("Phase 4: Prune");
    }

    /// <summary>
    /// Brain consolidation prompt includes the formatted timestamp when provided.
    /// **Validates: Requirements 4.2**
    /// </summary>
    [Fact]
    public void BuildBrainConsolidationPrompt_IncludesTimestampWhenProvided()
    {
        var timestamp = new DateTime(2026, 6, 15, 14, 30, 0, DateTimeKind.Utc);

        var result = ConsolidationPromptBuilder.BuildBrainConsolidationPrompt(timestamp);

        result.Should().Contain("2026-06-15 14:30:00 UTC");
    }

    /// <summary>
    /// Brain consolidation prompt indicates no prior consolidation when lastConsolidationUtc is null.
    /// **Validates: Requirements 4.2**
    /// </summary>
    [Fact]
    public void BuildBrainConsolidationPrompt_IndicatesNoPriorConsolidationWhenNull()
    {
        var result = ConsolidationPromptBuilder.BuildBrainConsolidationPrompt(lastConsolidationUtc: null);

        result.Should().Contain("No prior consolidation has occurred");
    }

    /// <summary>
    /// Refactoring detection prompt instructs JSON output format.
    /// **Validates: Requirements 5.2**
    /// </summary>
    [Fact]
    public void BuildRefactoringDetectionPrompt_InstructsJsonOutput()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringDetectionPrompt();

        result.Should().Contain("JSON");
        result.Should().Contain(".agent/refactoring-proposals.json");
    }

    /// <summary>
    /// Refactoring detection prompt specifies maximum proposals based on parameter.
    /// **Validates: Requirements 5.4**
    /// </summary>
    [Fact]
    public void BuildRefactoringDetectionPrompt_SpecifiesMaxThreeProposals()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringDetectionPrompt();

        result.Should().Contain("3 proposals");
    }

    /// <summary>
    /// Refactoring detection prompt respects custom max proposals parameter.
    /// </summary>
    [Fact]
    public void BuildRefactoringDetectionPrompt_RespectsCustomMaxProposals()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringDetectionPrompt(5);

        result.Should().Contain("5 proposals");
        result.Should().NotContain("3 proposals");
    }

    /// <summary>
    /// Harness suggestion prompt includes feedback count and success rate.
    /// **Validates: Requirements 7.3**
    /// </summary>
    [Fact]
    public void BuildHarnessSuggestionPrompt_IncludesFeedbackCountAndSuccessRate()
    {
        var result = ConsolidationPromptBuilder.BuildHarnessSuggestionPrompt(
            feedbackCount: 42, successRate: 78.5m);

        result.Should().Contain("42");
        // The success rate is formatted with F1 using the current culture's decimal separator
        var expectedRate = 78.5m.ToString("F1") + "%";
        result.Should().Contain(expectedRate);
    }

    /// <summary>
    /// Harness suggestion prompt instructs concrete, actionable suggestions.
    /// **Validates: Requirements 7.4**
    /// </summary>
    [Fact]
    public void BuildHarnessSuggestionPrompt_InstructsConcreteSuggestions()
    {
        var result = ConsolidationPromptBuilder.BuildHarnessSuggestionPrompt(
            feedbackCount: 10, successRate: 65.0m);

        result.Should().Contain("Concrete and actionable");
        result.Should().Contain("Grounded in evidence");
    }

    /// <summary>
    /// FormatBrainConsolidationSummary includes all four metrics.
    /// **Validates: Requirements 4.2**
    /// </summary>
    [Fact]
    public void FormatBrainConsolidationSummary_IncludesAllMetrics()
    {
        var result = ConsolidationPromptBuilder.FormatBrainConsolidationSummary(
            filesModified: 5,
            entriesMerged: 3,
            contradictionsResolved: 2,
            entriesPruned: 7);

        result.Should().Contain("5");
        result.Should().Contain("3");
        result.Should().Contain("2");
        result.Should().Contain("7");
        result.Should().Contain("Files modified");
        result.Should().Contain("Entries merged");
        result.Should().Contain("Contradictions resolved");
        result.Should().Contain("Entries pruned");
    }
}
