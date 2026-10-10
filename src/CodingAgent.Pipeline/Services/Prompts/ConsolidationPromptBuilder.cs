using System.Text;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services.Prompts;

/// <summary>
/// Builds prompts for the three consolidation loop types:
/// brain consolidation, refactoring detection, and harness suggestions.
/// </summary>
public static partial class ConsolidationPromptBuilder
{
    /// <summary>
    /// Builds the 4-phase brain consolidation prompt.
    /// Includes last consolidation timestamp for recency context.
    /// </summary>
    public static string BuildBrainConsolidationPrompt(DateTime? lastConsolidationUtc)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Brain Knowledge Repository Consolidation");
        sb.AppendLine();
        sb.AppendLine("You are performing a consolidation pass on the `.brain/` knowledge repository.");
        sb.AppendLine("Your goal is to keep the knowledge base concise, accurate, and free of contradictions.");
        sb.AppendLine();

        // Recency context — conditional content, so not using AppendSection
        sb.AppendLine("## Context");
        sb.AppendLine();
        if (lastConsolidationUtc.HasValue)
        {
            sb.AppendLine($"Last successful consolidation: **{lastConsolidationUtc.Value:yyyy-MM-dd HH:mm:ss} UTC**");
            sb.AppendLine("Focus on changes and additions since that timestamp.");
        }
        else
        {
            sb.AppendLine("**No prior consolidation has occurred.** This is the first consolidation pass.");
            sb.AppendLine("Review the entire knowledge base from scratch.");
        }

        sb.AppendLine();

        // Phase 1: Orient
        PromptBuilder.AppendSection(sb, "## Phase 1: Orient", s =>
        {
            s.AppendLine("Scan all files in the `.brain/` directory recursively. Build a mental inventory of:");
            s.AppendLine("- All knowledge files and their topics");
            s.AppendLine("- The directory structure and organization");
            s.AppendLine("- File sizes and last-modified indicators");
            s.AppendLine("- Any README or index files that describe the structure");
            s.AppendLine();
            s.AppendLine("Do NOT make changes during this phase. Only observe and catalog.");
        });

        // Phase 2: Gather Signal
        PromptBuilder.AppendSection(sb, "## Phase 2: Gather Signal", s =>
        {
            s.AppendLine("Read recent session logs and run summaries to identify:");
            s.AppendLine("- New lessons learned that may duplicate existing entries");
            s.AppendLine("- Technology decisions that have been superseded");
            s.AppendLine("- Relative date references (e.g., \"yesterday\", \"last week\") that should be absolute");
            s.AppendLine("- Entries that reference removed or renamed components");
            s.AppendLine("- Contradictions between different knowledge files");
            s.AppendLine("- **Citation data:** Which entries were referenced in session logs and how (helpful, not applicable, outdated)");
            s.AppendLine();
            s.AppendLine("### Citation Aggregation");
            s.AppendLine();
            s.AppendLine("Session logs contain a `## Brain Entries Referenced` section listing which knowledge entries");
            s.AppendLine("were consulted and their usefulness (`used, helpful` | `read, not applicable` | `used, outdated`).");
            s.AppendLine("Aggregate this data across all session logs since the last consolidation:");
            s.AppendLine("- Count how many sessions cite each entry as **helpful**");
            s.AppendLine("- Note entries cited as **outdated** by any session (candidates for correction)");
            s.AppendLine("- Identify entries in `general/`, `technology/`, and `projects/` that are **never cited** in any session log");
            s.AppendLine();
            s.AppendLine("Use this citation data to inform decisions in Phase 3 (Consolidate) and Phase 4 (Prune).");
        });

        // Phase 2.5: Research & Verify
        PromptBuilder.AppendSection(sb, "## Phase 2.5: Research & Verify", s =>
        {
            s.AppendLine("For entries that reference specific tools, libraries, versions, or external services:");
            s.AppendLine("- **Verify currency:** Check whether referenced library versions are still the latest (e.g., is the noted NuGet package version still current?)");
            s.AppendLine("- **Check for better alternatives:** If a workaround or pattern was documented because a tool lacked a feature, verify whether that feature has since been added");
            s.AppendLine("- **Validate links and references:** If entries reference external documentation URLs or API endpoints, verify they are still valid");
            s.AppendLine("- **Update outdated information:** If you find newer/better approaches to documented problems, update the entry with the current best practice and note the change");
            s.AppendLine();
            s.AppendLine("Use web search to verify information when uncertain. Only update entries where you have high confidence the information has changed — do not speculate.");
        });

