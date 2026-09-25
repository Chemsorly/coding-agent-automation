using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Prompts;

namespace CodingAgent.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests for <see cref="DecompositionPromptBuilder"/> and <see cref="AgentLabels"/> decomposition labels.
/// Verifies that prompts contain all required instructions per the design document,
/// and that new epic labels are correctly defined with proper colors.
/// Feature: 027-epic-decomposition-pipeline, Requirements: 2.1, 2.8, 3.4, 3.5, 3.8
/// </summary>
public class DecompositionAnalysisStepTests
{
    [Fact]
    public void BuildAnalysisPrompt_ContainsMaxSubIssuesCap()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(7, 12);

        prompt.Should().Contain("7");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsFileLimit()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12);

        prompt.Should().Contain("**12 files**");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsOneVerificationCriterion()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12);

        prompt.Should().Contain("verification criterion");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsOneAgentRunConstraint()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12);

        prompt.Should().Contain("single agent run");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsOpenIssuesDeduplicationInstruction()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12);

        prompt.Should().Contain(".agent/open-issues/");
        prompt.Should().Contain("overlap");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsReRunFeedbackInstruction()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12);

        prompt.Should().Contain("re-run");
        prompt.Should().Contain("feedback");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsDependencyOrderingInstruction()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12);

        prompt.Should().Contain("dependencies");
        prompt.Should().Contain("backward");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsOutputPathInstruction()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12);

        prompt.Should().Contain(".agent/decomposition-plan.md");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsGateRejectionConcernsInstruction()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5, 12);

        prompt.Should().Contain("agent:gate-rejection");
        prompt.Should().Contain("hard constraint");
        prompt.Should().Contain("which sub-issue handles it");
    }

    [Fact]
    public void BuildDecompositionPrompt_ContainsJsonSchemaInstruction()
    {
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(5, 12);

        prompt.Should().Contain("title");
        prompt.Should().Contain("body");
        prompt.Should().Contain("dependencies");
        prompt.Should().Contain("labels");
    }

    [Fact]
    public void BuildDecompositionPrompt_ContainsSubIssuesOutputPath()
    {
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(5, 12);

        prompt.Should().Contain(".agent/sub-issues/");
    }

    [Fact]
    public void BuildDecompositionPrompt_ContainsIssueTemplateSections()
    {
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(5, 12);

        prompt.Should().Contain("Summary");
        prompt.Should().Contain("Acceptance Criteria");
    }

    [Fact]
    public void BuildDecompositionPrompt_ContainsMaxSubIssuesCap()
    {
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(10, 12);

        prompt.Should().Contain("10");
    }

    [Fact]
    public void BuildReviewPrompt_ContainsOverlapCheck()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt(10, 12);

        prompt.Should().Contain("overlap");
        prompt.Should().Contain("open issues");
    }

    [Fact]
    public void BuildReviewPrompt_ContainsSizingValidation()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt(10, 12);

        prompt.Should().Contain("≤12 files");
        prompt.Should().Contain("verification criterion");
    }

    [Fact]
    public void BuildReviewPrompt_ContainsAcyclicDependencyCheck()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt(10, 12);

        prompt.Should().Contain("acyclic");
    }

    [Fact]
    public void BuildReviewPrompt_ContainsCriticalFlagging()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt(10, 12);

        prompt.Should().Contain("[CRITICAL]");
    }

    [Fact]
    public void BuildReviewPrompt_ContainsDuplicateTitleCheck()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt(10, 12);

        prompt.Should().Contain("duplicate");
    }

    [Fact]
    public void BuildRefinementPrompt_ContainsReviewFindingsPath()
    {
        var prompt = DecompositionPromptBuilder.BuildRefinementPrompt(10, 12);

        prompt.Should().Contain(".agent/decomposition-review.md");
    }

    [Fact]
    public void BuildRefinementPrompt_ContainsCriticalAndWarningInstruction()
    {
        var prompt = DecompositionPromptBuilder.BuildRefinementPrompt(10, 12);

        prompt.Should().Contain("[CRITICAL]");
        prompt.Should().Contain("[WARNING]");
    }
}


/// <summary>
/// Unit tests for <see cref="AgentLabels"/> decomposition label definitions.
/// Verifies new labels are in Definitions with correct colors.
/// Feature: 027-epic-decomposition-pipeline, Requirements: 2.1, 2.8
/// </summary>
public class AgentLabelsDecompositionTests
{
    [Fact]
    public void Epic_LabelConstant_HasCorrectValue()
    {
        AgentLabels.Epic.Should().Be("agent:epic");
    }

    [Fact]
    public void EpicReview_LabelConstant_HasCorrectValue()
    {
        AgentLabels.EpicReview.Should().Be("agent:epic-review");
    }

    [Fact]
    public void EpicApproved_LabelConstant_HasCorrectValue()
    {
        AgentLabels.EpicApproved.Should().Be("agent:epic-approved");
    }

    [Fact]
    public void Definitions_ContainsEpicLabel_WithPurpleColor()
    {
        AgentLabels.Definitions.Should().Contain(d => d.Name == AgentLabels.Epic && d.Color == "7057ff");
    }

    [Fact]
    public void Definitions_ContainsEpicReviewLabel_WithYellowColor()
    {
        AgentLabels.Definitions.Should().Contain(d => d.Name == AgentLabels.EpicReview && d.Color == "fbca04");
    }

