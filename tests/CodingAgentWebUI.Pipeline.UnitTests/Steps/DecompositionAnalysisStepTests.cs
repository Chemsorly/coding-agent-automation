using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Prompts;

namespace CodingAgentWebUI.Pipeline.UnitTests.Steps;

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
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(7);

        prompt.Should().Contain("7");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsFileLimit()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5);

        prompt.Should().Contain("5 files");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsOneVerificationCriterion()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5);

        prompt.Should().Contain("verification criterion");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsOneAgentRunConstraint()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5);

        prompt.Should().Contain("single agent run");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsOpenIssuesDeduplicationInstruction()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5);

        prompt.Should().Contain(".agent/open-issues/");
        prompt.Should().Contain("overlap");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsReRunFeedbackInstruction()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5);

        prompt.Should().Contain("re-run");
        prompt.Should().Contain("feedback");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsDependencyOrderingInstruction()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5);

        prompt.Should().Contain("dependencies");
        prompt.Should().Contain("backward");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsOutputPathInstruction()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5);

        prompt.Should().Contain(".agent/decomposition-plan.md");
    }

    [Fact]
    public void BuildAnalysisPrompt_ContainsGateRejectionConcernsInstruction()
    {
        var prompt = DecompositionPromptBuilder.BuildAnalysisPrompt(5);

        prompt.Should().Contain("agent:gate-rejection");
        prompt.Should().Contain("hard constraint");
        prompt.Should().Contain("which sub-issue handles it");
    }

    [Fact]
    public void BuildDecompositionPrompt_ContainsJsonSchemaInstruction()
    {
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(5);

        prompt.Should().Contain("title");
        prompt.Should().Contain("body");
        prompt.Should().Contain("dependencies");
        prompt.Should().Contain("labels");
    }

    [Fact]
    public void BuildDecompositionPrompt_ContainsSubIssuesOutputPath()
    {
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(5);

        prompt.Should().Contain(".agent/sub-issues/");
    }

    [Fact]
    public void BuildDecompositionPrompt_ContainsIssueTemplateSections()
    {
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(5);

        prompt.Should().Contain("Summary");
        prompt.Should().Contain("Acceptance Criteria");
    }

    [Fact]
    public void BuildDecompositionPrompt_ContainsMaxSubIssuesCap()
    {
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(10);

        prompt.Should().Contain("10");
    }

    [Fact]
    public void BuildReviewPrompt_ContainsOverlapCheck()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt();

        prompt.Should().Contain("overlap");
        prompt.Should().Contain("open issues");
    }

    [Fact]
    public void BuildReviewPrompt_ContainsSizingValidation()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt();

        prompt.Should().Contain("5 files");
        prompt.Should().Contain("verification criterion");
    }

    [Fact]
    public void BuildReviewPrompt_ContainsAcyclicDependencyCheck()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt();

        prompt.Should().Contain("acyclic");
    }

    [Fact]
    public void BuildReviewPrompt_ContainsCriticalFlagging()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt();

        prompt.Should().Contain("[CRITICAL]");
    }

    [Fact]
    public void BuildReviewPrompt_ContainsDuplicateTitleCheck()
    {
        var prompt = DecompositionPromptBuilder.BuildReviewPrompt();

        prompt.Should().Contain("duplicate");
    }

    [Fact]
    public void BuildRefinementPrompt_ContainsReviewFindingsPath()
    {
        var prompt = DecompositionPromptBuilder.BuildRefinementPrompt();

        prompt.Should().Contain(".agent/decomposition-review.md");
    }

    [Fact]
    public void BuildRefinementPrompt_ContainsCriticalAndWarningInstruction()
    {
        var prompt = DecompositionPromptBuilder.BuildRefinementPrompt();

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
