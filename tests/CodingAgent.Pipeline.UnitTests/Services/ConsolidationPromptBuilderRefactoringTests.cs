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
    public void BuildRefactoringContextExtractionPrompt_DiscoversMcpToolsAndRunsAnalyzersOnce()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringContextExtractionPrompt();

        result.Should().Contain("Check your available MCP tools for additional data sources");
        result.Should().Contain($"`{AgentWorkspacePaths.RefactoringToolOutputDirectory}/<name>.txt`");
        result.Should().Contain("\"availableTools\"");
    }

    [Fact]
    public void BuildRefactoringStructuralDebtPrompt_IncludesPreambleWithToolAugmentation()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringStructuralDebtPrompt();

        result.Should().Contain("Use the available tools");
        result.Should().Contain(AgentWorkspacePaths.RefactoringToolOutputDirectory);
        result.Should().Contain("Query the listed MCP tools");
        // The three detection agents share one workspace: parallel builds would collide
        result.Should().Contain("Do NOT run builds");
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

        result.Should().Contain("install and run");
        result.Should().Contain("read-only analyzer that does not build");
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

    // ─── Phase 2a: Scoring / Phase 2b: Aggregation ──────────────────────

    [Fact]
    public void BuildRefactoringAggregationPrompt_ReferencesAllSubAgentOutputPaths()
    {
        // Phase 2b reads the Phase 2a compact candidates files, not raw findings files
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain(AgentWorkspacePaths.RefactoringPhase2aStructuralFilePath);
        result.Should().Contain(AgentWorkspacePaths.RefactoringPhase2aCorrectnessFilePath);
        result.Should().Contain(AgentWorkspacePaths.RefactoringPhase2aDesignFilePath);
        result.Should().Contain(AgentWorkspacePaths.RefactoringPhase2aTestGapsFilePath);
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
        // Convention filtering is applied by Phase 2a; verify it is present there
        var result = ConsolidationPromptBuilder.BuildRefactoringPhase2aPrompt(RefactoringScanFocus.Structure);

        result.Should().Contain("intentionalPatterns");
        result.Should().Contain("knownDebt");
        result.Should().Contain("DROP IT");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_IncludesRankingMatrix()
    {
        // Scoring on three axes is done in Phase 2a; verify it is present there
        var result = ConsolidationPromptBuilder.BuildRefactoringPhase2aPrompt(RefactoringScanFocus.Structure);

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
        // Evidence quality gate is applied by Phase 2a; verify it is present there
        var result = ConsolidationPromptBuilder.BuildRefactoringPhase2aPrompt(RefactoringScanFocus.Structure);

        result.Should().Contain("Evidence Quality Gate");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_RejectsCodeReadingOnlyForHardCategories()
    {
        // Evidence quality gate (DROP rules) is applied by Phase 2a; verify it is present there
        var result = ConsolidationPromptBuilder.BuildRefactoringPhase2aPrompt(RefactoringScanFocus.Structure);

        result.Should().Contain("DROP");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_ExemptsSimplificationAndDocumentation()
    {
        // Code-reading exemption for lower-risk categories is applied by Phase 2a; verify it is present there
        var result = ConsolidationPromptBuilder.BuildRefactoringPhase2aPrompt(RefactoringScanFocus.Structure);

        result.Should().Contain("may use \"code-reading:\" alone");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_CapsEvidenceScoreForExemptedCategories()
    {
        // Capped evidence score is applied by Phase 2a; verify it is present there
        var result = ConsolidationPromptBuilder.BuildRefactoringPhase2aPrompt(RefactoringScanFocus.Structure);

        result.Should().Contain("capped evidence score of 1");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_HotspotIsNotEvidence()
    {
        // Hotspot-is-not-evidence rule is stated in Phase 2a; verify it is present there
        var result = ConsolidationPromptBuilder.BuildRefactoringPhase2aPrompt(RefactoringScanFocus.Structure);

        result.Should().Contain("`hotspot:` is a priority signal, not evidence");
        result.Should().Contain("A `tool:` source counts only when it names a compiler, linter, analyzer or MCP tool");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_EvidenceGateCoversEveryCategory()
    {
        // Evidence quality gate is applied by Phase 2a; verify all categories are covered there
        // (using Structure focus: has all the gate categories from the full set via RefactoringCategories.All coverage)
        var structuralResult = ConsolidationPromptBuilder.BuildRefactoringPhase2aPrompt(RefactoringScanFocus.Structure);
        var gate = Section(structuralResult, "## Step 2: Evidence Quality Gate", "## Step 3:");

        // Gate must cover every category that has hard evidence rules
        gate.Should().Contain($"`{RefactoringCategories.Bug}`");
        gate.Should().Contain($"`{RefactoringCategories.DeadCode}`");
        gate.Should().Contain($"`{RefactoringCategories.Duplication}`");
        gate.Should().Contain($"`{RefactoringCategories.NamingInconsistency}`");
        gate.Should().Contain($"`{RefactoringCategories.PrimitiveObsession}`");
        gate.Should().Contain($"`{RefactoringCategories.TestGap}`");
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
        // knownDebt/todo rule is applied by Phase 2a; verify it is present there
        var result = ConsolidationPromptBuilder.BuildRefactoringPhase2aPrompt(RefactoringScanFocus.Correctness);

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
        result.Should().Contain("at most one proposal per");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_HandlesMissingFindingsFile()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain("If a candidates file is missing or is not valid JSON, that Phase 2a agent failed");
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
        ConsolidationPromptBuilder.BuildRefactoringDesignConsistencyPrompt(),
        ConsolidationPromptBuilder.BuildRefactoringTestGapPrompt()
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
        result.Should().Contain(AgentWorkspacePaths.RefactoringTestGapFindingsFilePath);
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

    // TODO: These tests verify the correct content is present in the prompt output but cannot
    // distinguish between the constant being interpolated versus a duplicate bare string literal
    // with the same value. If AgentWorkspacePaths.RefactoringAnalysisFilePath were removed and
    // ".agent/refactoring-analysis.md" re-introduced as a literal, these tests would still pass.
    // To close this gap, consider adding a source-scan test that asserts no .cs file outside
    // AgentWorkspacePaths.cs contains a raw ".agent/refactoring-analysis.md" string literal.
    // TODO: This test is a duplicate of BuildRefactoringReviewPrompt_ReadsAnalysisLogAndIssueContextFiles (line ~512),
    // which already asserts result.Should().Contain(AgentWorkspacePaths.RefactoringAnalysisFilePath) on the same method.
    // This test adds no new assertion and would pass or fail under exactly the same conditions. Consider removing it.
    [Fact]
    public void BuildRefactoringReviewPrompt_ReferencesAnalysisFilePath()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringReviewPrompt();

        result.Should().Contain(AgentWorkspacePaths.RefactoringAnalysisFilePath);
    }

    // TODO: This test is a duplicate of BuildRefactoringAggregationPrompt_WritesAnalysisLogToConstantPath (line ~399),
    // which already asserts result.Should().Contain(AgentWorkspacePaths.RefactoringAnalysisFilePath) on the same method.
    // This test adds no new assertion and would pass or fail under exactly the same conditions. Consider removing it.
    [Fact]
    public void BuildRefactoringAggregationPrompt_ReferencesAnalysisFilePath()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        result.Should().Contain(AgentWorkspacePaths.RefactoringAnalysisFilePath);
    }

    [Fact]
    public void BuildRefactoringStructuralDebtPrompt_RequiresBehavioralDifferenceList()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringStructuralDebtPrompt();

        // TODO: These substring assertions were written alongside the implementation (tautological risk).
        // Consider asserting on a longer representative phrase that cannot survive partial removal, or verify
        // that the full three-line block appears directly after the "find EVERY copy" line as the issue requires.
        result.Should().Contain("list every behavioral difference in `evidence`");
        result.Should().Contain("Write \"no differences\" when the copies behave the same");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_DuplicationDescriptionAndAcRulesPresent()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        // description rule
        // TODO: The AC-rule assertion does not verify the trailing clause ("that each listed difference is kept
        // or removed as `description` says"), so a truncated rule would still pass. Consider asserting the full sentence.
        // The description rule's second line ("(for example as a parameter) or removes it, and why removing it is safe")
        // is also not asserted; add it to avoid silent truncation.
        result.Should().Contain("For `duplication`, list each behavioral difference between the copies");
        result.Should().Contain("Write \"The copies behave the same\" when they do");
        // AC rule
        result.Should().Contain("For `duplication` with behavioral differences, one criterion MUST state");
        // numbers-from-command rule
        result.Should().Contain("Every number you state (occurrences, lines, files, counts) must come from a command");
    }

    [Fact]
    public void BuildRefactoringReviewPrompt_ChecksDuplicationWithoutDifferences()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringReviewPrompt();

        // TODO: The acceptance criterion requires the bullet to be "flagged [WARNING]", but the assertion on
        // "Flag [WARNING] and list the differences it missed" covers only the trailing call-to-action, not the
        // [WARNING] label on the bullet title itself. Add an assertion that the heading text
        // "**Duplication without its differences**" appears together with "[WARNING]" in the rendered output,
        // e.g. by asserting on the EvaluationBullets rendering that includes "[WARNING]" in the bullet prefix.
        result.Should().Contain("**Duplication without its differences**");
        result.Should().Contain("Flag [WARNING] and list the differences it missed");
    }

    // ─── Phase 1, Agent D: Test Gap Detection ─────────────────────────────

    [Fact]
    public void BuildRefactoringTestGapPrompt_WritesToTestGapFindingsFile()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringTestGapPrompt();

        result.Should().Contain(AgentWorkspacePaths.RefactoringTestGapFindingsFilePath);
    }

    [Fact]
    public void BuildRefactoringTestGapPrompt_UsesOnlyTestGapCategory()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringTestGapPrompt();

        result.Should().Contain("\"category\": \"test-gap\"");
    }

    [Fact]
    public void BuildRefactoringTestGapPrompt_ForbidsBuilds()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringTestGapPrompt();

        result.Should().Contain("Do NOT run builds");
    }

    [Fact]
    public void BuildRefactoringContextExtractionPrompt_AsksForCoverageReport()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringContextExtractionPrompt();

        result.Should().Contain("**Test coverage.**");
        result.Should().Contain("coverage.<ext>");
        result.Should().Contain("timeout 20m");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_HasTestGapEvidenceGate()
    {
        // Test-gap evidence gate is applied by Phase 2a; verify it is present for the Tests focus
        var result = ConsolidationPromptBuilder.BuildRefactoringPhase2aPrompt(RefactoringScanFocus.Tests);
        var gate = Section(result, "## Step 2: Evidence Quality Gate", "## Step 3:");

        gate.Should().Contain("`test-gap`");
        gate.Should().Contain("tool:coverage:");
    }

    [Fact]
    public void BuildRefactoringTestGapPrompt_ReferencesHotspotAndConventionsFiles()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringTestGapPrompt();

        result.Should().Contain(AgentWorkspacePaths.HotspotAnalysisFilePath);
        result.Should().Contain(AgentWorkspacePaths.RefactoringConventionsFilePath);
    }

    [Fact]
    public void BuildRefactoringTestGapPrompt_ListsCoverageAsPrimaryEvidence()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringTestGapPrompt();

        result.Should().Contain("primary evidence");
        result.Should().Contain("tool:coverage:");
    }

    // ─── Focus: Phase 0 coverage step ────────────────────────────────────

    [Fact]
    public void BuildRefactoringContextExtractionPrompt_WithoutCoverage_OmitsCoverageStep()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringContextExtractionPrompt(includeCoverage: false);

        result.Should().NotContain("**Test coverage.**");
    }

    [Fact]
    public void BuildRefactoringContextExtractionPrompt_WithoutCoverage_RenumbersNextItem()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringContextExtractionPrompt(includeCoverage: false);

        // Item 3 must now be "List all of them under availableTools"
        result.Should().Contain("3. List all of them under `availableTools`");
    }

    [Fact]
    public void BuildRefactoringContextExtractionPrompt_WithCoverage_Unchanged()
    {
        var withCoverage = ConsolidationPromptBuilder.BuildRefactoringContextExtractionPrompt(includeCoverage: true);
        var defaultCall = ConsolidationPromptBuilder.BuildRefactoringContextExtractionPrompt();

        withCoverage.Should().Be(defaultCall, "default and explicit includeCoverage=true must produce identical output");
        withCoverage.Should().Contain("**Test coverage.**");
        withCoverage.Should().Contain("4. List all of them under `availableTools`");
    }

    // ─── Focus: Phase 2 aggregation ──────────────────────────────────────

    [Fact]
    public void BuildRefactoringAggregationPrompt_FocusTests_ListsOnlyTestGapInput()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt(focus: RefactoringScanFocus.Tests);

        // Only Phase 2a test-gaps candidates file should appear
        result.Should().Contain(AgentWorkspacePaths.RefactoringPhase2aTestGapsFilePath);

        // Other Phase 2a candidates files must NOT appear
        result.Should().NotContain(AgentWorkspacePaths.RefactoringPhase2aStructuralFilePath);
        result.Should().NotContain(AgentWorkspacePaths.RefactoringPhase2aCorrectnessFilePath);
        result.Should().NotContain(AgentWorkspacePaths.RefactoringPhase2aDesignFilePath);
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_FocusTests_CategorySchemaContainsOnlyTestGap()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt(focus: RefactoringScanFocus.Tests);

        // TODO [WARNING]: These assertions use substring matching and may pass even if the schema emits
        // extra categories alongside "test-gap" (e.g. "test-gap|structural-debt"). The NotContain check
        // only excludes the full All-schema pipe-joined string, not partial multi-value combinations.
        // A regression where ForFocus(Tests) returns extra categories would not be caught. Consider
        // replacing with an exact schema-line assertion, or asserting that no other category names from
        // RefactoringCategories.All appear in the result.
        result.Should().Contain($"\"category\": \"{RefactoringCategories.TestGap}\"");
        result.Should().NotContain($"\"category\": \"{string.Join("|", RefactoringCategories.All)}\"");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_FocusAll_IsUnchangedFromDefault()
    {
        var focusAll = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt(focus: RefactoringScanFocus.All);
        var defaultCall = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        focusAll.Should().Be(defaultCall, "focus=All must produce byte-for-byte identical output to the default call");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_FocusTests_AddsFocusConstraintSentence()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt(focus: RefactoringScanFocus.Tests);

        result.Should().Contain("This scan is focused on Tests:");
        result.Should().Contain("`test-gap`");
    }

    [Fact]
    public void BuildRefactoringAggregationPrompt_FocusStructure_ListsOnlyStructuralInput()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt(focus: RefactoringScanFocus.Structure);

        // Only the Phase 2a structural candidates file should appear
        result.Should().Contain(AgentWorkspacePaths.RefactoringPhase2aStructuralFilePath);
        result.Should().NotContain(AgentWorkspacePaths.RefactoringPhase2aCorrectnessFilePath);
        result.Should().NotContain(AgentWorkspacePaths.RefactoringPhase2aDesignFilePath);
        result.Should().NotContain(AgentWorkspacePaths.RefactoringPhase2aTestGapsFilePath);
    }
}
