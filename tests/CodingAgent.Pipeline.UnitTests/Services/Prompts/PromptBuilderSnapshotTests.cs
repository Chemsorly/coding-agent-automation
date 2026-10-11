using System.Text.RegularExpressions;
using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services.Prompts;

namespace CodingAgent.Pipeline.UnitTests.Services.Prompts;

/// <summary>
/// Snapshot tests that verify the full generated output of every Build* method
/// touched by the prompt-section-emitter consolidation (issue #3441).
///
/// PURPOSE: These tests act as a behaviour-preserving guard. They capture the exact
/// string produced by the current code BEFORE any refactoring and confirm it is
/// unchanged AFTER the refactoring. A diff in these tests means a semantic change
/// snuck in alongside the structural cleanup.
///
/// MAINTENANCE: When intentional prompt wording changes are made, update the
/// snapshot constants to match the new output. Each constant documents the exact
/// prompt text at the time of the last intentional change.
/// </summary>
public class PromptBuilderSnapshotTests
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Shared test fixtures — minimal, deterministic inputs
    // ─────────────────────────────────────────────────────────────────────────

    private static readonly IssueDetail TestIssue = new()
    {
        Identifier = "1",
        Title = "Test Issue",
        Description = "Test description.",
        Labels = []
    };

    private static readonly ParsedIssue TestParsed = new()
    {
        RequirementsSection = "Test requirements.",
        AcceptanceCriteria = ["Criterion one", "Criterion two"]
    };

    // ─────────────────────────────────────────────────────────────────────────
    //  1. PromptBuilder.BuildReviewPrompt (inlineCommentsEnabled: true)
    //     Exercises AppendStructuredOutputInstructions (visibility change).
    // ─────────────────────────────────────────────────────────────────────────

    private const string Snapshot_BuildReviewPrompt_InlineEnabled = """
Report findings only. Do not modify source code or project files.


You are reviewing code changes made by another agent. You have no prior context about how or why these changes were made — judge purely on correctness, security, and adherence to requirements.

The diff has been pre-computed for you. Read these files to understand the changes:
- `.agent/diff-stat.txt` — summary of changed files with line counts (read this FIRST to triage)
- `.agent/full-diff.txt` — full diff between origin/main and the working tree

IMPORTANT: Do NOT run `git diff` yourself — the diff is already captured in the files above. Read the diff-stat first to identify which files are relevant to your review focus, then selectively read sections of the full diff for those files. You do NOT need to read the entire full-diff file.

You may also run read-only git commands for additional context:
- `git log origin/main..HEAD --oneline` — shows commits on the branch
- `git status` — shows working tree state (NOTE: untracked files are expected — the pipeline automatically stages and commits ALL new/modified files before creating the PR)

Review carefully.


## Thoroughness

Be exhaustive within your domain. Don't stop at the first finding — scan the entire scope systematically. Consider what's MISSING (untested paths, unhandled errors, missing validation) as much as what's wrong. Check interactions between changed components — a change in one area may break assumptions in another.


## Calibration

SEVERITY GUIDELINES:
- [CRITICAL] requires a concrete scenario: an exact input that triggers failure, or for concurrency issues, a specific interleaving that produces inconsistent state. If you cannot construct such a scenario, downgrade to [WARNING].
- [WARNING] requires identifying specific code that deviates from best practice or could fail under documented conditions. Vague "might cause issues" is not sufficient.
- [SUGGESTION] is for improvements that don't indicate a defect.

FINDING LINES:
Start a line with a severity marker only when the line is a finding, and keep each finding on that one line. Every line that starts with a marker is counted as a finding; when you name a severity anywhere else, write it as a plain word ("no critical issues").

ACCURACY OVER THOROUGHNESS:
Scan the entire diff systematically, but only report findings you can support with a specific code path or scenario. A review with 0 findings is a valid outcome when the code is correct. Flagging correct code wastes engineering time.

Judge the CODE DIFF only. Ignore commit messages, branch names, PR titles, or any framing about the developer's intent. A defect is present or absent regardless of how the change is described.

Write your findings to the file `.agent/review-findings.md` in the workspace. Do NOT print the findings to stdout — only write them to that file.

Do NOT run git write commands (git add, git commit, git push, git checkout, git reset, etc.). The pipeline handles all version control operations — it automatically stages and commits ALL new and modified files (including untracked files). Read-only git commands (git log, git diff, git status, git show) are fine.

Below is the original issue for reference. Review the changes against these requirements.

# Issue #1: Test Issue

The full issue description and discussion thread are at `.agent/issue-context.md` — read it for context.

## Acceptance Criteria
- Criterion one
- Criterion two


## Output Format

Format each finding on its own line using this structure:
[SEVERITY] path/to/file.ext:LINE — description of the issue

Where:
- SEVERITY is one of: CRITICAL, WARNING, SUGGESTION
- path is relative to the repository root using forward slashes
- LINE is the 1-based line number in the file
- description explains the finding

Example:
[CRITICAL] src/Service.cs:42 — Null reference possible when input is not validated
[WARNING] src/Controllers/UserController.cs:15 — Missing input validation on email parameter

For findings without a specific file location:
[WARNING] — General observation about architecture
""";

    [Fact]
    public void BuildReviewPrompt_InlineEnabled_MatchesSnapshot()
    {
        var result = PromptBuilder.BuildReviewPrompt(
            "Review carefully.",
            TestIssue,
            TestParsed,
            findingsFilePath: ".agent/review-findings.md",
            inlineCommentsEnabled: true);

        result.Should().Be(Snapshot_BuildReviewPrompt_InlineEnabled);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  2. PromptBuilder.BuildAnalysisPrompt (brainContextWritten: true)
    //     Exercises the extracted AppendBrainContextReference helper.
    // ─────────────────────────────────────────────────────────────────────────

    private const string Snapshot_BuildAnalysisPrompt_BrainContextWritten = """
Analysis only. Do not modify source code. Write output to designated files only.


Analyze carefully.


## Thoroughness

Be exhaustive within your domain. Don't stop at the first finding — scan the entire scope systematically. Consider what's MISSING (untested paths, unhandled errors, missing validation) as much as what's wrong. Check interactions between changed components — a change in one area may break assumptions in another.

Do NOT implement any changes. Only analyze and recommend.

Write your analysis to the file `.agent/analysis.md` in the workspace. Do NOT print the analysis to stdout — only write it to that file.

Use sub-agents to cover more ground and provide a thorough analysis. For example, delegate parallel investigations to explore different parts of the codebase — one sub-agent could examine the data layer while another looks at the UI components, or one traces the call chain while another checks for test coverage gaps. This produces a more complete picture than a single-threaded read-through.

# Issue #1: Test Issue

The full issue description and discussion thread are at `.agent/issue-context.md` — read it for context.

## Acceptance Criteria
- Criterion one
- Criterion two

After writing your analysis to `.agent/analysis.md`, also write a structured assessment to `.agent/analysis-assessment.json` with this exact JSON schema:

```json
{
  "recommendation": "ready",
  "reason": "Issue is well-scoped with clear acceptance criteria",
  "concerns": ["Non-blocking concern"],
  "blockingIssues": [],
  "plannedApproach": "One-line implementation strategy",
  "estimatedComplexity": "moderate"
}
```

Set `recommendation` to:
- `"ready"` if the issue is clear, well-scoped, and you have a concrete implementation plan.
- `"not_ready"` if the issue is too vague, contradictory, has hard blockers, requires information you can't determine from the codebase, OR if the scope is too broad for a single agent run (heuristic: changes affecting >30 files or spanning >3 distinct projects). When rejecting for scope, include splitting recommendations in `blockingIssues` (e.g., "Split by concern: UI changes, data layer, test updates"). Add any blocking issues to `blockingIssues`.
- `"wont_do"` if, after analyzing the codebase, you determine no code changes are needed. This includes: bugs that can't be reproduced, issues that are already fixed, features that are already implemented, or behavior that is working as designed. Explain your reasoning in `reason`. **Important:** if the production code fix described in the issue is already present on the branch, set `"wont_do"` — do NOT pivot to `"ready"` on the grounds that test coverage could still be added. Test-only follow-up work belongs in a separate issue.

Project knowledge and conventions are at `.agent/brain-context.md` — consult it for coding standards and patterns.

Analyze the workspace now and write your recommendation to `.agent/analysis.md`.
""";

    [Fact]
    public void BuildAnalysisPrompt_BrainContextWritten_MatchesSnapshot()
    {
        var result = PromptBuilder.BuildAnalysisPrompt(
            "Analyze carefully.",
            TestIssue,
            TestParsed,
            brainContextWritten: true);

        result.Should().Be(Snapshot_BuildAnalysisPrompt_BrainContextWritten);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  3. PromptBuilder.BuildPrompt (brainContextWritten: true)
    //     Exercises the extracted AppendBrainContextReference helper.
    // ─────────────────────────────────────────────────────────────────────────

    private const string Snapshot_BuildPrompt_BrainContextWritten = """
Implement carefully.

## Verification Before Use

- Confirm method exists and signature matches before calling.
- Verify import targets and file paths exist before referencing.
- Confirm parameter names, types, and order match the declaration.
- No stubs or placeholders. Every method must contain real logic.
- If referenced code is missing, search for it. Adapt — never fabricate.

Do NOT run git write commands (git add, git commit, git push, git checkout, git reset, etc.). The pipeline handles all version control operations — it automatically stages and commits ALL new and modified files (including untracked files). Read-only git commands (git log, git diff, git status, git show) are fine.
Fix the cause, not the check. Do NOT add or widen exclusions, suppressions or ignore rules in static-analysis, coverage, lint or CI configuration (for example `sonar.*.exclusions`, `NOSONAR`, `#pragma warning disable`, `[SuppressMessage]`, `eslint-disable`, coverage excludes or CI workflow files). Do NOT skip, disable or delete tests. Do NOT change the expected value of an existing test to match new output unless the issue asks for the behaviour change that test covers. These rules do not apply to a change the issue explicitly asks for. If a check cannot pass without breaking one of these rules, leave it failing and say why; the pipeline then keeps the PR as a draft for a person to decide.
The analysis for this issue is at `.agent/analysis.md` — read it before implementing.

# Issue #1: Test Issue

The full issue description and discussion thread are at `.agent/issue-context.md` — read it for context.

## Acceptance Criteria
- Criterion one
- Criterion two

Project knowledge and conventions are at `.agent/brain-context.md` — consult it for coding standards and patterns.

Implement these changes now.
""";

    [Fact]
    public void BuildPrompt_BrainContextWritten_MatchesSnapshot()
    {
        var result = PromptBuilder.BuildPrompt(
            "Implement carefully.",
            TestIssue,
            TestParsed,
            brainContextWritten: true);

        result.Should().Be(Snapshot_BuildPrompt_BrainContextWritten);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  4. ConsolidationPromptBuilder.BuildHarnessSuggestionPrompt
    //     Exercises the sb.AppendLine("## Output Format") site.
    //     NOTE: This method embeds DateTime.UtcNow, so we normalise the
    //     timestamp before comparing. Everything else must be stable.
    // ─────────────────────────────────────────────────────────────────────────

    private const string Snapshot_BuildHarnessSuggestionPrompt_Normalised = """
# Harness Improvement Analysis

You are analyzing accumulated pipeline run feedback to identify recurring patterns and suggest improvements.

## Feedback Context

- **Total runs with feedback:** 10
- **Overall success rate:** 75.0%

The feedback data file (`feedback-data.json`) in this workspace contains the raw RunFeedback entries from pipeline runs.
Each entry includes harness feedback (pipeline/tool issues) and optionally issue feedback (issue/repo quality problems).

## Analysis Instructions

1. Read the feedback data file completely
2. Identify recurring patterns across multiple runs — look for repeated categories, similar stuck reasons, and common missing capabilities
3. Rank patterns by **frequency** (how many runs mention it) and **impact** (how much it affects success rate)
4. Produce the **top 3-5 improvement suggestions** that would have the highest positive impact

## Suggestion Quality Requirements

Each suggestion MUST be:
- **Concrete and actionable** — specify exactly what to change (e.g., "Add file X to the initial context provided to the agent" not "Provide more context")
- **Grounded in evidence** — reference at least 3 specific feedback entries (by their category or stuckReason text) that motivate the suggestion
- **Scoped to one change** — each suggestion addresses one improvement, not a bundle of changes

Do NOT produce abstract recommendations like "improve error handling" or "add more tests".
Every suggestion must reference specific feedback patterns and propose a specific change.

## Output Format

Produce your analysis as a JSON object with the following structure:

```json
{
  "generatedAtUtc": "TIMESTAMP",
  "basedOnRunCount": 10,
  "successRate": 75.0,
  "suggestions": [
    {
      "text": "The concrete suggestion text — what to change and how",
      "rationale": "Why this would help, referencing specific feedback patterns",
      "frequency": 5
    }
  ]
}
```

The `frequency` field indicates how many runs contributed to this observation.
Order suggestions by impact (highest first).

""";

    [Fact]
    public void BuildHarnessSuggestionPrompt_MatchesSnapshot()
    {
        var result = ConsolidationPromptBuilder.BuildHarnessSuggestionPrompt(
            feedbackCount: 10, successRate: 75.0m);

        // Normalise the volatile timestamp so the comparison is stable
        var normalised = Regex.Replace(
            result,
            @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z",
            "TIMESTAMP");

        normalised.Should().Be(Snapshot_BuildHarnessSuggestionPrompt_Normalised);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  5. DecompositionPromptBuilder.BuildDecompositionPrompt
    //     Exercises the sb.AppendLine("## Output Format") site.
    // ─────────────────────────────────────────────────────────────────────────

    private const string Snapshot_BuildDecompositionPrompt = """
# Epic Decomposition — Sub-Issue Generation

You are generating full implementation-ready sub-issue descriptions from an approved decomposition plan.
The approved plan is available in the issue comments within `.agent/issue-context.md`.

## Context

Read the approved plan from `.agent/issue-context.md` (the plan comment is identified by the
`<!-- agent:decomposition-plan -->` marker in the comment thread).
Also explore the codebase to produce accurate file paths and implementation details.

## Deduplication

If existing agent-generated sub-issues are listed in the context, do NOT duplicate them.
Only produce sub-issues for items in the plan that are not already created.

## Output Format

Produce full issue descriptions as JSON files at `.agent/sub-issues/{NN}-{title-slug}.json`
where `{NN}` is a zero-padded two-digit sequence number (01, 02, ...) and `{title-slug}` is a
lowercase, hyphen-separated slug derived from the title (max 60 characters).

Produce at most **5** sub-issue files.

Each JSON file MUST conform to this schema:

```json
{
  "title": "Short descriptive title (max 256 characters)",
  "body": "Full markdown issue body (see template below)",
  "dependencies": ["Title of another sub-issue this depends on"],
  "labels": ["enhancement"]
}
```

### Required Fields

- **title** — Non-empty string, maximum 256 characters
- **body** — Non-empty markdown string following the issue template
- **dependencies** — Array of title strings referencing other sub-issues in this decomposition (use exact titles, not issue numbers)
- **labels** — Array of additional label strings (the `agent:next` and `agent:generated` labels are applied automatically)

## Issue Body Template

The `body` field MUST include the following sections in order:

1. **## Summary** — 2-3 sentence problem statement
2. **## Affected Components** — List of file paths with brief roles
3. **## Requirements** — Hard constraints the implementation must satisfy
4. **## Acceptance Criteria** — Specific observable outcomes (checkboxes)

Optional sections (include when relevant):
- **## Suggested Approach** — One possible implementation path
- **## Out of Scope** — What this issue intentionally does NOT cover
- **## Related Issues** — References to related issues

## Dependency Ordering

Order files so that **dependencies always point backward** — earlier-numbered sub-issues
are depended upon by later-numbered ones. The numeric prefix in the filename determines
creation order, so a sub-issue at `02-*.json` can depend on `01-*.json` but NOT vice versa.

Use exact title strings in the `dependencies` array (they will be resolved to `#N` format during creation).

## Constraints

- Each sub-issue must create or modify a maximum of **12 files**
- Each sub-issue must have exactly **one verification criterion** in its acceptance criteria
- Each sub-issue must be completable in **one agent run**
- All sub-issue titles must be **unique**
- Files must be encoded as **UTF-8 without BOM**
- Do NOT create any source code files. Only produce the sub-issue JSON files.


""";

    [Fact]
    public void BuildDecompositionPrompt_MatchesSnapshot()
    {
        var result = DecompositionPromptBuilder.BuildDecompositionPrompt(
            maxSubIssues: 5, maxFiles: 12);

        result.Should().Be(Snapshot_BuildDecompositionPrompt);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  6–8. ConsolidationPromptBuilder: the three Phase 1 refactoring prompts
    //       Each exercises AppendFindingsOutputFormat → OutputFormatHeading.
    //       We snapshot just the section from "## Output Format" to end to keep
    //       the constants maintainable and avoid capturing the entire preamble.
    //
    //       These partial-suffix snapshots were NOT modified by issue #3537. Their
    //       continued passage (they are run by every dotnet test invocation) proves
    //       that the scaffold refactoring preserved the Output Format + Quality Bar
    //       suffix byte-for-byte. Tests 12–14 below extend this to full-prompt coverage.
    //       Tests 15–17 cross-validate the full-output snapshots (12–14) against these
    //       partial-suffix snapshots, so 6–8 must remain as reference anchors.
    //
    // TODO [WARNING] Tests 6–8 and tests 12–14 cover the same byte range (## Output Format
    // onward). Tests 15–17 now depend on tests 6–8 remaining as the authoritative reference
    // for that range, so they cannot simply be removed. The redundancy is intentional:
    // tests 6–8 serve as the stable reference anchor for cross-validation; tests 12–14
    // cover the full string. If tests 12–14 are ever removed, tests 6–8 revert to their
    // original role. If tests 6–8 are removed, tests 15–17 must be removed too, and the
    // AC3 cross-validation evidence disappears. Document any future removal here.
    // (Review finding: TestQualityReviewer agent, PromptBuilderSnapshotTests.cs:459)
    // ─────────────────────────────────────────────────────────────────────────

    private const string Snapshot_BuildRefactoringStructuralDebtPrompt_OutputFormatSection = """
## Output Format

Write findings to `.agent/refactoring-structural-findings.json` as a JSON object:

```json
{
  "findings": [
    {
      "title": "Short descriptive title",
      "category": "duplication|structural-drift|complexity|over-engineering",
      "affectedFiles": ["src/path/to/File.cs"],
      "evidence": "Concrete code snippet or line reference proving the issue",
      "evidenceSources": ["code-reading:File.cs:L42", "grep:catch (Exception ex)", "tool:dotnet-build:CA1502"],
      "crossReference": "Second file/location that corroborates (duplication partner, drift boundary, etc.)",
      "scopeQuery": "When the finding is one instance of a repeated pattern: the search that lists every instance, e.g. git grep -n 'pattern' -- src",
      "impact": "What goes wrong because of this — be specific",
      "suggestedFix": "Brief approach, not full implementation"
    }
  ],
  "notChecked": ["Files or areas you skipped, and why"]
}
```

## Quality Bar

- Every finding MUST have `crossReference` — a second location proving the issue isn't isolated.
  For duplication: the other copy. For drift: the layer rule violated + the import. For complexity: the callers affected.
- For duplication, find EVERY copy, not just two: put the search that lists them in `scopeQuery`.
- Findings about over-engineering require proof the abstraction is never extended: check all implementations
  of the interface, check test mocks, check DI registrations, check git history for attempts to add implementations.
- **Do NOT flag patterns listed in `intentionalPatterns`.** If unsure, skip it.
- Prefer fewer high-quality findings over many shallow ones. Maximum 10 findings.


""";

    [Fact]
    public void BuildRefactoringStructuralDebtPrompt_OutputFormatSection_MatchesSnapshot()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringStructuralDebtPrompt();

        var outputFormatIdx = result.IndexOf("## Output Format", StringComparison.Ordinal);
        outputFormatIdx.Should().BeGreaterThan(0, "prompt must contain ## Output Format section");
        var section = result[outputFormatIdx..];

        section.Should().Be(Snapshot_BuildRefactoringStructuralDebtPrompt_OutputFormatSection);
    }

    private const string Snapshot_BuildRefactoringCorrectnessPrompt_OutputFormatSection = """
## Output Format

Write findings to `.agent/refactoring-correctness-findings.json` as a JSON object:

```json
{
  "findings": [
    {
      "title": "Short descriptive title",
      "category": "todo|dead-code|bug|stale-documentation",
      "affectedFiles": ["src/path/to/File.cs"],
      "evidence": "The exact code snippet or comment text proving the issue",
      "evidenceSources": ["grep:TODO", "tool:dotnet-build:IDE0051", "usage-search:FooService.Bar:0-callers"],
      "crossReference": "For dead code: proof of zero callers. For bugs: the code path that triggers it. For stale docs: the actual behavior vs documented behavior.",
      "scopeQuery": "When the finding is one instance of a repeated pattern: the search that lists every instance, e.g. git grep -n 'pattern' -- src",
      "impact": "What goes wrong or what cognitive cost this imposes",
      "suggestedFix": "Brief approach"
    }
  ],
  "notChecked": ["Files or areas you skipped, and why"]
}
```

## Quality Bar

- **Dead code findings MUST include proof of zero usage** — either tool output or a usage search showing no callers.
  Do NOT flag code as dead without searching for references. Reflection, DI registration, and test mocks can create invisible references.
- **Bug findings MUST demonstrate a concrete failure scenario** — not "this could fail" but "when X is null at L42, L47 dereferences it without a guard."
- **TODO findings must include the surrounding context** — the comment alone is not enough. Show what's incomplete or broken.
- **Stale doc findings must show both** the documented claim AND the actual code behavior side-by-side.
- Findings sourced from deterministic tools (grep, linter, compiler warnings) are inherently higher quality.
  Tag them as described in the critical rules.
- Maximum 10 findings. Prefer bugs > dead code > stale docs > TODOs (by impact).


""";

    [Fact]
    public void BuildRefactoringCorrectnessPrompt_OutputFormatSection_MatchesSnapshot()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringCorrectnessPrompt();

        var outputFormatIdx = result.IndexOf("## Output Format", StringComparison.Ordinal);
        outputFormatIdx.Should().BeGreaterThan(0, "prompt must contain ## Output Format section");
        var section = result[outputFormatIdx..];

        section.Should().Be(Snapshot_BuildRefactoringCorrectnessPrompt_OutputFormatSection);
    }

    private const string Snapshot_BuildRefactoringDesignConsistencyPrompt_OutputFormatSection = """
## Output Format

Write findings to `.agent/refactoring-design-findings.json` as a JSON object:

```json
{
  "findings": [
    {
      "title": "Short descriptive title",
      "category": "naming-inconsistency|primitive-obsession",
      "affectedFiles": ["src/path/to/File.cs"],
      "evidence": "The specific naming deviation or primitive usage with concrete examples",
      "evidenceSources": ["grep:string repositoryUrl", "usage-search:repositoryUrl:5-signatures"],
      "crossReference": "For naming: the convention rule violated + examples of correct naming elsewhere. For primitives: multiple locations using the same raw type for the same concept.",
      "scopeQuery": "When the finding is one instance of a repeated pattern: the search that lists every instance, e.g. git grep -n 'pattern' -- src",
      "impact": "Cognitive cost, confusion risk, or bug risk from the inconsistency",
      "suggestedFix": "Brief approach — rename to X, introduce value type Y, extract constant Z"
    }
  ],
  "notChecked": ["Files or areas you skipped, and why"]
}
```

## Quality Bar

- **Naming findings require a convention rule reference.** "This name seems odd" is not a finding.
  "Convention says services end with 'Service' but `FooHandler` doesn't follow this" IS a finding.
- **Primitive obsession findings require 3+ occurrences.** A single string parameter is not primitive obsession.
  The same concept passed as raw string through 3+ call sites IS primitive obsession.
- Every naming and primitive-obsession finding needs a `scopeQuery` that lists all occurrences, so the fix is complete.
- **Do NOT flag naming in test projects** unless conventions.json explicitly covers test naming.
- **Do NOT flag names that match `intentionalPatterns`** from conventions.json.
- This agent has the highest false-positive risk. Be conservative. Maximum 8 findings.


""";

    [Fact]
    public void BuildRefactoringDesignConsistencyPrompt_OutputFormatSection_MatchesSnapshot()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringDesignConsistencyPrompt();

        var outputFormatIdx = result.IndexOf("## Output Format", StringComparison.Ordinal);
        outputFormatIdx.Should().BeGreaterThan(0, "prompt must contain ## Output Format section");
        var section = result[outputFormatIdx..];

        section.Should().Be(Snapshot_BuildRefactoringDesignConsistencyPrompt_OutputFormatSection);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  9. ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt
    //     Exercises the direct sb.AppendLine(OutputFormatHeading) call in Phase 2
    //     (this site is invisible to the git grep criterion but must also be fixed).
    // ─────────────────────────────────────────────────────────────────────────

    private const string Snapshot_BuildRefactoringAggregationPrompt_OutputFormatSection = """
## Output Format

Produce the final proposals at `.agent/refactoring-proposals.json` as a JSON array:

```json
[
  {
    "title": "Short descriptive title of the refactoring opportunity",
    "category": "duplication|structural-drift|complexity|over-engineering|todo|dead-code|bug|stale-documentation|naming-inconsistency|primitive-obsession",
    "affectedFiles": ["src/path/to/File1.cs", "src/path/to/File2.cs"],
    "rationale": "The problem: what goes wrong or what it costs, and why it matters",
    "description": "The change: what to change, as one concrete approach",
    "evidence": "File.cs:L42-L47\n<the decisive code or tool output, quoted verbatim>",
    "evidenceSources": ["tool:dotnet-build:IDE0051", "usage-search:Foo.Bar:0-callers", "code-reading:File.cs:L42"],
    "scopeQuery": "git grep -n 'pattern' -- src",
    "prerequisites": ["Add characterization tests for X before refactoring"],
    "estimatedEffort": "small|medium|large",
    "riskLevel": "low|medium|high",
    "technique": "Extract Method|Inline Class|Rename|Introduce Value Type|etc.",
    "acceptanceCriteria": [
      "Zero references to old name OldService remain in .cs files",
      "Extracted class registered in DI container"
    ]
  }
]
```

### Field Definitions

- **category** (required) — the category of the finding the proposal comes from, exactly as listed in the schema above.
- **rationale** (required) — the problem. State what goes wrong, or what it costs, and commit to it: "X causes Y because Z."
  For `bug`, state the failure scenario: the input or state, and the wrong outcome.
- **description** (required) — the change. Propose ONE approach; do not offer alternatives ("X or Y").
- **evidence** (required) — the decisive code or tool output, quoted verbatim, starting with the file and line range.
  Copy it from the sub-agent finding; do not paraphrase.
- **evidenceSources** (required) — list of evidence that supports this proposal. Prefix with type:
  `tool:` (linter/compiler output), `code-reading:` (manual inspection), `grep:` (pattern search),
  `usage-search:` (reference count), `hotspot:` (git frequency). Multi-source proposals are higher quality.
- **scopeQuery** — the search that lists every instance this proposal must change. Required when the finding is one
  instance of a repeated pattern (always for `duplication`, `naming-inconsistency`, `primitive-obsession`).
  Every match is in scope unless `description` excludes it by name. Run it before you finish: every file it
  matches must be in `affectedFiles` or excluded in `description`.
- **prerequisites** — prep work needed. If affected files lack test coverage, MUST include
  "Add characterization tests for X before refactoring". Do NOT reference other proposals by number
  (e.g., "proposal #1") — GitHub will autolink #N to wrong issues.
- **estimatedEffort** — `small` (<5 files), `medium` (5-15 files), `large` (15-30 files).
- **riskLevel** — `low` (rename/move), `medium` (extract/restructure), `high` (interface changes).
- **technique** — named refactoring pattern if applicable.
- **acceptanceCriteria** (required, 2-4 items) — Verifiable conditions proving the refactoring was correctly applied.
  A separate review agent validates each criterion against the PR diff. Criteria drive automatic fix iterations
  when violated — write them to catch what a careless implementation would miss.

  Rules:
  - Describe WHAT must be true after, not HOW to do it. Good: "No remaining callers of X". Bad: "Create file Y.cs"
  - Must be verifiable from the git diff or test results alone. No runtime, no benchmarks, no subjective quality.
  - Do NOT repeat pipeline invariants ("build passes", "tests pass") — those are enforced separately.
  - Each criterion must test a DISTINCT concern. No rephrased duplicates.
  - Prefer negative assertions ("no references remain", "no callers exist") — they catch incomplete implementations.
  - When `scopeQuery` is set, one criterion MUST state that no match of the pattern remains (apart from the exclusions in `description`).
  - For `bug`, one criterion MUST require a test that reproduces the failure scenario and passes after the fix.
  - Do NOT use #N notation — GitHub autolinks to wrong issues.

  Examples by category:
  - dead-code: "No remaining callers or references to deleted methods in .cs files"
  - rename: "Zero occurrences of old name in source, test, and configuration files"
  - extract-class: "Original class no longer contains the extracted methods", "New class is registered in DI"
  - duplication: "Duplicated logic consolidated to single call site"
  - bug: "A test that sets X to null before calling Y fails before the fix and passes after it"

## Writing for the Implementer

Each proposal becomes an issue for an engineer who has not seen this analysis:
- Do NOT mention the analysis agents (A, B, C), phases, scores, rankings, or this scan in any text field.
- Keep the issue self-contained: name the files, symbols and line ranges the engineer needs.
- Keep it short. A shorter, well-scoped issue is more likely to be implemented correctly.

## Scope Constraints

Each proposal MUST be achievable by a single agent in one run:
- Maximum ~30 affected files (source + test) per proposal
- If a finding would touch more files, drop it and record it in the analysis log as scope-exceeded
- Prefer mechanical, low-risk changes over sweeping architectural ones
- Do NOT propose changes spanning serialization boundaries simultaneously
- One concern per proposal: do not bundle two classes' decompositions into one proposal

## Also Produce

Write a brief analysis log at `.agent/refactoring-analysis.md` containing:
- Total findings received from agents A, B, C, and any agent whose findings file was missing or invalid
- How many were dropped (duplicates, convention-filtered, evidence gate, scope-exceeded)
- The ranking scores for the top candidates
- Which findings were dropped and why (one line each)
- The `notChecked` areas the agents reported

""";

    [Fact]
    public void BuildRefactoringAggregationPrompt_OutputFormatSection_MatchesSnapshot()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt();

        var outputFormatIdx = result.IndexOf("## Output Format", StringComparison.Ordinal);
        outputFormatIdx.Should().BeGreaterThan(0, "prompt must contain ## Output Format section");
        var section = result[outputFormatIdx..];

        section.Should().Be(Snapshot_BuildRefactoringAggregationPrompt_OutputFormatSection);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  10. DecompositionPromptBuilder.BuildAnalysisPrompt
    //      Added by issue #3534 as a characterization guard before section-builder
    //      refactoring. Full-prompt snapshot — any prose change will fail this test.
    // ─────────────────────────────────────────────────────────────────────────

    private const string Snapshot_BuildAnalysisPrompt_Decomposition = """
# Epic Decomposition Analysis

You are performing a decomposition analysis of an epic issue.
Your goal is to explore the codebase and produce a structured decomposition plan
that breaks the epic into implementation-ready sub-issues.

## Exploration Strategy

Before proposing sub-issues, thoroughly explore the codebase:

1. **Directory tree** — Understand the project structure, solution layout, and module boundaries
2. **Architecture files** — Read README, design docs, and any `.brain/` knowledge if available
3. **DI setup** — Review dependency injection configuration to understand service wiring
4. **Similar features** — Find 1-2 existing features similar to the epic and study their implementation patterns

## Deduplication Check

Open issues are in `.agent/open-issues/` — read them to check for overlap before proposing sub-issues.
Do NOT propose sub-issues that duplicate work already tracked in existing open issues.
If you identify partial overlap, note the related issue in your plan and explain why your proposal is distinct.

## Re-run Feedback

If this is a re-run (a previous plan was rejected), look for comments posted after the
previous plan comment in `.agent/issue-context.md` as rejection feedback.
Address all feedback points in your revised plan.

## Gate Rejection Concerns

If `.agent/issue-context.md` contains analysis gate comments (marked with `<!-- agent:gate-rejection -->`),
treat each concern listed in the 'Blocking Issues' or 'Concerns' section as a **hard constraint**.
Your decomposition plan must explicitly address how each concern is resolved:

- For each gate concern, state which sub-issue handles it and how
- If a concern spans multiple sub-issues, explain the handoff between them
- If you believe a concern is invalid, explain why with evidence from the codebase

Do NOT treat gate concerns as generic "split it up" guidance — they identify specific
technical risks that must be individually mitigated in your plan.

## Sub-Issue Sizing Constraints

Each proposed sub-issue MUST satisfy ALL of the following constraints:

- **File limit:** Create or modify a maximum of **12 files** (files only read for context do not count)
- **One verification criterion:** Exactly one pass/fail assertion (e.g., "unit test X passes", "build succeeds with no warnings")
- **One agent run:** Completable in a single agent run (single context window, no multi-session work, no waiting on external feedback)

## Constraints

- Propose at most **5** sub-issues
- Order sub-issues so that **dependencies always point backward** — earlier-numbered sub-issues are depended upon by later-numbered ones
- Each sub-issue must have a unique, descriptive title
- Do NOT propose sub-issues that require multi-session execution or external feedback

## Output

Write your decomposition plan to `.agent/decomposition-plan.md` in the workspace.

The plan must include:

1. **Strategy rationale** — 2-3 sentences explaining why you split the epic this way
2. **Sub-issue table** with columns: #, Title, Scope (one sentence), Files (estimated count), Dependencies (by title or "None"), Verification (one criterion)
3. **Dependency graph** — Sub-issues listed in execution order showing blocking relationships

Do NOT create any source code files. Only produce the decomposition plan.


""";

    [Fact]
    public void BuildAnalysisPrompt_Decomposition_MatchesSnapshot()
    {
        var result = DecompositionPromptBuilder.BuildAnalysisPrompt(maxSubIssues: 5, maxFiles: 12);

        result.Should().Be(Snapshot_BuildAnalysisPrompt_Decomposition);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  11. ConsolidationPromptBuilder.BuildBrainConsolidationPrompt (null timestamp)
    //      Added by issue #3534 as a characterization guard before section-builder
    //      refactoring. Full-prompt snapshot with null timestamp — fully deterministic.
    // ─────────────────────────────────────────────────────────────────────────

    private const string Snapshot_BuildBrainConsolidationPrompt_NullTimestamp = """
# Brain Knowledge Repository Consolidation

You are performing a consolidation pass on the `.brain/` knowledge repository.
Your goal is to keep the knowledge base concise, accurate, and free of contradictions.

## Context

**No prior consolidation has occurred.** This is the first consolidation pass.
Review the entire knowledge base from scratch.

## Phase 1: Orient

Scan all files in the `.brain/` directory recursively. Build a mental inventory of:
- All knowledge files and their topics
- The directory structure and organization
- File sizes and last-modified indicators
- Any README or index files that describe the structure

Do NOT make changes during this phase. Only observe and catalog.

## Phase 2: Gather Signal

Read recent session logs and run summaries to identify:
- New lessons learned that may duplicate existing entries
- Technology decisions that have been superseded
- Relative date references (e.g., "yesterday", "last week") that should be absolute
- Entries that reference removed or renamed components
- Contradictions between different knowledge files
- **Citation data:** Which entries were referenced in session logs and how (helpful, not applicable, outdated)

### Citation Aggregation

Session logs contain a `## Brain Entries Referenced` section listing which knowledge entries
were consulted and their usefulness (`used, helpful` | `read, not applicable` | `used, outdated`).
Aggregate this data across all session logs since the last consolidation:
- Count how many sessions cite each entry as **helpful**
- Note entries cited as **outdated** by any session (candidates for correction)
- Identify entries in `general/`, `technology/`, and `projects/` that are **never cited** in any session log

Use this citation data to inform decisions in Phase 3 (Consolidate) and Phase 4 (Prune).

## Phase 2.5: Research & Verify

For entries that reference specific tools, libraries, versions, or external services:
- **Verify currency:** Check whether referenced library versions are still the latest (e.g., is the noted NuGet package version still current?)
- **Check for better alternatives:** If a workaround or pattern was documented because a tool lacked a feature, verify whether that feature has since been added
- **Validate links and references:** If entries reference external documentation URLs or API endpoints, verify they are still valid
- **Update outdated information:** If you find newer/better approaches to documented problems, update the entry with the current best practice and note the change

Use web search to verify information when uncertain. Only update entries where you have high confidence the information has changed — do not speculate.

## Phase 3: Consolidate

Apply the following transformations:
- **Merge duplicates:** Combine entries that describe the same concept into a single, authoritative entry
- **Resolve contradictions:** When two entries conflict, keep the more recent or more specific one. Add a note about what was superseded if relevant
- **Convert relative dates:** Replace relative time references with absolute dates (e.g., "yesterday" → "2026-01-15")
- **Update references:** Fix references to renamed or moved components
- **Improve organization:** Move misplaced entries to their correct section or file

## Phase 4: Prune

Remove content that no longer provides value:
- Stale entries about components that no longer exist
- Session logs older than 30 days that have already been distilled into lessons
- Redundant entries that were merged in Phase 3
- Empty or placeholder files

Use citation data from Phase 2 to inform pruning decisions:
- **High-value (keep):** Entries cited as helpful in 3+ sessions — these are proven useful
- **Outdated (correct or remove):** Entries cited as outdated by any session — verify and either update or mark ⚠️ OUTDATED
- **Uncited + stale (prune candidates):** Entries never cited in any session log AND older than their verification window
- **Recently written (keep):** Entries written since the last consolidation should be kept regardless of citation count — they haven't had time to be cited yet

When pruning an entry, check whether it has only `[experience]` sources and was never verified.
Entries with `[docs]` sources are more likely to be correct even if uncited — prefer re-verification over pruning.

Keep the index files (README.md) concise and up-to-date with the current structure.

## Phase 5: Generate Project SKILL.md

For each project directory under `.brain/projects/`, regenerate a `SKILL.md` file.
This file is a distilled, single-document summary that agents receive as pre-loaded context
via subagent retrieval — it should be the most useful file in the project folder.

**Regenerate from scratch each time** (do not incrementally edit the existing SKILL.md).
Cap content at ~1500 words. Structure it as:

```markdown
# Project: {project-name}

## Architecture
{Tech stack, key project structure, main components and their roles}

## Conventions
{Coding standards, naming patterns, preferred libraries, serialization choices}

## Known Pitfalls
{Common mistakes from lessons-learned, gotchas that cause build/test failures}

## Testing Patterns
{How tests are structured, commands to run, quarantine rules, CI quirks}

## Key Decisions
{Important architectural decisions and their rationale}
```

Source content from the project's brain entries, technology files, general lessons,
and session logs. Only include information that is current and verified.
If a project folder has very little accumulated knowledge, produce a shorter SKILL.md
with just the sections that have content — do not pad with generic advice.

## Output

Make all changes directly to the files. After completion, provide a brief summary of what was done:
- Number of files modified
- Number of entries merged
- Number of contradictions resolved
- Number of entries pruned
- Number of SKILL.md files generated/updated


""";

    [Fact]
    public void BuildBrainConsolidationPrompt_NullTimestamp_MatchesSnapshot()
    {
        var result = ConsolidationPromptBuilder.BuildBrainConsolidationPrompt(lastConsolidationUtc: null);

        result.Should().Be(Snapshot_BuildBrainConsolidationPrompt_NullTimestamp);
    }

    // TODO [WARNING] Only the null-timestamp path of BuildBrainConsolidationPrompt has a full-prompt snapshot
    // (test 11 above). The non-null path (lastConsolidationUtc.HasValue == true) renders different ## Context
    // body content and is only covered by spot-checks in ConsolidationPromptBuilderTests.cs. A regression that
    // breaks the HasValue branch output — including any AppendSection spacing — would not be caught by a snapshot.
    // Add a deterministic snapshot test for BuildBrainConsolidationPrompt with a fixed DateTime value.
    // (Review finding: PromptBuilderSnapshotTests.cs:785)

    // TODO [WARNING] DecompositionPromptBuilder.BuildCreationPrompt has no snapshot test at all. The diff replaces
    // eight AppendSection calls inside BuildCreationPrompt and also changes PromptBuilder.AppendOutputFormatHeading +
    // manual sb.AppendLine() with AppendSection(sb, "## Output Format", ...). Neither this file nor any other test
    // covers the full output of BuildCreationPrompt. A regression to any of those eight sections — including any
    // AppendSection spacing — would not be caught. Add a full-prompt snapshot for BuildCreationPrompt.
    // (Review finding: DotNetSpecialist agent, PromptBuilderSnapshotTests.cs:393)

    // TODO [WARNING] Snapshot tests 6–8 (BuildRefactoringStructuralDebtPrompt, BuildRefactoringCorrectnessPrompt,
    // BuildRefactoringDesignConsistencyPrompt) assert only on the tail of each prompt from ## Output Format onward.
    // The preceding sections — which were also converted to AppendSection in this PR — are not covered by any snapshot.
    // A regression to the preamble content or AppendSection spacing in those earlier sections would not be detected.
    // Add full-prompt snapshots (or at least preamble-section snapshots) for these three methods.
    // (Review finding: TestQualityReviewer agent, PromptBuilderSnapshotTests.cs:420)

    // ─────────────────────────────────────────────────────────────────────────
    //  12. ConsolidationPromptBuilder.BuildRefactoringStructuralDebtPrompt — full output
    //      Characterization test for the issue #3537 scaffold refactoring.
    //
    //      Output invariance note: this test was added in the same PR as the refactoring
    //      (issue #3537), so its snapshot constant was captured from the post-refactor code.
    //      It is therefore tautological with respect to proving that the output is unchanged
    //      relative to the pre-refactor baseline — the snapshot reflects whatever the
    //      refactored code happens to produce, not a reference captured before the change.
    //
    //      Cross-validation tests 15–17 (below) address this limitation by asserting that
    //      the tail of this snapshot matches the corresponding pre-existing partial-suffix
    //      snapshot (test 6), which predates this PR and was not modified by it. This
    //      cross-check provides the strongest locally-verifiable evidence that this
    //      snapshot's suffix is correct.
    //
    //      Any change to the output will fail this test.
    //
    // TODO [WARNING] This test uses full-string equality, so a regression anywhere in the
    // 100+ line preamble produces the same failure message as a regression in the quality
    // bar, reducing diagnostic precision. Consider decomposing into per-section assertions
    // (e.g. preamble, categories, exploration strategy, output format, quality bar) to make
    // failures easier to locate. Not blocking — the full-string check is still correct.
    // (Review finding: TestQualityReviewer agent, PromptBuilderSnapshotTests.cs:993)
    // ─────────────────────────────────────────────────────────────────────────

    private const string Snapshot_BuildRefactoringStructuralDebtPrompt_FullOutput = """
## CRITICAL RULES — Read First

1. **Evidence over speculation.** Every finding must cite a specific file path + line number or code snippet. "This looks complex" is not a finding.
2. **Use the available tools.** Phase 0 listed them under `availableTools` in `.agent/refactoring-conventions.json` and saved the output of the project's analyzers in `.agent/refactoring-tool-output/`. Tool output is higher-confidence evidence than your own code reading. Query the listed MCP tools for data about the code you examine. Do NOT run builds or other commands that write build output: the other two agents work in this workspace at the same time. Read-only analyzers that do not build are fine, and you are allowed to install them.
3. **Declare what you did NOT check.** List files/areas you skipped due to context limits in the `notChecked` field of your output.
4. **Reasoning length scales with severity.** 1-2 sentences for low-impact observations. 4-6 sentences with full evidence chain for high-impact findings.
5. **Do NOT modify source code.** Only produce the findings output file.
6. **Out of scope:** `.agent/`, `.brain/`, `.git/`, generated code, and files ignored by git. These are pipeline scratch space or tooling, not project code. Never report findings in them.
7. **Tag evidence honestly.** Each `evidenceSources` entry names how you got the evidence:
   - `tool:<tool>:<rule-id>` — output of a compiler, linter, analyzer or MCP tool that actually ran (by you or Phase 0). Nothing else is `tool:`.
   - `grep:<pattern>` — a text search, with the pattern you searched for.
   - `usage-search:<symbol>` — a reference search, with the result (e.g. `usage-search:FooService.Bar:0-callers`).
   - `code-reading:<file>:L<line>` — your own reading of the code.
   - `hotspot:<file>` — change frequency. This says how often a file changes, not that anything is wrong with it.
# Agent A: Structural Debt Detection

You are one of three parallel analysis agents. Your focus is **structural debt** —
patterns where incremental changes have created global incoherence.

## Your Categories

1. **Duplicated logic** — Similar code patterns repeated across multiple files that could be extracted.
   Look for: near-identical method bodies, copy-pasted error handling, repeated validation logic,
   similar DTO transformations in multiple locations.

2. **Structural drift** — Areas where the architecture has diverged from the intended design.
   Consult `.agent/refactoring-conventions.json` for layer rules. Look for: imports crossing
   layer boundaries, services doing work outside their responsibility, components that grew
   beyond their original scope.

3. **Overly complex areas** — Methods or classes that have grown too large or have too many responsibilities.
   Metrics: methods >50 lines, classes >500 lines, constructors with >6 parameters,
   methods with >4 levels of nesting. Focus on hotspot files — complexity in rarely-touched code
   is low priority.

4. **Over-engineering & unnecessary abstraction** — Wrapper classes that pass through without logic;
   factory/builder patterns where a constructor would suffice; configuration options nobody uses.
   **This category has the highest false-positive rate in published benchmarks.** An interface with one
   production implementation is NOT a finding when it is registered for dependency injection, has test
   doubles (mocks or fakes in test projects), or is the seam across a project boundary.
   **Check `.agent/refactoring-conventions.json` → `intentionalPatterns` before flagging.**
   If the project's philosophy is "minimal interfaces", a missing interface is NOT a finding.

## Exploration Strategy

1. Read `.agent/hotspot-analysis.txt` — start with the top 15 most-changed files
2. Read `.agent/refactoring-conventions.json` — understand what's intentional vs accidental
3. For each hotspot file: read it, assess structural health against the 4 categories above
4. Then read 5 files NOT in the hotspot list (stable but potentially problematic)
5. For duplication detection: when you find a pattern in one file, grep for similar patterns elsewhere

## Output Format

Write findings to `.agent/refactoring-structural-findings.json` as a JSON object:

```json
{
  "findings": [
    {
      "title": "Short descriptive title",
      "category": "duplication|structural-drift|complexity|over-engineering",
      "affectedFiles": ["src/path/to/File.cs"],
      "evidence": "Concrete code snippet or line reference proving the issue",
      "evidenceSources": ["code-reading:File.cs:L42", "grep:catch (Exception ex)", "tool:dotnet-build:CA1502"],
      "crossReference": "Second file/location that corroborates (duplication partner, drift boundary, etc.)",
      "scopeQuery": "When the finding is one instance of a repeated pattern: the search that lists every instance, e.g. git grep -n 'pattern' -- src",
      "impact": "What goes wrong because of this — be specific",
      "suggestedFix": "Brief approach, not full implementation"
    }
  ],
  "notChecked": ["Files or areas you skipped, and why"]
}
```

## Quality Bar

- Every finding MUST have `crossReference` — a second location proving the issue isn't isolated.
  For duplication: the other copy. For drift: the layer rule violated + the import. For complexity: the callers affected.
- For duplication, find EVERY copy, not just two: put the search that lists them in `scopeQuery`.
- Findings about over-engineering require proof the abstraction is never extended: check all implementations
  of the interface, check test mocks, check DI registrations, check git history for attempts to add implementations.
- **Do NOT flag patterns listed in `intentionalPatterns`.** If unsure, skip it.
- Prefer fewer high-quality findings over many shallow ones. Maximum 10 findings.


""";

    [Fact]
    public void BuildRefactoringStructuralDebtPrompt_FullOutput_MatchesSnapshot()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringStructuralDebtPrompt();

        result.Should().Be(Snapshot_BuildRefactoringStructuralDebtPrompt_FullOutput);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  13. ConsolidationPromptBuilder.BuildRefactoringCorrectnessPrompt — full output
    //      Characterization test for the issue #3537 scaffold refactoring.
    //      Note: Agent B uses the heading "## Exploration Strategy: Enumerate Then Verify" —
    //      different from Agents A and C — which this snapshot captures.
    //
    //      Output invariance note: this test was added in the same PR as the refactoring
    //      (issue #3537) and its snapshot is tautological with respect to output invariance.
    //      Cross-validation test 16 (below) verifies the suffix of this snapshot against
    //      the pre-existing partial-suffix snapshot (test 7, predating this PR).
    // ─────────────────────────────────────────────────────────────────────────

    private const string Snapshot_BuildRefactoringCorrectnessPrompt_FullOutput = """
## CRITICAL RULES — Read First

1. **Evidence over speculation.** Every finding must cite a specific file path + line number or code snippet. "This looks complex" is not a finding.
2. **Use the available tools.** Phase 0 listed them under `availableTools` in `.agent/refactoring-conventions.json` and saved the output of the project's analyzers in `.agent/refactoring-tool-output/`. Tool output is higher-confidence evidence than your own code reading. Query the listed MCP tools for data about the code you examine. Do NOT run builds or other commands that write build output: the other two agents work in this workspace at the same time. Read-only analyzers that do not build are fine, and you are allowed to install them.
3. **Declare what you did NOT check.** List files/areas you skipped due to context limits in the `notChecked` field of your output.
4. **Reasoning length scales with severity.** 1-2 sentences for low-impact observations. 4-6 sentences with full evidence chain for high-impact findings.
5. **Do NOT modify source code.** Only produce the findings output file.
6. **Out of scope:** `.agent/`, `.brain/`, `.git/`, generated code, and files ignored by git. These are pipeline scratch space or tooling, not project code. Never report findings in them.
7. **Tag evidence honestly.** Each `evidenceSources` entry names how you got the evidence:
   - `tool:<tool>:<rule-id>` — output of a compiler, linter, analyzer or MCP tool that actually ran (by you or Phase 0). Nothing else is `tool:`.
   - `grep:<pattern>` — a text search, with the pattern you searched for.
   - `usage-search:<symbol>` — a reference search, with the result (e.g. `usage-search:FooService.Bar:0-callers`).
   - `code-reading:<file>:L<line>` — your own reading of the code.
   - `hotspot:<file>` — change frequency. This says how often a file changes, not that anything is wrong with it.
# Agent B: Correctness & Hygiene Detection

You are one of three parallel analysis agents. Your focus is **correctness and hygiene** —
concrete issues that are wrong, dead, or misleading.

## Your Categories

1. **TODO/HACK/FIXME comments** — Left by previous work, indicating incomplete implementation.
   Not all TODOs are actionable — only flag ones that indicate a real gap or risk.
   Some TODOs are deferred review findings (e.g. `TODO [WARNING]`). Treat them like any other TODO:
   verify the problem still exists in the current code, and drop it if the code already handles it.

2. **Dead code & unused artifacts** — Unreferenced methods, classes, interfaces, or files.
   Includes: unused using directives beyond IDE cleanup, orphaned files from removed features,
   parameters that are never used, private methods never called.

3. **Obvious bugs** — High-confidence correctness issues ONLY. You must be certain the code is wrong:
   null dereference after a code path that doesn't guarantee non-null, off-by-one in boundary
   checks, unreachable code paths (dead branches), resource leaks (opened but never disposed),
   logic errors where conditions are always true/false, race conditions in shared mutable state.
   **Do NOT flag "potential" issues you're unsure about.** Only high-confidence bugs.

4. **Stale documentation & misleading comments** — XML doc comments describing behavior the code
   no longer exhibits; README sections referencing removed features; comments explaining "why"
   that reference conditions no longer true; parameter descriptions that don't match signatures.

## Exploration Strategy: Enumerate Then Verify

Research shows LLM agents miss absences when scanning for bad patterns.
Flip the approach: enumerate what exists, then verify each item.

**For TODOs/HACKs/FIXMEs:**
1. Search the codebase: grep/search for `TODO`, `HACK`, `FIXME`, `XXX`, `WORKAROUND`
2. For each result: read the surrounding context and assess if it indicates a real gap
3. Discard TODOs that are aspirational ("TODO: nice to have") — keep ones indicating broken/incomplete behavior

**For dead code:**
1. Check the tool output in `.agent/refactoring-tool-output/` and the MCP tools in `availableTools` for unused-code
   reports — the highest-confidence approach. Common sources: compiler warnings (CS0219, IDE0051),
   `eslint --rule no-unused-vars`, `pylint`, `deadcode`. If none covers unused code, you may install and run
   a read-only analyzer that does not build.
2. If no tools available: enumerate public types/methods in key files, then search for their usages.
   A public method with zero callers outside its own class is a dead code candidate.
3. Check git history for recently-deleted features — their support code may linger.

**For bugs:**
1. Focus on hotspot files (high churn = more likely to contain recent regressions)
2. Read error handling paths specifically — bugs hide in catch blocks and edge cases
3. Check null safety: follow nullable references through code paths and verify guards exist
4. When a bug is one instance of a pattern (the same faulty catch, guard or call in several places),
   search for every instance and put the search in `scopeQuery`. A fix for half the instances is not a fix.

**For stale docs:**
1. Read method signatures, then read their XML doc comments — do they match?
2. Check README.md for references to files/features that no longer exist
3. Check inline comments that reference specific behavior — verify the behavior still exists

## Output Format

Write findings to `.agent/refactoring-correctness-findings.json` as a JSON object:

```json
{
  "findings": [
    {
      "title": "Short descriptive title",
      "category": "todo|dead-code|bug|stale-documentation",
      "affectedFiles": ["src/path/to/File.cs"],
      "evidence": "The exact code snippet or comment text proving the issue",
      "evidenceSources": ["grep:TODO", "tool:dotnet-build:IDE0051", "usage-search:FooService.Bar:0-callers"],
      "crossReference": "For dead code: proof of zero callers. For bugs: the code path that triggers it. For stale docs: the actual behavior vs documented behavior.",
      "scopeQuery": "When the finding is one instance of a repeated pattern: the search that lists every instance, e.g. git grep -n 'pattern' -- src",
      "impact": "What goes wrong or what cognitive cost this imposes",
      "suggestedFix": "Brief approach"
    }
  ],
  "notChecked": ["Files or areas you skipped, and why"]
}
```

## Quality Bar

- **Dead code findings MUST include proof of zero usage** — either tool output or a usage search showing no callers.
  Do NOT flag code as dead without searching for references. Reflection, DI registration, and test mocks can create invisible references.
- **Bug findings MUST demonstrate a concrete failure scenario** — not "this could fail" but "when X is null at L42, L47 dereferences it without a guard."
- **TODO findings must include the surrounding context** — the comment alone is not enough. Show what's incomplete or broken.
- **Stale doc findings must show both** the documented claim AND the actual code behavior side-by-side.
- Findings sourced from deterministic tools (grep, linter, compiler warnings) are inherently higher quality.
  Tag them as described in the critical rules.
- Maximum 10 findings. Prefer bugs > dead code > stale docs > TODOs (by impact).


""";

    [Fact]
    public void BuildRefactoringCorrectnessPrompt_FullOutput_MatchesSnapshot()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringCorrectnessPrompt();

        result.Should().Be(Snapshot_BuildRefactoringCorrectnessPrompt_FullOutput);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  14. ConsolidationPromptBuilder.BuildRefactoringDesignConsistencyPrompt — full output
    //      Characterization test for the issue #3537 scaffold refactoring.
    //      Note: Agent C has two extra intro lines after the standard
    //      "You are one of three parallel analysis agents..." sentence that Agents A and B
    //      do not have — this snapshot captures that distinction.
    //
    //      Output invariance note: this test was added in the same PR as the refactoring
    //      (issue #3537) and its snapshot is tautological with respect to output invariance.
    //      Cross-validation test 17 (below) verifies the suffix of this snapshot against
    //      the pre-existing partial-suffix snapshot (test 8, predating this PR).
    // ─────────────────────────────────────────────────────────────────────────

    private const string Snapshot_BuildRefactoringDesignConsistencyPrompt_FullOutput = """
## CRITICAL RULES — Read First

1. **Evidence over speculation.** Every finding must cite a specific file path + line number or code snippet. "This looks complex" is not a finding.
2. **Use the available tools.** Phase 0 listed them under `availableTools` in `.agent/refactoring-conventions.json` and saved the output of the project's analyzers in `.agent/refactoring-tool-output/`. Tool output is higher-confidence evidence than your own code reading. Query the listed MCP tools for data about the code you examine. Do NOT run builds or other commands that write build output: the other two agents work in this workspace at the same time. Read-only analyzers that do not build are fine, and you are allowed to install them.
3. **Declare what you did NOT check.** List files/areas you skipped due to context limits in the `notChecked` field of your output.
4. **Reasoning length scales with severity.** 1-2 sentences for low-impact observations. 4-6 sentences with full evidence chain for high-impact findings.
5. **Do NOT modify source code.** Only produce the findings output file.
6. **Out of scope:** `.agent/`, `.brain/`, `.git/`, generated code, and files ignored by git. These are pipeline scratch space or tooling, not project code. Never report findings in them.
7. **Tag evidence honestly.** Each `evidenceSources` entry names how you got the evidence:
   - `tool:<tool>:<rule-id>` — output of a compiler, linter, analyzer or MCP tool that actually ran (by you or Phase 0). Nothing else is `tool:`.
   - `grep:<pattern>` — a text search, with the pattern you searched for.
   - `usage-search:<symbol>` — a reference search, with the result (e.g. `usage-search:FooService.Bar:0-callers`).
   - `code-reading:<file>:L<line>` — your own reading of the code.
   - `hotspot:<file>` — change frequency. This says how often a file changes, not that anything is wrong with it.
# Agent C: Design Consistency Detection

You are one of three parallel analysis agents. Your focus is **design consistency** —
patterns where naming, typing, or API shape deviates from the project's own conventions.

**This agent depends heavily on `.agent/refactoring-conventions.json`.** Read it first.
Your job is to find deviations from the project's OWN standards, not generic best practices.

## Your Categories

1. **Naming inconsistencies** — Classes, methods, or variables that don't follow the project's naming conventions.
   Use `namingConventions` from conventions.json as your reference. Examples:
   - Service classes without the expected suffix (e.g., `FooHandler` when convention is `FooService`)
   - Interfaces that don't follow the prefix/suffix pattern
   - Files whose names don't match their primary class
   - Methods using different verb patterns than the rest of the codebase (e.g., `Fetch` vs `Get` vs `Load`)
   - Inconsistent casing in specific contexts (event names, configuration keys, JSON properties)

2. **Primitive obsession** — Using strings, ints, or raw types to represent domain concepts.
   Look for:
   - String parameters representing structured data (emails, URLs, IDs, file paths) without validation
   - Magic numbers/strings without named constants — especially repeated across multiple files
   - Repeated validation logic for the same concept in multiple call sites
   - Method signatures with multiple same-typed parameters that could be confused (e.g., `void Move(string from, string to)`)
   - Enums that should be polymorphic types (switch statements over the same enum in many places)

## Exploration Strategy

**For naming inconsistencies:**
1. Read `namingConventions` from conventions.json — this IS the truth
2. Enumerate class/interface names across projects (list files, read declarations)
3. For each naming convention rule: verify compliance across a representative sample
4. Focus on PUBLIC API surface — internal inconsistencies matter less
5. Only flag patterns that appear more than once — a single oddly-named class might be intentional

**For primitive obsession:**
1. Look at method signatures in service interfaces — these define the API contracts
2. Search for repeated string-typed parameters with the same name across different methods
   (e.g., `string repositoryUrl` appearing in 5+ method signatures = candidate for a value type)
3. Search for magic strings/numbers: look for string literals and numeric constants used in
   conditional logic. If the same literal appears in 3+ places, it should be a constant or enum.
4. Check switch statements over enums — if the same enum is switched over in 4+ locations,
   it may be a candidate for polymorphism (but check `intentionalPatterns` first).

## Output Format

Write findings to `.agent/refactoring-design-findings.json` as a JSON object:

```json
{
  "findings": [
    {
      "title": "Short descriptive title",
      "category": "naming-inconsistency|primitive-obsession",
      "affectedFiles": ["src/path/to/File.cs"],
      "evidence": "The specific naming deviation or primitive usage with concrete examples",
      "evidenceSources": ["grep:string repositoryUrl", "usage-search:repositoryUrl:5-signatures"],
      "crossReference": "For naming: the convention rule violated + examples of correct naming elsewhere. For primitives: multiple locations using the same raw type for the same concept.",
      "scopeQuery": "When the finding is one instance of a repeated pattern: the search that lists every instance, e.g. git grep -n 'pattern' -- src",
      "impact": "Cognitive cost, confusion risk, or bug risk from the inconsistency",
      "suggestedFix": "Brief approach — rename to X, introduce value type Y, extract constant Z"
    }
  ],
  "notChecked": ["Files or areas you skipped, and why"]
}
```

## Quality Bar

- **Naming findings require a convention rule reference.** "This name seems odd" is not a finding.
  "Convention says services end with 'Service' but `FooHandler` doesn't follow this" IS a finding.
- **Primitive obsession findings require 3+ occurrences.** A single string parameter is not primitive obsession.
  The same concept passed as raw string through 3+ call sites IS primitive obsession.
- Every naming and primitive-obsession finding needs a `scopeQuery` that lists all occurrences, so the fix is complete.
- **Do NOT flag naming in test projects** unless conventions.json explicitly covers test naming.
- **Do NOT flag names that match `intentionalPatterns`** from conventions.json.
- This agent has the highest false-positive risk. Be conservative. Maximum 8 findings.


""";

    [Fact]
    public void BuildRefactoringDesignConsistencyPrompt_FullOutput_MatchesSnapshot()
    {
        var result = ConsolidationPromptBuilder.BuildRefactoringDesignConsistencyPrompt();

        result.Should().Be(Snapshot_BuildRefactoringDesignConsistencyPrompt_FullOutput);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  15–17. Cross-validation: full-output snapshot suffix matches pre-existing
    //         partial-suffix snapshot (tests 6–8).
    //
    //  WHY THESE EXIST (CRITICAL fix for issue #3537):
    //  Tests 12–14 were added in the same PR as the refactoring. Their snapshot
    //  constants were captured from the post-refactor code, making them tautological
    //  with respect to the AC3 invariance claim ("output is unchanged"). A snapshot
    //  written from incorrect post-refactor output would make tests 12–14 pass while
    //  proving nothing about correctness relative to the pre-refactor baseline.
    //
    //  Tests 6–8 (partial-suffix snapshots) were NOT modified by issue #3537 and
    //  predate this refactoring. They independently pin the "## Output Format" +
    //  "## Quality Bar" tail of each prompt. Tests 15–17 cross-check that the tail of
    //  each full-output snapshot constant (tests 12–14) equals the corresponding
    //  pre-existing partial-suffix constant (tests 6–8). Because test 15/16/17
    //  compares two independently-sourced constants, a mismatch between them would
    //  surface immediately — catching any case where the full-output snapshot was
    //  captured with a wrong suffix.
    //
    //  Together with the structural equivalence of the pre- and post-refactor intro
    //  sections (verifiable by reading the diff: the same `sb.AppendLine` calls appear
    //  in the same order in both the original and the `AppendSubAgentScaffold` path),
    //  these cross-validation tests provide the strongest locally-verifiable evidence
    //  that the output is correct.
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildRefactoringStructuralDebtPrompt_FullOutputSnapshot_SuffixMatchesPartialSnapshot()
    {
        // Tests 6 and 12 were captured independently. If the suffix of the full-output
        // snapshot matches the pre-existing partial-suffix snapshot, the full snapshot's
        // tail is verified against a reference that predates this refactoring.
        var outputFormatIdx = Snapshot_BuildRefactoringStructuralDebtPrompt_FullOutput.IndexOf(
            "## Output Format", StringComparison.Ordinal);

        outputFormatIdx.Should().BeGreaterThan(0,
            "the full-output snapshot must contain '## Output Format'");

        var suffix = Snapshot_BuildRefactoringStructuralDebtPrompt_FullOutput[outputFormatIdx..];

        suffix.Should().Be(Snapshot_BuildRefactoringStructuralDebtPrompt_OutputFormatSection,
            "the tail of the full-output snapshot (test 12) must equal the pre-existing " +
            "partial-suffix snapshot (test 6) that predates the issue #3537 refactoring");
    }

    [Fact]
    public void BuildRefactoringCorrectnessPrompt_FullOutputSnapshot_SuffixMatchesPartialSnapshot()
    {
        // Tests 7 and 13 were captured independently. Same cross-validation as test 15 above.
        var outputFormatIdx = Snapshot_BuildRefactoringCorrectnessPrompt_FullOutput.IndexOf(
            "## Output Format", StringComparison.Ordinal);

        outputFormatIdx.Should().BeGreaterThan(0,
            "the full-output snapshot must contain '## Output Format'");

        var suffix = Snapshot_BuildRefactoringCorrectnessPrompt_FullOutput[outputFormatIdx..];

        suffix.Should().Be(Snapshot_BuildRefactoringCorrectnessPrompt_OutputFormatSection,
            "the tail of the full-output snapshot (test 13) must equal the pre-existing " +
            "partial-suffix snapshot (test 7) that predates the issue #3537 refactoring");
    }

    [Fact]
    public void BuildRefactoringDesignConsistencyPrompt_FullOutputSnapshot_SuffixMatchesPartialSnapshot()
    {
        // Tests 8 and 14 were captured independently. Same cross-validation as test 15 above.
        var outputFormatIdx = Snapshot_BuildRefactoringDesignConsistencyPrompt_FullOutput.IndexOf(
            "## Output Format", StringComparison.Ordinal);

        outputFormatIdx.Should().BeGreaterThan(0,
            "the full-output snapshot must contain '## Output Format'");

        var suffix = Snapshot_BuildRefactoringDesignConsistencyPrompt_FullOutput[outputFormatIdx..];

        suffix.Should().Be(Snapshot_BuildRefactoringDesignConsistencyPrompt_OutputFormatSection,
            "the tail of the full-output snapshot (test 14) must equal the pre-existing " +
            "partial-suffix snapshot (test 8) that predates the issue #3537 refactoring");
    }
}