        // Phase 3: Consolidate
        PromptBuilder.AppendSection(sb, "## Phase 3: Consolidate", s =>
        {
            s.AppendLine("Apply the following transformations:");
            s.AppendLine("- **Merge duplicates:** Combine entries that describe the same concept into a single, authoritative entry");
            s.AppendLine("- **Resolve contradictions:** When two entries conflict, keep the more recent or more specific one. Add a note about what was superseded if relevant");
            s.AppendLine("- **Convert relative dates:** Replace relative time references with absolute dates (e.g., \"yesterday\" → \"2026-01-15\")");
            s.AppendLine("- **Update references:** Fix references to renamed or moved components");
            s.AppendLine("- **Improve organization:** Move misplaced entries to their correct section or file");
        });

        // Phase 4: Prune
        PromptBuilder.AppendSection(sb, "## Phase 4: Prune", s =>
        {
            s.AppendLine("Remove content that no longer provides value:");
            s.AppendLine("- Stale entries about components that no longer exist");
            s.AppendLine("- Session logs older than 30 days that have already been distilled into lessons");
            s.AppendLine("- Redundant entries that were merged in Phase 3");
            s.AppendLine("- Empty or placeholder files");
            s.AppendLine();
            s.AppendLine("Use citation data from Phase 2 to inform pruning decisions:");
            s.AppendLine("- **High-value (keep):** Entries cited as helpful in 3+ sessions — these are proven useful");
            s.AppendLine("- **Outdated (correct or remove):** Entries cited as outdated by any session — verify and either update or mark ⚠️ OUTDATED");
            s.AppendLine("- **Uncited + stale (prune candidates):** Entries never cited in any session log AND older than their verification window");
            s.AppendLine("- **Recently written (keep):** Entries written since the last consolidation should be kept regardless of citation count — they haven't had time to be cited yet");
            s.AppendLine();
            s.AppendLine("When pruning an entry, check whether it has only `[experience]` sources and was never verified.");
            s.AppendLine("Entries with `[docs]` sources are more likely to be correct even if uncited — prefer re-verification over pruning.");
            s.AppendLine();
            s.AppendLine("Keep the index files (README.md) concise and up-to-date with the current structure.");
        });

        // Phase 5: Project SKILL.md generation
        PromptBuilder.AppendSection(sb, "## Phase 5: Generate Project SKILL.md", s =>
        {
            s.AppendLine("For each project directory under `.brain/projects/`, regenerate a `SKILL.md` file.");
            s.AppendLine("This file is a distilled, single-document summary that agents receive as pre-loaded context");
            s.AppendLine("via subagent retrieval — it should be the most useful file in the project folder.");
            s.AppendLine();
            s.AppendLine("**Regenerate from scratch each time** (do not incrementally edit the existing SKILL.md).");
            s.AppendLine("Cap content at ~1500 words. Structure it as:");
            s.AppendLine();
            s.AppendLine("```markdown");
            s.AppendLine("# Project: {project-name}");
            s.AppendLine();
            s.AppendLine("## Architecture");
            s.AppendLine("{Tech stack, key project structure, main components and their roles}");
            s.AppendLine();
            s.AppendLine("## Conventions");
            s.AppendLine("{Coding standards, naming patterns, preferred libraries, serialization choices}");
            s.AppendLine();
            s.AppendLine("## Known Pitfalls");
            s.AppendLine("{Common mistakes from lessons-learned, gotchas that cause build/test failures}");
            s.AppendLine();
            s.AppendLine("## Testing Patterns");
            s.AppendLine("{How tests are structured, commands to run, quarantine rules, CI quirks}");
            s.AppendLine();
            s.AppendLine("## Key Decisions");
            s.AppendLine("{Important architectural decisions and their rationale}");
            s.AppendLine("```");
            s.AppendLine();
            s.AppendLine("Source content from the project's brain entries, technology files, general lessons,");
            s.AppendLine("and session logs. Only include information that is current and verified.");
            s.AppendLine("If a project folder has very little accumulated knowledge, produce a shorter SKILL.md");
            s.AppendLine("with just the sections that have content — do not pad with generic advice.");
        });

