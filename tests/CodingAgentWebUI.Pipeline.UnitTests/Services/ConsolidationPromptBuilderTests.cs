// Feature: 021-consolidation-loops, Task 5.4: Unit tests for ConsolidationPromptBuilder content
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Prompts;

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

    [Fact]
    public void BuildOpenIssueContext_BothListsPopulated_ContainsTitlesAndHeader()
    {
        var refactoring = new List<IssueSummary>
        {
            new() { Identifier = "10", Title = "Extract shared retry logic", Labels = ["agent:generated"] }
        };
        var other = new List<IssueSummary>
        {
            new() { Identifier = "20", Title = "Add pagination support", Labels = [] }
        };

        var result = ConsolidationPromptBuilder.BuildOpenIssueContext(refactoring, other);

        result.Should().Contain("Do Not Duplicate");
        result.Should().Contain("#10");
        result.Should().Contain("Extract shared retry logic");
        result.Should().Contain("#20");
        result.Should().Contain("Add pagination support");
    }

    [Fact]
    public void BuildOpenIssueContext_BothListsEmpty_ReturnsEmptyString()
    {
        var result = ConsolidationPromptBuilder.BuildOpenIssueContext([], []);

        result.Should().BeEmpty();
    }

    [Fact]
    public void BuildOpenIssueContext_OnlyRefactoringIssues_ContainsOnlyRefactoringSection()
    {
        var refactoring = new List<IssueSummary>
        {
            new() { Identifier = "5", Title = "Rename methods", Labels = ["agent:generated"] }
        };

        var result = ConsolidationPromptBuilder.BuildOpenIssueContext(refactoring, []);

        result.Should().Contain("Open Refactoring Issues");
        result.Should().NotContain("Other Recent Open Issues");
    }

    [Fact]
    public void BuildOpenIssueContext_OnlyOtherIssues_ContainsOnlyOtherSection()
    {
        var other = new List<IssueSummary>
        {
            new() { Identifier = "7", Title = "Fix bug", Labels = [] }
        };

        var result = ConsolidationPromptBuilder.BuildOpenIssueContext([], other);

        result.Should().NotContain("Open Refactoring Issues");
        result.Should().Contain("Other Recent Open Issues");
    }

    [Fact]
    public void BuildProposalOutcomeContext_EmptyList_ReturnsEmptyString()
    {
        var result = ConsolidationPromptBuilder.BuildProposalOutcomeContext(Array.Empty<IssueSummary>());

        result.Should().BeEmpty();
    }

    [Fact]
    public void BuildProposalOutcomeContext_OnlyAmbiguousIssues_ReturnsEmptyString()
    {
        var issues = new[]
        {
            new IssueSummary { Identifier = "1", Title = "Ambiguous", Labels = new[] { "agent:generated" } }
        };

        var result = ConsolidationPromptBuilder.BuildProposalOutcomeContext(issues);

        result.Should().BeEmpty();
    }

    [Fact]
    public void BuildProposalOutcomeContext_ImplementedIssues_FormatsCorrectly()
    {
        var issues = new[]
        {
            new IssueSummary { Identifier = "315", Title = "Extract shared retry logic", Labels = new[] { "agent:generated", "agent:done" } }
        };

        var result = ConsolidationPromptBuilder.BuildProposalOutcomeContext(issues);

        result.Should().Contain("Implemented (team valued these)");
        result.Should().Contain("#315 \"Extract shared retry logic\"");
        result.Should().NotContain("Rejected");
    }

    [Fact]
    public void BuildProposalOutcomeContext_RejectedIssues_FormatsCorrectly()
    {
        var issues = new[]
        {
            new IssueSummary { Identifier = "320", Title = "Restructure interfaces", Labels = new[] { "agent:generated", "agent:wont-do" } },
            new IssueSummary { Identifier = "322", Title = "Replace serializer", Labels = new[] { "agent:generated", "agent:cancelled" } }
        };

        var result = ConsolidationPromptBuilder.BuildProposalOutcomeContext(issues);

        result.Should().Contain("Rejected (avoid similar proposals)");
        result.Should().Contain("#320 \"Restructure interfaces\"");
        result.Should().Contain("#322 \"Replace serializer\"");
        result.Should().Contain("Do NOT propose refactorings similar to rejected items above.");
    }

    [Fact]
    public void BuildProposalOutcomeContext_MixedIssues_ExcludesAmbiguous()
    {
        var issues = new[]
        {
            new IssueSummary { Identifier = "1", Title = "Done", Labels = new[] { "agent:done" } },
            new IssueSummary { Identifier = "2", Title = "Ambiguous", Labels = new[] { "agent:generated" } },
            new IssueSummary { Identifier = "3", Title = "Rejected", Labels = new[] { "agent:wont-do" } }
        };

        var result = ConsolidationPromptBuilder.BuildProposalOutcomeContext(issues);

        result.Should().Contain("#1 \"Done\"");
        result.Should().Contain("#3 \"Rejected\"");
        result.Should().NotContain("#2");
    }
}
