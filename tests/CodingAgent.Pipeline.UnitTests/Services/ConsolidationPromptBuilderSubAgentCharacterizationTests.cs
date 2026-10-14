using AwesomeAssertions;
using CodingAgent.Pipeline.Services.Prompts;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Characterization tests that lock in the exact structural output of each Phase 1 sub-agent prompt.
/// These tests prove that the scaffold extraction in <see cref="ConsolidationPromptBuilder"/>
/// (issue #3537) does not alter the generated prompt text for any of the three sub-agents.
/// The tests verify:
/// <list type="bullet">
///   <item>The sub-agent preamble appears before each agent's title.</item>
///   <item>The exact intro text sequence (title → blank → "You are one of three..." → focus description → blank).</item>
///   <item>The section order: ## Your Categories → ## Exploration Strategy → ## Output Format → ## Quality Bar.</item>
///   <item>The exact first line of each agent's ## Quality Bar, which identifies which agent's quality-bar content is present.</item>
/// </list>
/// NOTE: These tests use partial substring assertions (Should().Contain). They do not provide byte-for-byte
/// identity verification. Full-string snapshot tests for all three methods are in
/// PromptBuilderSnapshotTests.cs (tests 12–14, BuildRefactoringStructuralDebtPrompt_Full_MatchesSnapshot,
/// BuildRefactoringCorrectnessPrompt_Full_MatchesSnapshot,
/// BuildRefactoringDesignConsistencyPrompt_Full_MatchesSnapshot), which use Should().Be() for complete equality.
/// </summary>
// TODO [WARNING] (review finding: AcceptanceCriteria agent) — These tests use Should().Contain() partial-match
// assertions rather than full byte-for-byte snapshot comparisons. The complete byte-for-byte verification
// for AC3 ("Generated prompt output ... unchanged") is provided by the full-string snapshot tests 12–14 in
// PromptBuilderSnapshotTests.cs. The partial tests here remain as lightweight structural guards only.
// TODO [WARNING] (review finding: TestQualityReviewer agent) — The ## Your Categories and
// ## Exploration Strategy section bodies are not asserted by any test in this file. Swapping those
// lambdas between agents would not be detected here. The full-string snapshot tests 12–14 in
// PromptBuilderSnapshotTests.cs cover those bodies via Should().Be() assertions.
public class ConsolidationPromptBuilderSubAgentCharacterizationTests
{
    // ── Agent A: Structural Debt ──────────────────────────────────────────────

    [Fact]
    public void BuildRefactoringStructuralDebtPrompt_Characterization_PreamblePrecedesTitle()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringStructuralDebtPrompt();

        var preamblePos = result.IndexOf("## CRITICAL RULES — Read First", StringComparison.Ordinal);
        var titlePos = result.IndexOf("# Agent A: Structural Debt Detection", StringComparison.Ordinal);

