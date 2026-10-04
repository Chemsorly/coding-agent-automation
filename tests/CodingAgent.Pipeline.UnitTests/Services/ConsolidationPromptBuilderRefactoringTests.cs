using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services.Prompts;

namespace CodingAgent.Pipeline.UnitTests.Services;

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

    [Fact]
    public void BuildRefactoringAggregationPrompt_IncludesEvidenceQualityGateSection()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain("Evidence Quality Gate");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_RejectsCodeReadingOnlyForHardCategories()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain("DROP the proposal");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_ExemptsSimplificationAndDocumentation()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain("may use \"code-reading:\" alone");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_CapsEvidenceScoreForExemptedCategories()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain("capped evidence score of 1");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_HotspotIsNotEvidence()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain("`hotspot:` is a priority signal, not evidence");
        result.Should().Contain("A `tool:` source counts only when it names a compiler, linter or analyzer");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_EvidenceGateCoversEveryCategory()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();
        var gate = Section(result, "### Step 3: Evidence Quality Gate", "### Step 4");

        foreach (var category in RefactoringCategories.All)
            gate.Should().Contain($"`{category}`");
    }

    [Fact]
    public void RefactoringPrompts_SchemaCategoriesMatchTheSingleCategoryList()
    {
        ConsolidationPromptBuilder.BuildRefactoringStructuralDebtPrompt()
            .Should().Contain($"\"category\": \"{string.Join("|", RefactoringCategories.Structural)}\"");
        ConsolidationPromptBuilder.BuildRefactoringCorrectnessPrompt()
            .Should().Contain($"\"category\": \"{string.Join("|", RefactoringCategories.Correctness)}\"");
        ConsolidationPromptBuilder.BuildRefactoringDesignConsistencyPrompt()
            .Should().Contain($"\"category\": \"{string.Join("|", RefactoringCategories.Design)}\"");
        ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt()
            .Should().Contain($"\"category\": \"{string.Join("|", RefactoringCategories.All)}\"");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_KnownDebtDoesNotDropTodoFindings()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain("This does not apply to `todo` findings");
    }

    [Fact]
    public void BuildRefactoringContextExtractionPrompt_KeepsInlineTodosOutOfKnownDebt()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringContextExtractionPrompt();

        result.Should().Contain("Do NOT copy inline TODO/FIXME/HACK comments into knownDebt");
        result.Should().NotContain("anything acknowledged in TODOs");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_RanksBugsFirstAndSpreadsWork()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain("**Bugs first.**");
        result.Should().Contain("at most one proposal per primary file");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_HandlesMissingFindingsFile()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain("If a findings file is missing or is not valid JSON, that agent failed");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_RequiresEvidenceAndScopeQuery()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain("\"evidence\":");
        result.Should().Contain("\"scopeQuery\":");
        result.Should().Contain("one criterion MUST state that no match of the pattern remains");
        result.Should().Contain("one criterion MUST require a test that reproduces the failure scenario");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_ForbidsPipelineNarrationAndAlternatives()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain("Do NOT mention the analysis agents (A, B, C), phases, scores, rankings, or this scan");
        result.Should().Contain("Propose ONE approach");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_WritesAnalysisLogToConstantPath()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain(AgentWorkspacePaths.RefactoringAnalysisFilePath);
        result.Should().Contain("The `notChecked` areas the agents reported");
    }

    [Theory]
    [MemberData(nameof(SubAgentPrompts))]
    public void SubAgentPrompts_ExcludePipelineScratchSpaceAndReportNotChecked(string prompt)
    {
        prompt.Should().Contain("**Out of scope:** `.agent/`, `.brain/`");
        prompt.Should().Contain("\"notChecked\":");
        prompt.Should().Contain("\"findings\": [");
        prompt.Should().Contain("Nothing else is `tool:`");
    }

    public static TheoryData<string> SubAgentPrompts() => new()
    {
        ConsolidationPromptBuilder.BuildRefactoringStructuralDebtPrompt(),
        ConsolidationPromptBuilder.BuildRefactoringCorrectnessPrompt(),
        ConsolidationPromptBuilder.BuildRefactoringDesignConsistencyPrompt()
    };

    [Fact]
    public void BuildRefactoringStructuralDebtPrompt_DoesNotFlagInjectedSingleImplementationInterfaces()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringStructuralDebtPrompt();

        result.Should().Contain("is NOT a finding when it is registered for dependency injection");
    }

    private static string Section(string text, string startHeading, string endHeading)
    {
        var start = text.IndexOf(startHeading, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"the prompt should contain '{startHeading}'");
        var end = text.IndexOf(endHeading, start + startHeading.Length, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"the prompt should contain '{endHeading}' after '{startHeading}'");
        return text[start..end];
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
    public void BuildRefactoringReviewPrompt_ReadsAnalysisLogAndIssueContextFiles()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringReviewPrompt();

        result.Should().Contain(AgentWorkspacePaths.RefactoringAnalysisFilePath);
        result.Should().Contain(AgentWorkspacePaths.RefactoringIssueContextFilePath);
    }

    [Fact]
    public void BuildRefactoringReviewPrompt_FlagsIncompleteScopeAndBugsWithoutReproduction()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringReviewPrompt();

        result.Should().Contain("**Incomplete scope**");
        result.Should().Contain("**Bug without a reproduction**");
        result.Should().Contain("**Evidence not shown**");
    }

    [Fact]
    public void BuildRefactoringReviewPrompt_DoesNotDemandAMinimumFindingCount()
    {
        // The refinement step cannot add findings, so a minimum-count CRITICAL could never be fixed
        var result = ConsolidationPromptBuilder.BuildRefactoringReviewPrompt();

        result.Should().NotContain("Shallow exploration");
        result.Should().NotContain("fewer than 15");
    }

    [Fact]
    public void BuildRefactoringRefinementPrompt_LetsRefinementCompleteTheScope()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringRefinementPrompt();

        result.Should().Contain("add them to `affectedFiles` and fix `scopeQuery`");
    }

    [Fact]
    public void BuildRefactoringReviewPrompt_IncludesAcceptanceCriteriaQualityBullets()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringReviewPrompt();

        result.Should().Contain("Unverifiable acceptance criteria");
        result.Should().Contain("Implementation-prescriptive acceptance criteria");
    }
}