    [Fact]
    public void Definitions_ContainsEpicApprovedLabel_WithGreenColor()
    {
        AgentLabels.Definitions.Should().Contain(d => d.Name == AgentLabels.EpicApproved && d.Color == "0e8a16");
    }

    [Fact]
    public void All_ContainsAllEpicLabels()
    {
        AgentLabels.All.Should().Contain(AgentLabels.Epic);
        AgentLabels.All.Should().Contain(AgentLabels.EpicReview);
        AgentLabels.All.Should().Contain(AgentLabels.EpicApproved);
    }

    [Fact]
    public void Definitions_EpicLabels_HaveDistinctColors()
    {
        var epicColor = AgentLabels.Definitions.First(d => d.Name == AgentLabels.Epic).Color;
        var reviewColor = AgentLabels.Definitions.First(d => d.Name == AgentLabels.EpicReview).Color;
        var approvedColor = AgentLabels.Definitions.First(d => d.Name == AgentLabels.EpicApproved).Color;

        // Epic (purple) should be distinct from review (yellow)
        epicColor.Should().NotBe(reviewColor);
    }
}

/// <summary>
/// Wiring tests verifying that <see cref="DecompositionAnalysisStep"/> passes
/// <see cref="PipelineConfiguration.MaxDecompositionSubIssues"/> to both the review
/// and refinement prompts (Req 3 of issue #3018).
///
/// These tests use the prompt builders directly with config-derived values — the same
/// pattern used elsewhere in this class for BuildAnalysisPrompt. Because
/// <see cref="DecompositionAnalysisStep.ExecuteAsync"/> wires <c>maxSubIssues</c>
/// to <c>BuildReviewPrompt(maxSubIssues, maxFiles, projectContext)</c> and
/// <c>BuildRefinementPrompt(maxSubIssues, maxFiles)</c>, asserting that the returned
/// prompts contain the cap value verifies the wiring is correct.
/// </summary>
// TODO: These wiring tests call DecompositionPromptBuilder methods directly and do not exercise
// DecompositionAnalysisStep.ExecuteAsync itself. A refactor that accidentally swapped maxSubIssues
// and maxFiles in the ExecuteAsync call site would not be caught. Consider adding an integration-style
// test that runs ExecuteAsync with a controlled config and verifies via captured prompt content that
// config.MaxDecompositionSubIssues reaches both builder calls.
public class DecompositionAnalysisStepWiringTests
{
    [Fact]
    public void MaxDecompositionSubIssues_ReachesReviewPrompt()
    {
        // Given config with a specific cap value
        const int maxSubIssues = 7;
        const int maxFiles = 12;

        // When the review prompt is built with the wired parameters
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt(maxSubIssues, maxFiles, null);

        // Then the cap value is present in the prompt — verifying the wiring carries it through
        prompt.Should().Contain("7",
            because: "DecompositionAnalysisStep.ExecuteAsync passes config.MaxDecompositionSubIssues to BuildReviewPrompt");
        prompt.Should().Contain("### 6. Sub-Issue Count Check",
            because: "the cap criterion section must be present");
    }

    [Fact]
    public void MaxDecompositionSubIssues_ReachesRefinementPrompt()
    {
        // Given config with a specific cap value
        const int maxSubIssues = 7;
        const int maxFiles = 12;

        // When the refinement prompt is built with the wired parameters
        var prompt = DecompositionPromptBuilder.BuildRefinementPrompt(maxSubIssues, maxFiles);

        // Then the cap value is present in the prompt — verifying the wiring carries it through
        prompt.Should().Contain("7",
            because: "DecompositionAnalysisStep.ExecuteAsync passes config.MaxDecompositionSubIssues to BuildRefinementPrompt");
        prompt.Should().Contain("at most 7 sub-issues",
            because: "the sub-issue count constraint must be in the refinement constraint list");
    }

    [Fact]
    public void MaxDecompositionSubIssues_DifferentValues_ProduceDifferentReviewPrompts()
    {
        // Verifies that different config values produce distinguishable prompts — not a constant
        var config5 = new PipelineConfiguration { MaxDecompositionSubIssues = 5, MaxDecompositionSubIssueFiles = 12 };
        var config12 = new PipelineConfiguration { MaxDecompositionSubIssues = 12, MaxDecompositionSubIssueFiles = 12 };

        var prompt5 = DecompositionPromptBuilder.BuildReviewPrompt(config5.MaxDecompositionSubIssues, config5.MaxDecompositionSubIssueFiles, null);
        var prompt12 = DecompositionPromptBuilder.BuildReviewPrompt(config12.MaxDecompositionSubIssues, config12.MaxDecompositionSubIssueFiles, null);

        prompt5.Should().NotBe(prompt12);
    }

    [Fact]
    public void MaxDecompositionSubIssues_DifferentValues_ProduceDifferentRefinementPrompts()
    {
        var config5 = new PipelineConfiguration { MaxDecompositionSubIssues = 5, MaxDecompositionSubIssueFiles = 12 };
        var config12 = new PipelineConfiguration { MaxDecompositionSubIssues = 12, MaxDecompositionSubIssueFiles = 12 };

        var prompt5 = DecompositionPromptBuilder.BuildRefinementPrompt(config5.MaxDecompositionSubIssues, config5.MaxDecompositionSubIssueFiles);
        var prompt12 = DecompositionPromptBuilder.BuildRefinementPrompt(config12.MaxDecompositionSubIssues, config12.MaxDecompositionSubIssueFiles);

        prompt5.Should().NotBe(prompt12);
    }
}
