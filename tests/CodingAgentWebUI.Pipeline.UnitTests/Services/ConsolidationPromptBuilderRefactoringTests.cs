using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Services.Prompts;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for the phased refactoring detection prompts in ConsolidationPromptBuilder.
/// Validates structural content, output path references, and research-backed constraints.
/// </summary>
public class ConsolidationPromptBuilderRefactoringTests
{
    // ─── Phase 0: Context Extraction ─────────────────────────────────────

    [Fact]
    public void BuildRefactoringContextExtractionPrompt_ReferencesConventionsOutputPath()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringContextExtractionPrompt();

        result.Should().Contain(AgentWorkspacePaths.RefactoringConventionsFilePath);
    }

    [Fact]
    public void BuildRefactoringContextExtractionPrompt_InstructsJsonOutput()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringContextExtractionPrompt();

        result.Should().Contain("\"intentionalPatterns\"");
        result.Should().Contain("\"namingConventions\"");
        result.Should().Contain("\"layerRules\"");
    }

    [Fact]
    public void BuildRefactoringContextExtractionPrompt_InstructsObservationNotJudgment()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringContextExtractionPrompt();

        result.Should().Contain("Observe, don't judge");
    }

    // ─── Phase 1, Agent A: Structural Debt ───────────────────────────────

    [Fact]
    public void BuildRefactoringStructuralDebtPrompt_ReferencesOutputPath()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringStructuralDebtPrompt();

        result.Should().Contain(AgentWorkspacePaths.RefactoringStructuralFindingsFilePath);
    }

    [Fact]
    public void BuildRefactoringStructuralDebtPrompt_IncludesPreambleWithToolAugmentation()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringStructuralDebtPrompt();

        result.Should().Contain("Tool augmentation encouraged");
        result.Should().Contain("install tools");
    }

    [Fact]
    public void BuildRefactoringStructuralDebtPrompt_ReferencesConventionsFile()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringStructuralDebtPrompt();

        result.Should().Contain("refactoring-conventions.json");
        result.Should().Contain("intentionalPatterns");
    }

    [Fact]
    public void BuildRefactoringStructuralDebtPrompt_RequiresCrossReference()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringStructuralDebtPrompt();

        result.Should().Contain("crossReference");
        result.Should().Contain("MUST have");
    }

    [Fact]
    public void BuildRefactoringStructuralDebtPrompt_CoversFourCategories()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringStructuralDebtPrompt();

        result.Should().Contain("Duplicated logic");
        result.Should().Contain("Structural drift");
        result.Should().Contain("Overly complex");
        result.Should().Contain("Over-engineering");
    }

    // ─── Phase 1, Agent B: Correctness ───────────────────────────────────

    [Fact]
    public void BuildRefactoringCorrectnessPrompt_ReferencesOutputPath()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringCorrectnessPrompt();

        result.Should().Contain(AgentWorkspacePaths.RefactoringCorrectnessFindingsFilePath);
    }

    [Fact]
    public void BuildRefactoringCorrectnessPrompt_UsesEnumerateThenVerifyPattern()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringCorrectnessPrompt();

        result.Should().Contain("Enumerate Then Verify");
        result.Should().Contain("grep");
    }

    [Fact]
    public void BuildRefactoringCorrectnessPrompt_RequiresProofForDeadCode()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringCorrectnessPrompt();

        result.Should().Contain("proof of zero usage");
    }

    [Fact]
    public void BuildRefactoringCorrectnessPrompt_RequiresConcreteFailureForBugs()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringCorrectnessPrompt();

        result.Should().Contain("concrete failure scenario");
    }

    [Fact]
    public void BuildRefactoringCorrectnessPrompt_EncouragesToolInstallation()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringCorrectnessPrompt();

        result.Should().Contain("install and run them");
    }

    // ─── Phase 1, Agent C: Design Consistency ────────────────────────────

    [Fact]
    public void BuildRefactoringDesignConsistencyPrompt_ReferencesOutputPath()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringDesignConsistencyPrompt();

        result.Should().Contain(AgentWorkspacePaths.RefactoringDesignFindingsFilePath);
    }

    [Fact]
    public void BuildRefactoringDesignConsistencyPrompt_DependsOnConventions()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringDesignConsistencyPrompt();

        result.Should().Contain("refactoring-conventions.json");
        result.Should().Contain("Read it first");
    }

    [Fact]
    public void BuildRefactoringDesignConsistencyPrompt_RequiresConventionRuleReference()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringDesignConsistencyPrompt();

        result.Should().Contain("convention rule reference");
    }

    [Fact]
    public void BuildRefactoringDesignConsistencyPrompt_RequiresThreePlusOccurrences()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringDesignConsistencyPrompt();

        result.Should().Contain("3+ occurrences");
    }

    // ─── Phase 2: Aggregation ────────────────────────────────────────────

    [Fact]
    public void BuildRefactoringAggregationPrompt_ReferencesAllSubAgentOutputPaths()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain(AgentWorkspacePaths.RefactoringStructuralFindingsFilePath);
        result.Should().Contain(AgentWorkspacePaths.RefactoringCorrectnessFindingsFilePath);
        result.Should().Contain(AgentWorkspacePaths.RefactoringDesignFindingsFilePath);
        result.Should().Contain(AgentWorkspacePaths.RefactoringConventionsFilePath);
        result.Should().Contain(AgentWorkspacePaths.HotspotAnalysisFilePath);
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_OutputsToProposalsFilePath()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain(AgentWorkspacePaths.RefactoringProposalsFilePath);
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_RespectsMaxProposalsParameter()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt(maxProposals: 5);

        result.Should().Contain("5");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_IncludesDeduplicationStep()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain("Deduplicate");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_IncludesConventionFilterStep()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain("intentionalPatterns");
        result.Should().Contain("knownDebt");
        result.Should().Contain("DROP IT");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_IncludesRankingMatrix()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain("Hotspot frequency");
        result.Should().Contain("Evidence strength");
        result.Should().Contain("Scope feasibility");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_IncludesIssueContextWhenProvided()
    {
        var issueContext = "## Existing Open Issues\n- #42 \"Fix something\"";

        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt(
            issueContext: issueContext);

        result.Should().Contain("#42");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_IncludesOutcomeContextWhenProvided()
    {
        var outcomeContext = "## Past Proposal Outcomes\n### Rejected\n- #99 \"Bad idea\"";

        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt(
            outcomeContext: outcomeContext);

        result.Should().Contain("#99");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_RequiresEvidenceSourcesField()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain("\"evidenceSources\"");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_EnforcesScopeConstraints()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain("30 affected files");
        result.Should().Contain("single agent in one run");
    }

    // ─── Review Prompt (Strengthened) ────────────────────────────────────

    [Fact]
    public void BuildRefactoringReviewPrompt_ChecksEvidenceCorroboration()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringReviewPrompt();

        result.Should().Contain("Evidence corroboration failure");
        result.Should().Contain("single `evidenceSources` entry");
    }

    [Fact]
    public void BuildRefactoringReviewPrompt_ChecksActualBlastRadius()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringReviewPrompt();

        result.Should().Contain("Actual blast radius");
        result.Should().Contain("consumers");
    }

    [Fact]
    public void BuildRefactoringReviewPrompt_ChecksFailureModeCommitment()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringReviewPrompt();

        result.Should().Contain("Failure mode not committed");
        result.Should().Contain("Hedged language");
    }

    [Fact]
    public void BuildRefactoringReviewPrompt_ReferencesSubAgentFindingsFiles()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringReviewPrompt();

        result.Should().Contain(AgentWorkspacePaths.RefactoringStructuralFindingsFilePath);
        result.Should().Contain(AgentWorkspacePaths.RefactoringCorrectnessFindingsFilePath);
        result.Should().Contain(AgentWorkspacePaths.RefactoringDesignFindingsFilePath);
    }

    [Fact]
    public void BuildRefactoringReviewPrompt_ChecksConventionContradiction()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringReviewPrompt();

        result.Should().Contain("Convention contradiction");
        result.Should().Contain("conventions.json");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_IncludesAcceptanceCriteriaInSchema()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain("\"acceptanceCriteria\"");
    }

    // TODO: Assertion fragments ("verifiable", "WHAT must be true", "2-4 items") are generic and could match
    // unrelated prompt text. Consider asserting on more distinctive phrases unique to the AC guidance section
    // (e.g., "Prefer negative assertions" or "Do NOT use #N notation") to avoid false-passing tests if the
    // quality guidance rules are accidentally deleted.
    [Fact]
    public void BuildRefactoringAggregationPrompt_IncludesAcceptanceCriteriaFieldDefinition()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain("acceptanceCriteria");
        result.Should().Contain("verifiable");
        result.Should().Contain("WHAT must be true");
        result.Should().Contain("2-4 items");
    }

    [Fact]
    public void BuildRefactoringReviewPrompt_IncludesAcceptanceCriteriaQualityBullets()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringReviewPrompt();

        result.Should().Contain("Unverifiable acceptance criteria");
        result.Should().Contain("Implementation-prescriptive acceptance criteria");
    }
}