        // Output expectations
        // NOTE (issue #3534): This is the final section of BuildBrainConsolidationPrompt. AppendSection appends a trailing
        // blank line that the original inline block did not emit before return. The new snapshot
        // Snapshot_BuildBrainConsolidationPrompt_NullTimestamp locks in the +\n+\n ending from scratch (no pre-refactor
        // baseline exists for this method), so it does not prove AC3 ("unchanged output") for the null-timestamp path.
        // The non-null timestamp path (lastConsolidationUtc.HasValue) has no full-prompt snapshot at all — a spacing
        // regression there would go undetected. Add a deterministic snapshot with a fixed DateTime value.
        // (Review finding: correctness agent, ConsolidationPromptBuilder.cs)
        PromptBuilder.AppendSection(sb, "## Output", s =>
        {
            s.AppendLine("Make all changes directly to the files. After completion, provide a brief summary of what was done:");
            s.AppendLine("- Number of files modified");
            s.AppendLine("- Number of entries merged");
            s.AppendLine("- Number of contradictions resolved");
            s.AppendLine("- Number of entries pruned");
            s.AppendLine("- Number of SKILL.md files generated/updated");
        });

        return sb.ToString();
    }

    /// <summary>
    /// Builds a prompt section listing open issues to prevent duplicate proposals.
    /// Returns empty string if both lists are empty.
    /// </summary>
    public static string BuildOpenIssueContext(
        IReadOnlyList<IssueSummary> refactoringIssues,
        IReadOnlyList<IssueSummary> otherIssues)
    {
        ArgumentNullException.ThrowIfNull(refactoringIssues);
        ArgumentNullException.ThrowIfNull(otherIssues);

        if (refactoringIssues.Count == 0 && otherIssues.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("## Existing Open Issues — Do Not Duplicate");
        sb.AppendLine();

        if (refactoringIssues.Count > 0)
        {
            sb.AppendLine("### Open Refactoring Scan Issues (still pending)");
            foreach (var issue in refactoringIssues)
                sb.AppendLine($"- #{issue.Identifier} \"{issue.Title}\"");
            sb.AppendLine();
        }

        if (otherIssues.Count > 0)
        {
            sb.AppendLine("### Other Open Issues (may overlap)");
            foreach (var issue in otherIssues)
                sb.AppendLine($"- #{issue.Identifier} \"{issue.Title}\"");
            sb.AppendLine();
        }

        sb.AppendLine("Do NOT propose refactoring that overlaps with any issue listed above.");
        sb.AppendLine("If you identify an opportunity that partially overlaps, note the related issue number in your rationale and explain why your proposal is distinct.");
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// Builds the harness suggestion analysis prompt.
    /// Instructs agent to identify top 3-5 improvements from feedback data.
    /// </summary>
    public static string BuildHarnessSuggestionPrompt(int feedbackCount, decimal successRate)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Harness Improvement Analysis");
        sb.AppendLine();
        sb.AppendLine("You are analyzing accumulated pipeline run feedback to identify recurring patterns and suggest improvements.");
        sb.AppendLine();

        // Context
        PromptBuilder.AppendSection(sb, "## Feedback Context", s =>
        {
            s.AppendLine($"- **Total runs with feedback:** {feedbackCount}");
            s.AppendLine($"- **Overall success rate:** {successRate:F1}%");
            s.AppendLine();
            s.AppendLine("The feedback data file (`feedback-data.json`) in this workspace contains the raw RunFeedback entries from pipeline runs.");
            s.AppendLine("Each entry includes harness feedback (pipeline/tool issues) and optionally issue feedback (issue/repo quality problems).");
        });

        // Instructions
        PromptBuilder.AppendSection(sb, "## Analysis Instructions", s =>
        {
            s.AppendLine("1. Read the feedback data file completely");
            s.AppendLine("2. Identify recurring patterns across multiple runs — look for repeated categories, similar stuck reasons, and common missing capabilities");
            s.AppendLine("3. Rank patterns by **frequency** (how many runs mention it) and **impact** (how much it affects success rate)");
            s.AppendLine("4. Produce the **top 3-5 improvement suggestions** that would have the highest positive impact");
        });

        // Quality requirements
        PromptBuilder.AppendSection(sb, "## Suggestion Quality Requirements", s =>
        {
            s.AppendLine("Each suggestion MUST be:");
            s.AppendLine("- **Concrete and actionable** — specify exactly what to change (e.g., \"Add file X to the initial context provided to the agent\" not \"Provide more context\")");
            s.AppendLine("- **Grounded in evidence** — reference at least 3 specific feedback entries (by their category or stuckReason text) that motivate the suggestion");
            s.AppendLine("- **Scoped to one change** — each suggestion addresses one improvement, not a bundle of changes");
            s.AppendLine();
            s.AppendLine("Do NOT produce abstract recommendations like \"improve error handling\" or \"add more tests\".");
            s.AppendLine("Every suggestion must reference specific feedback patterns and propose a specific change.");
        });

        // Output format
        PromptBuilder.AppendOutputFormatHeading(sb);
        sb.AppendLine();
        sb.AppendLine("Produce your analysis as a JSON object with the following structure:");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine($"  \"generatedAtUtc\": \"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}\",");
        sb.AppendLine($"  \"basedOnRunCount\": {feedbackCount},");
        sb.AppendLine($"  \"successRate\": {successRate:F1},");
        sb.AppendLine("  \"suggestions\": [");
        sb.AppendLine("    {");
        sb.AppendLine("      \"text\": \"The concrete suggestion text — what to change and how\",");
        sb.AppendLine("      \"rationale\": \"Why this would help, referencing specific feedback patterns\",");
        sb.AppendLine("      \"frequency\": 5");
        sb.AppendLine("    }");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("The `frequency` field indicates how many runs contributed to this observation.");
        sb.AppendLine("Order suggestions by impact (highest first).");

        return sb.ToString();
    }

    /// <summary>
    /// Formats the brain consolidation summary string with all metrics.
    /// </summary>
    public static string FormatBrainConsolidationSummary(
        int filesModified,
        int entriesMerged,
        int contradictionsResolved,
        int entriesPruned)
    {
        return $"Files modified: {filesModified}, Entries merged: {entriesMerged}, Contradictions resolved: {contradictionsResolved}, Entries pruned: {entriesPruned}";
    }

    /// <summary>
    /// Review prompt for refactoring proposals.
    /// Instructs discriminator to read .agent/refactoring-proposals.json and write
    /// findings to .agent/refactoring-review.md. Strengthened with research-backed
    /// criteria: evidence corroboration, actual blast radius, failure mode commitment.
    /// </summary>
    public static string BuildRefactoringReviewPrompt()
    {
        return BuildAdversarialReviewPrompt(new AdversarialReviewPromptSpec
        {
            Title = "Refactoring Proposals Review",
            SubjectNoun = "refactoring proposals",
            IntroScope = "the proposals file, the analysis report, the sub-agent findings files, and the actual codebase",
            InputInstruction = $"""
            Read the proposals file at `{AgentWorkspacePaths.RefactoringProposalsFilePath}` and the analysis report at `{AgentWorkspacePaths.RefactoringAnalysisFilePath}`.
            Also read the sub-agent findings for cross-reference:
            - `{AgentWorkspacePaths.RefactoringStructuralFindingsFilePath}` (Agent A)
            - `{AgentWorkspacePaths.RefactoringCorrectnessFindingsFilePath}` (Agent B)
            - `{AgentWorkspacePaths.RefactoringDesignFindingsFilePath}` (Agent C)
            - `{AgentWorkspacePaths.RefactoringConventionsFilePath}` (project conventions)
            If `{AgentWorkspacePaths.RefactoringIssueContextFilePath}` exists, read it: it lists the open issues and past proposals the new proposals must not duplicate.
            """,
            OutputPath = AgentWorkspacePaths.RefactoringReviewFilePath,
            EvaluationBullets =
            [
                "Non-existent `affectedFiles` paths — verify the referenced files actually exist in the repository",
                "**Evidence corroboration failure** — proposals with only a single `evidenceSources` entry, especially `code-reading:` only. `hotspot:` is a priority signal and does not corroborate anything. Sources tagged `tool:` that name no compiler, linter, analyzer or MCP tool are mislabeled searches or reads. Single-source proposals should be flagged [WARNING]",
                "**Evidence not shown** — `evidence` is missing, paraphrased, or does not match the code at the cited lines. Flag [WARNING]",
                "**Incomplete scope** — the proposal fixes some instances of a repeated pattern but not all. Run its `scopeQuery` (or your own search when it has none): every match must be in `affectedFiles` or excluded by name in `description`. Flag [WARNING] and list the missing instances",
                "**Actual blast radius understated** — count the REAL affected files: not just `affectedFiles` but also their test files, their consumers (files importing them), and shared configuration. If the true blast radius exceeds 30 files, flag [CRITICAL]",
                "**Failure mode not committed** — rationales that say \"could lead to issues\" or \"might cause confusion\" without stating WHAT specifically goes wrong. A valid rationale commits: \"X causes Y because Z.\" Hedged language indicates low confidence — flag [WARNING]",
                "**Bug without a reproduction** — a `bug` proposal whose rationale has no concrete failure scenario, or whose acceptance criteria have no test that reproduces it. Flag [WARNING]",
                "**Convention contradiction** — proposals that flag patterns listed in `intentionalPatterns` or `knownDebt` from conventions.json. The aggregation step should have caught these, but verify. Flag [CRITICAL] if found",
                "Bundled concerns that should be separate proposals",
                "**Scope exceeding single-agent capacity** — proposals touching more than ~30 files (source + test), spanning multiple serialization boundaries, or requiring coordinated breaking changes across projects. Flag [CRITICAL] with a suggestion to split",
                "**Overlap with existing issues** — check if any proposal substantially duplicates an open issue or re-proposes a past proposal listed in the issue context file. Flag [WARNING] if overlap detected",
                "**Unverifiable acceptance criteria** — criteria requiring runtime execution, benchmarks, manual testing, or subjective judgment. The review agent can only verify from diff + test results. Flag [WARNING]",
                "**Implementation-prescriptive acceptance criteria** — criteria that dictate specific file names, class names, or implementation patterns rather than observable post-conditions. These block valid alternative approaches. Flag [WARNING]",
            ],
            EvaluationSubject = "each proposal",
            SubjectShortName = "proposals",
            DoNotModifyClause = "Do NOT modify source files, configuration files, or the proposals file. Only read the input and write the review findings file."
        });
    }

    /// <summary>
    /// Review prompt for brain consolidation changes.
    /// Instructs discriminator to read .agent/brain-consolidation-diff.md and write
    /// findings to .agent/brain-consolidation-review.md.
    /// </summary>
    public static string BuildBrainConsolidationReviewPrompt()
    {
        return BuildAdversarialReviewPrompt(new AdversarialReviewPromptSpec
        {
            Title = "Brain Consolidation Review",
            SubjectNoun = "brain consolidation changes",
            IntroScope = "the diff summary file and the actual `.brain/` files in the workspace",
            InputInstruction = $"Read the diff summary file at `{AgentWorkspacePaths.BrainConsolidationDiffFilePath}` from the workspace. **Also spot-check the `.brain/` files directly** to verify claims — cross-reference at least 3 changes against the actual file state.",
            OutputPath = AgentWorkspacePaths.BrainConsolidationReviewFilePath,
            EvaluationBullets =
            [
                "Incorrectly removed valuable entries that should have been kept",
                "Merged entries that lost important information in the process",
                "Contradictions resolved by keeping the wrong version",
                "Inaccurate factual updates",
                "New contradictions introduced by the consolidation itself",
                "**Unverifiable claims** — diff entries that are vague, lack quotes, or cannot be confirmed by reading the actual `.brain/` files. Flag [WARNING] if the diff reads like a generic summary rather than a specific changelog",
            ],
            EvaluationSubject = "the consolidation changes",
            SubjectShortName = "consolidation changes",
            DoNotModifyClause = "Do NOT modify source files, configuration files, or the `.brain/` files being reviewed. Only read the input and write the review findings file."
        });
    }

    /// <summary>
    /// Review prompt for harness suggestions.
    /// Instructs discriminator to read .agent/harness-suggestions-output.json and
    /// feedback-data.json, then write findings to .agent/harness-suggestions-review.md.
    /// </summary>
    public static string BuildHarnessSuggestionsReviewPrompt()
    {
        return BuildAdversarialReviewPrompt(new AdversarialReviewPromptSpec
        {
            Title = "Harness Suggestions Review",
            SubjectNoun = "harness improvement suggestions",
            IntroScope = "the suggestions file and the original feedback data",
            InputInstruction = $"Read the suggestions file at `{AgentWorkspacePaths.HarnessSuggestionsOutputFilePath}` from the workspace.\nAlso read `feedback-data.json` from the workspace root to cross-reference suggestions against actual feedback data.",
            OutputPath = AgentWorkspacePaths.HarnessSuggestionsReviewFilePath,
            EvaluationBullets =
            [
                "Suggestions not grounded in specific feedback patterns from the data",
                "Abstract or non-actionable suggestions that lack specificity",
                "Implausible frequency counts that don't match the feedback data",
                "Bundled concerns that should be separate suggestions",
                "Rationales lacking specific evidence from feedback entries",
            ],
            EvaluationSubject = "the suggestions",
            SubjectShortName = "suggestions",
            DoNotModifyClause = "Do NOT modify source files, configuration files, or the suggestions file being reviewed. Only read the input files and write the review findings file."
        });
    }

    /// <summary>
    /// Refinement prompt for refactoring proposals.
    /// Instructs generator to read .agent/refactoring-review.md and update
    /// .agent/refactoring-proposals.json accordingly.
    /// </summary>
    public static string BuildRefactoringRefinementPrompt()
    {
        return BuildRefinementPrompt(
            "Refactoring Proposals Refinement",
            "refactoring proposals",
            AgentWorkspacePaths.RefactoringReviewFilePath,
            "updating `" + AgentWorkspacePaths.RefactoringProposalsFilePath + "`",
            [
                "You may remove proposals that the reviewer correctly identified as invalid",
                "You may reduce the proposal count if warranted",
                "Do not add new proposals — only refine or remove existing ones",
                "When the reviewer lists missing instances of a pattern, add them to `affectedFiles` and fix `scopeQuery` — or remove the proposal if the full scope exceeds ~30 files",
                "Keep each `category` value exactly as the schema lists it",
            ]);
    }

    /// <summary>
    /// Refinement prompt for brain consolidation.
    /// Instructs generator to read .agent/brain-consolidation-review.md and revise
    /// its .brain/ modifications accordingly.
    /// </summary>
    public static string BuildBrainConsolidationRefinementPrompt()
    {
        return BuildRefinementPrompt(
            "Brain Consolidation Refinement",
            "brain consolidation changes",
            AgentWorkspacePaths.BrainConsolidationReviewFilePath,
            "revising the `.brain/` files",
            [
                "Restore incorrectly removed entries",
                "Fix incorrect merges",
                "Correct factual errors",
                $"Update `{AgentWorkspacePaths.BrainConsolidationDiffFilePath}` with a note about what was revised",
            ]);
    }

    /// <summary>
    /// Refinement prompt for harness suggestions.
    /// Instructs generator to read .agent/harness-suggestions-review.md and update
    /// .agent/harness-suggestions-output.json accordingly.
    /// </summary>
    public static string BuildHarnessSuggestionsRefinementPrompt()
    {
        return BuildRefinementPrompt(
            "Harness Suggestions Refinement",
            "harness suggestions",
            AgentWorkspacePaths.HarnessSuggestionsReviewFilePath,
            "updating `" + AgentWorkspacePaths.HarnessSuggestionsOutputFilePath + "`",
            [
                "Make suggestions more concrete, add specific evidence references, correct frequency counts",
                "You may remove suggestions that the reviewer correctly identified as unfounded",
            ]);
    }

    /// <summary>
    /// The texts that vary between the adversarial review prompts built by
    /// <see cref="BuildAdversarialReviewPrompt"/>.
    /// </summary>
    private sealed record AdversarialReviewPromptSpec
    {
        public required string Title { get; init; }
        public required string SubjectNoun { get; init; }
        public required string IntroScope { get; init; }
        public required string InputInstruction { get; init; }
        public required string OutputPath { get; init; }
        public required string[] EvaluationBullets { get; init; }
        public required string EvaluationSubject { get; init; }
        public required string SubjectShortName { get; init; }
        public required string DoNotModifyClause { get; init; }
    }

    private static string BuildAdversarialReviewPrompt(AdversarialReviewPromptSpec spec)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# {spec.Title}");
        sb.AppendLine();
        sb.AppendLine($"You are an independent reviewer evaluating {spec.SubjectNoun} produced by another agent.");
        sb.AppendLine($"Your review must be based solely on {spec.IntroScope} — you have no shared context with the generator.");
        sb.AppendLine();

        sb.AppendLine("## Input");
        sb.AppendLine();
        sb.AppendLine(spec.InputInstruction);
        sb.AppendLine();

        sb.AppendLine("## Evaluation Guidance");
        sb.AppendLine();
        sb.AppendLine($"Evaluate {spec.EvaluationSubject} holistically. Areas to consider include (but are not limited to):");
        foreach (var bullet in spec.EvaluationBullets)
        {
            sb.AppendLine($"- {bullet}");
        }
        sb.AppendLine();
        sb.AppendLine("You are not limited to this checklist. Flag any quality issue you identify.");
        sb.AppendLine();

        sb.AppendLine("## Output");
        sb.AppendLine();
        sb.AppendLine($"Write your findings to `{spec.OutputPath}` using the following severity markers:");
        sb.AppendLine();
        sb.AppendLine("- `[CRITICAL]` — The output is wrong or will cause downstream failures");
        sb.AppendLine("- `[WARNING]` — The output is incomplete but not incorrect");
        sb.AppendLine("- `[SUGGESTION]` — Optional improvement, not required");
        sb.AppendLine();
        sb.AppendLine("Only `[CRITICAL]` and `[WARNING]` findings will trigger a refinement pass. `[SUGGESTION]` findings are informational only.");
        sb.AppendLine();

        sb.AppendLine("## Important Rules");
        sb.AppendLine();
        sb.AppendLine($"- If the {spec.SubjectShortName} are thorough and correct, state that explicitly (e.g., \"No issues found\"). Do NOT invent findings.");
        sb.AppendLine("- When stating no issues were found, do NOT echo severity marker syntax. Write \"No issues found\" — not \"No [CRITICAL] issues found\".");
        sb.AppendLine($"- {PromptBuilder.FindingLineRule}");
        sb.AppendLine($"- {spec.DoNotModifyClause}");

        return sb.ToString();
    }

    private static string BuildRefinementPrompt(
        string title,
        string subjectNoun,
        string reviewPath,
        string addressAction,
        string[] guidelines)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# {title}");
        sb.AppendLine();
        sb.AppendLine($"A review of your {subjectNoun} has been completed.");
        sb.AppendLine();
        sb.AppendLine($"Read the review findings at `{reviewPath}`.");
        sb.AppendLine();
        sb.AppendLine($"Address all `[CRITICAL]` and `[WARNING]` findings by {addressAction}.");
        sb.AppendLine();
        sb.AppendLine("Guidelines:");
        foreach (var guideline in guidelines)
        {
            sb.AppendLine($"- {guideline}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Prompt instructing the generator to produce a diff summary after brain consolidation.
    /// Sent with UseResume=true to the generator's session.
    /// </summary>
    public static string BuildBrainConsolidationDiffPrompt()
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Brain Consolidation Diff Summary");
        sb.AppendLine();
        sb.AppendLine("You have completed your brain consolidation modifications.");
        sb.AppendLine();
        sb.AppendLine($"Produce a summary of all changes at `{AgentWorkspacePaths.BrainConsolidationDiffFilePath}`.");
        sb.AppendLine();
        sb.AppendLine("For each change, provide concrete evidence:");
        sb.AppendLine("- **Merges:** Quote the original entries (first 2–3 lines each) and the merged result");
        sb.AppendLine("- **Prunes:** Quote what was removed (first 2–3 lines) and cite why (uncited, stale, redundant)");
        sb.AppendLine("- **Factual updates:** State the old claim, the new claim, and the verification source (URL or tool used)");
        sb.AppendLine("- **Files created/deleted:** Full path and one-line purpose");
        sb.AppendLine("- **SKILL.md regenerations:** List which project SKILL.md files were regenerated");
        sb.AppendLine();
        sb.AppendLine("Do NOT summarize vaguely (e.g., \"cleaned up several entries\"). Every modification must be individually accounted for.");
        sb.AppendLine();
        sb.AppendLine("This summary will be reviewed by an independent agent who will spot-check the actual `.brain/` files — be thorough and specific.");
        sb.AppendLine();
        sb.AppendLine("Do NOT make additional modifications to `.brain/` files in this step.");

        return sb.ToString();
    }

    /// <summary>
    /// Builds a prompt section summarizing past refactoring proposal outcomes.
    /// Categorizes closed issues as implemented (agent:done) or rejected (agent:wont-do/agent:cancelled).
    /// Issues without agent labels are excluded. <paramref name="implementerFeedback"/> maps an issue
    /// identifier to what the agent that implemented the issue said the issue got wrong.
    /// Returns empty string if there are neither categorizable issues nor feedback.
    /// </summary>
    public static string BuildProposalOutcomeContext(
        IReadOnlyList<IssueSummary> closedIssues,
        IReadOnlyDictionary<string, string>? implementerFeedback = null)
    {
        var feedbackLines = closedIssues
            .Where(i => implementerFeedback?.ContainsKey(i.Identifier) == true)
            .Select(i => $"- #{i.Identifier} \"{i.Title}\" — {implementerFeedback![i.Identifier]}")
            .ToList();

        var implemented = new List<IssueSummary>();
        var rejected = new List<IssueSummary>();

        foreach (var issue in closedIssues)
        {
            if (issue.Labels.Contains(AgentLabels.Done))
                implemented.Add(issue);
            else if (issue.Labels.Contains(AgentLabels.WontDo) || issue.Labels.Contains(AgentLabels.Cancelled))
                rejected.Add(issue);
            // Ambiguous closures (no agent label) are excluded
        }

        if (implemented.Count == 0 && rejected.Count == 0 && feedbackLines.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## Past Proposal Outcomes — Learn From History");
        sb.AppendLine();

        AppendIssueListSection(sb, "### Implemented (completed by an agent — this alone does not show the team valued them)", implemented);
        AppendIssueListSection(sb, "### Rejected (avoid similar proposals)", rejected);

        if (feedbackLines.Count > 0)
        {
            sb.AppendLine("### Implementer Feedback (what past issues got wrong)");
            sb.AppendLine("The agents that implemented these issues reported gaps in the issue itself. Do not repeat them:");
            foreach (var line in feedbackLines)
                sb.AppendLine(line);
            sb.AppendLine();
        }

        sb.AppendLine("Do NOT propose refactorings similar to rejected items above.");
        sb.AppendLine("Do NOT re-propose implemented items: check the current code first — the change may already be in place.");

        return sb.ToString();
    }

    private static void AppendIssueListSection(StringBuilder sb, string heading, List<IssueSummary> issues)
    {
        if (issues.Count == 0)
            return;

        sb.AppendLine(heading);
        foreach (var issue in issues)
            sb.AppendLine($"- #{issue.Identifier} \"{issue.Title}\"");
        sb.AppendLine();
    }
}