        preamblePos.Should().BeGreaterThanOrEqualTo(0, "the sub-agent preamble must be present");
        titlePos.Should().BeGreaterThan(preamblePos, "the preamble must appear before the agent title");
    }

    [Fact]
    public void BuildRefactoringStructuralDebtPrompt_Characterization_IntroTextExact()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringStructuralDebtPrompt();

        result.Should().Contain(
            "# Agent A: Structural Debt Detection" + Environment.NewLine +
            Environment.NewLine +
            "You are one of three parallel analysis agents. Your focus is **structural debt** —" + Environment.NewLine +
            "patterns where incremental changes have created global incoherence." + Environment.NewLine +
            Environment.NewLine);
        // TODO [WARNING] (review finding: TestQualityReviewer agent) — This assertion ends at the trailing
        // blank after the focus description, so it does not verify that the Agent C–specific extra-intro
        // lines ("**This agent depends heavily...**") are absent from Agent A. If appendExtraIntro fired
        // unexpectedly, the extra text would appear AFTER the asserted substring and this test would still
        // pass. The full-string snapshot test BuildRefactoringStructuralDebtPrompt_Full_MatchesSnapshot in
        // PromptBuilderSnapshotTests.cs provides the byte-for-byte check that covers this gap.
    }

    [Fact]
    public void BuildRefactoringStructuralDebtPrompt_Characterization_SectionOrder()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringStructuralDebtPrompt();

        var categoriesPos = result.IndexOf("## Your Categories", StringComparison.Ordinal);
        var explorationPos = result.IndexOf("## Exploration Strategy", StringComparison.Ordinal);
        var outputPos = result.IndexOf("## Output Format", StringComparison.Ordinal);
        var qualityPos = result.IndexOf("## Quality Bar", StringComparison.Ordinal);

        categoriesPos.Should().BeGreaterThan(0, "## Your Categories must be present");
        explorationPos.Should().BeGreaterThan(categoriesPos, "## Exploration Strategy must follow ## Your Categories");
        outputPos.Should().BeGreaterThan(explorationPos, "## Output Format must follow ## Exploration Strategy");
        qualityPos.Should().BeGreaterThan(outputPos, "## Quality Bar must follow ## Output Format");
    }

    [Fact]
    public void BuildRefactoringStructuralDebtPrompt_Characterization_QualityBarOpening()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringStructuralDebtPrompt();

        // First bullet of Agent A's quality bar is unique to that agent
        result.Should().Contain(
            "## Quality Bar" + Environment.NewLine +
            Environment.NewLine +
            "- Every finding MUST have `crossReference`");
    }

    // ── Agent B: Correctness & Hygiene ────────────────────────────────────────

    [Fact]
    public void BuildRefactoringCorrectnessPrompt_Characterization_PreamblePrecedesTitle()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringCorrectnessPrompt();

        var preamblePos = result.IndexOf("## CRITICAL RULES — Read First", StringComparison.Ordinal);
        var titlePos = result.IndexOf("# Agent B: Correctness & Hygiene Detection", StringComparison.Ordinal);

        preamblePos.Should().BeGreaterThanOrEqualTo(0, "the sub-agent preamble must be present");
        titlePos.Should().BeGreaterThan(preamblePos, "the preamble must appear before the agent title");
    }

    [Fact]
    public void BuildRefactoringCorrectnessPrompt_Characterization_IntroTextExact()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringCorrectnessPrompt();

        result.Should().Contain(
            "# Agent B: Correctness & Hygiene Detection" + Environment.NewLine +
            Environment.NewLine +
            "You are one of three parallel analysis agents. Your focus is **correctness and hygiene** —" + Environment.NewLine +
            "concrete issues that are wrong, dead, or misleading." + Environment.NewLine +
            Environment.NewLine);
        // TODO [WARNING] (review finding: TestQualityReviewer agent) — Same gap as Agent A's IntroTextExact:
        // this assertion ends at the trailing blank and does not verify that the Agent C–specific extra-intro
        // lines are absent from Agent B. See BuildRefactoringCorrectnessPrompt_Full_MatchesSnapshot in
        // PromptBuilderSnapshotTests.cs for the byte-for-byte guard.
    }

    [Fact]
    public void BuildRefactoringCorrectnessPrompt_Characterization_SectionOrder()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringCorrectnessPrompt();

        var categoriesPos = result.IndexOf("## Your Categories", StringComparison.Ordinal);
        // Agent B uses a distinctive exploration section title
        var explorationPos = result.IndexOf("## Exploration Strategy: Enumerate Then Verify", StringComparison.Ordinal);
        var outputPos = result.IndexOf("## Output Format", StringComparison.Ordinal);
        var qualityPos = result.IndexOf("## Quality Bar", StringComparison.Ordinal);

        categoriesPos.Should().BeGreaterThan(0, "## Your Categories must be present");
        explorationPos.Should().BeGreaterThan(categoriesPos, "## Exploration Strategy: Enumerate Then Verify must follow ## Your Categories");
        outputPos.Should().BeGreaterThan(explorationPos, "## Output Format must follow ## Exploration Strategy");
        qualityPos.Should().BeGreaterThan(outputPos, "## Quality Bar must follow ## Output Format");
    }

    [Fact]
    public void BuildRefactoringCorrectnessPrompt_Characterization_QualityBarOpening()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringCorrectnessPrompt();

        // First bullet of Agent B's quality bar is unique to that agent
        result.Should().Contain(
            "## Quality Bar" + Environment.NewLine +
            Environment.NewLine +
            "- **Dead code findings MUST include proof of zero usage**");
    }

    // ── Agent C: Design Consistency ───────────────────────────────────────────

    [Fact]
    public void BuildRefactoringDesignConsistencyPrompt_Characterization_PreamblePrecedesTitle()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringDesignConsistencyPrompt();

        var preamblePos = result.IndexOf("## CRITICAL RULES — Read First", StringComparison.Ordinal);
        var titlePos = result.IndexOf("# Agent C: Design Consistency Detection", StringComparison.Ordinal);

        preamblePos.Should().BeGreaterThanOrEqualTo(0, "the sub-agent preamble must be present");
        titlePos.Should().BeGreaterThan(preamblePos, "the preamble must appear before the agent title");
    }

    [Fact]
    public void BuildRefactoringDesignConsistencyPrompt_Characterization_IntroTextExact()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringDesignConsistencyPrompt();

        // Agent C has additional intro lines after the standard "You are one of three" opening
        result.Should().Contain(
            "# Agent C: Design Consistency Detection" + Environment.NewLine +
            Environment.NewLine +
            "You are one of three parallel analysis agents. Your focus is **design consistency** —" + Environment.NewLine +
            "patterns where naming, typing, or API shape deviates from the project's own conventions." + Environment.NewLine +
            Environment.NewLine +
            "**This agent depends heavily on `.agent/refactoring-conventions.json`.** Read it first." + Environment.NewLine +
            "Your job is to find deviations from the project's OWN standards, not generic best practices." + Environment.NewLine +
            Environment.NewLine);
    }

    [Fact]
    public void BuildRefactoringDesignConsistencyPrompt_Characterization_SectionOrder()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringDesignConsistencyPrompt();

        var categoriesPos = result.IndexOf("## Your Categories", StringComparison.Ordinal);
        var explorationPos = result.IndexOf("## Exploration Strategy", StringComparison.Ordinal);
        var outputPos = result.IndexOf("## Output Format", StringComparison.Ordinal);
        var qualityPos = result.IndexOf("## Quality Bar", StringComparison.Ordinal);

        categoriesPos.Should().BeGreaterThan(0, "## Your Categories must be present");
        explorationPos.Should().BeGreaterThan(categoriesPos, "## Exploration Strategy must follow ## Your Categories");
        outputPos.Should().BeGreaterThan(explorationPos, "## Output Format must follow ## Exploration Strategy");
        qualityPos.Should().BeGreaterThan(outputPos, "## Quality Bar must follow ## Output Format");
    }

    [Fact]
    public void BuildRefactoringDesignConsistencyPrompt_Characterization_QualityBarOpening()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringDesignConsistencyPrompt();

        // First bullet of Agent C's quality bar is unique to that agent
        result.Should().Contain(
            "## Quality Bar" + Environment.NewLine +
            Environment.NewLine +
            "- **Naming findings require a convention rule reference.**");
    }
}
