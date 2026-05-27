using System.Text;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Builds prompts for the three consolidation loop types:
/// brain consolidation, refactoring detection, and harness suggestions.
/// </summary>
public static class ConsolidationPromptBuilder
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

        // Recency context
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
        sb.AppendLine("## Phase 1: Orient");
        sb.AppendLine();
        sb.AppendLine("Scan all files in the `.brain/` directory recursively. Build a mental inventory of:");
        sb.AppendLine("- All knowledge files and their topics");
        sb.AppendLine("- The directory structure and organization");
        sb.AppendLine("- File sizes and last-modified indicators");
        sb.AppendLine("- Any README or index files that describe the structure");
        sb.AppendLine();
        sb.AppendLine("Do NOT make changes during this phase. Only observe and catalog.");
        sb.AppendLine();

        // Phase 2: Gather Signal
        sb.AppendLine("## Phase 2: Gather Signal");
        sb.AppendLine();
        sb.AppendLine("Read recent session logs and run summaries to identify:");
        sb.AppendLine("- New lessons learned that may duplicate existing entries");
        sb.AppendLine("- Technology decisions that have been superseded");
        sb.AppendLine("- Relative date references (e.g., \"yesterday\", \"last week\") that should be absolute");
        sb.AppendLine("- Entries that reference removed or renamed components");
        sb.AppendLine("- Contradictions between different knowledge files");
        sb.AppendLine("- **Citation data:** Which entries were referenced in session logs and how (helpful, not applicable, outdated)");
        sb.AppendLine();
        sb.AppendLine("### Citation Aggregation");
        sb.AppendLine();
        sb.AppendLine("Session logs contain a `## Brain Entries Referenced` section listing which knowledge entries");
        sb.AppendLine("were consulted and their usefulness (`used, helpful` | `read, not applicable` | `used, outdated`).");
        sb.AppendLine("Aggregate this data across all session logs since the last consolidation:");
        sb.AppendLine("- Count how many sessions cite each entry as **helpful**");
        sb.AppendLine("- Note entries cited as **outdated** by any session (candidates for correction)");
        sb.AppendLine("- Identify entries in `general/`, `technology/`, and `projects/` that are **never cited** in any session log");
        sb.AppendLine();
        sb.AppendLine("Use this citation data to inform decisions in Phase 3 (Consolidate) and Phase 4 (Prune).");
        sb.AppendLine();

        // Phase 2.5: Research & Verify
        sb.AppendLine("## Phase 2.5: Research & Verify");
        sb.AppendLine();
        sb.AppendLine("For entries that reference specific tools, libraries, versions, or external services:");
        sb.AppendLine("- **Verify currency:** Check whether referenced library versions are still the latest (e.g., is the noted NuGet package version still current?)");
        sb.AppendLine("- **Check for better alternatives:** If a workaround or pattern was documented because a tool lacked a feature, verify whether that feature has since been added");
        sb.AppendLine("- **Validate links and references:** If entries reference external documentation URLs or API endpoints, verify they are still valid");
        sb.AppendLine("- **Update outdated information:** If you find newer/better approaches to documented problems, update the entry with the current best practice and note the change");
        sb.AppendLine();
        sb.AppendLine("Use web search to verify information when uncertain. Only update entries where you have high confidence the information has changed — do not speculate.");
        sb.AppendLine();

        // Phase 3: Consolidate
        sb.AppendLine("## Phase 3: Consolidate");
        sb.AppendLine();
        sb.AppendLine("Apply the following transformations:");
        sb.AppendLine("- **Merge duplicates:** Combine entries that describe the same concept into a single, authoritative entry");
        sb.AppendLine("- **Resolve contradictions:** When two entries conflict, keep the more recent or more specific one. Add a note about what was superseded if relevant");
        sb.AppendLine("- **Convert relative dates:** Replace relative time references with absolute dates (e.g., \"yesterday\" → \"2026-01-15\")");
        sb.AppendLine("- **Update references:** Fix references to renamed or moved components");
        sb.AppendLine("- **Improve organization:** Move misplaced entries to their correct section or file");
        sb.AppendLine();

        // Phase 4: Prune
        sb.AppendLine("## Phase 4: Prune");
        sb.AppendLine();
        sb.AppendLine("Remove content that no longer provides value:");
        sb.AppendLine("- Stale entries about components that no longer exist");
        sb.AppendLine("- Session logs older than 30 days that have already been distilled into lessons");
        sb.AppendLine("- Redundant entries that were merged in Phase 3");
        sb.AppendLine("- Empty or placeholder files");
        sb.AppendLine();
        sb.AppendLine("Use citation data from Phase 2 to inform pruning decisions:");
        sb.AppendLine("- **High-value (keep):** Entries cited as helpful in 3+ sessions — these are proven useful");
        sb.AppendLine("- **Outdated (correct or remove):** Entries cited as outdated by any session — verify and either update or mark ⚠️ OUTDATED");
        sb.AppendLine("- **Uncited + stale (prune candidates):** Entries never cited in any session log AND older than their verification window");
        sb.AppendLine("- **Recently written (keep):** Entries written since the last consolidation should be kept regardless of citation count — they haven't had time to be cited yet");
        sb.AppendLine();
        sb.AppendLine("When pruning an entry, check whether it has only `[experience]` sources and was never verified.");
        sb.AppendLine("Entries with `[docs]` sources are more likely to be correct even if uncited — prefer re-verification over pruning.");
        sb.AppendLine();
        sb.AppendLine("Keep the index files (README.md) concise and up-to-date with the current structure.");
        sb.AppendLine();

        // Output expectations
        sb.AppendLine("## Output");
        sb.AppendLine();
        sb.AppendLine("Make all changes directly to the files. After completion, provide a brief summary of what was done:");
        sb.AppendLine("- Number of files modified");
        sb.AppendLine("- Number of entries merged");
        sb.AppendLine("- Number of contradictions resolved");
        sb.AppendLine("- Number of entries pruned");

        return sb.ToString();
    }

    /// <summary>
    /// Builds the holistic refactoring analysis prompt.
    /// Instructs agent to produce bounded proposals as JSON.
    /// </summary>
    /// <param name="maxProposals">Maximum number of proposals the agent should produce.</param>
    public static string BuildRefactoringDetectionPrompt(int maxProposals = 3, string? issueContext = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Holistic Refactoring Analysis");
        sb.AppendLine();
        sb.AppendLine("You are performing a holistic analysis of the codebase to identify refactoring opportunities.");
        sb.AppendLine("Explore the repository structure, read key files, and identify areas where incremental changes have created global incoherence.");
        sb.AppendLine();

        // What to look for
        sb.AppendLine("## What to Look For");
        sb.AppendLine();
        sb.AppendLine("Analyze the codebase for the following categories of technical debt:");
        sb.AppendLine();
        sb.AppendLine("1. **TODO comments** — Left by previous agent runs or developers, indicating incomplete work");
        sb.AppendLine("2. **Duplicated logic** — Similar code patterns repeated across multiple files that could be extracted");
        sb.AppendLine("3. **Naming inconsistencies** — Classes, methods, or variables that don't follow the project's conventions");
        sb.AppendLine("4. **Structural drift** — Areas where the architecture has diverged from the intended design due to incremental changes");
        sb.AppendLine("5. **Overly complex areas** — Methods or classes that have grown too large or have too many responsibilities");
        sb.AppendLine("6. **Dead code & unused artifacts** — Unreferenced methods, properties, classes, or interfaces that are never called; orphaned files from removed features; unused using directives beyond IDE cleanup");
        sb.AppendLine("7. **Obvious bugs** — High-confidence correctness issues: null dereference risks, off-by-one errors, unreachable code paths, resource leaks (opened but never disposed), logic errors (conditions always true/false), race conditions in shared state. Only flag issues where you have strong evidence the code is wrong, not merely suboptimal");
        sb.AppendLine("8. **Stale documentation & misleading comments** — XML doc comments describing behavior the code no longer exhibits; README sections referencing removed features; comments explaining \"why\" that reference conditions no longer true; parameter descriptions that don't match actual parameters");
        sb.AppendLine("9. **Primitive obsession** — String or int parameters representing domain concepts (emails, URLs, IDs, file paths) without validation or type safety; magic numbers/strings without named constants; repeated validation logic for the same concept scattered across multiple call sites");
        sb.AppendLine();

        // How to explore
        sb.AppendLine("## Exploration Strategy");
        sb.AppendLine();
        sb.AppendLine("- Start by understanding the project structure (solution file, project files, directory layout)");
        sb.AppendLine("- Read key architectural files (README, design docs, brain knowledge if available)");
        sb.AppendLine("- Scan source directories for patterns and anomalies");
        sb.AppendLine("- Focus on areas with high file counts or deep nesting as indicators of complexity");
        sb.AppendLine("- Check for consistency in naming, structure, and patterns across similar components");
        sb.AppendLine();

        // Insert issue context between Exploration Strategy and Output Format
        if (!string.IsNullOrEmpty(issueContext))
        {
            sb.Append(issueContext);
            sb.AppendLine();
        }

        // Output format
        sb.AppendLine("## Output Format");
        sb.AppendLine();
        sb.AppendLine("Produce your findings as a JSON file at `.agent/refactoring-proposals.json`.");
        sb.AppendLine($"The file must contain an array of proposal objects (maximum {maxProposals} proposals).");
        sb.AppendLine();
        sb.AppendLine("Each proposal must follow this schema:");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("[");
        sb.AppendLine("  {");
        sb.AppendLine("    \"title\": \"Short descriptive title of the refactoring opportunity\",");
        sb.AppendLine("    \"category\": \"refactoring\",");
        sb.AppendLine("    \"affectedFiles\": [\"src/path/to/File1.cs\", \"src/path/to/File2.cs\"],");
        sb.AppendLine("    \"description\": \"Detailed description of what should be changed and how\",");
        sb.AppendLine("    \"rationale\": \"Why this refactoring would improve the codebase (maintainability, readability, performance, etc.)\",");
        sb.AppendLine("    \"prerequisites\": [\"Add characterization tests for X before refactoring\"],");
        sb.AppendLine("    \"estimatedEffort\": \"small\",");
        sb.AppendLine("    \"riskLevel\": \"low\",");
        sb.AppendLine("    \"technique\": \"Extract Method\"");
        sb.AppendLine("  }");
        sb.AppendLine("]");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### Optional Field Definitions");
        sb.AppendLine();
        sb.AppendLine("The following fields are optional. Include them when you can assess them confidently:");
        sb.AppendLine();
        sb.AppendLine("- **prerequisites** — List of prep work needed before the refactoring can be safely applied (e.g., \"Add characterization tests for X\"). If affected files have no test coverage, include a prerequisite like \"Add characterization tests for X before refactoring\".");
        sb.AppendLine("- **estimatedEffort** — `small` (<5 files, mechanical changes), `medium` (5-15 files with logic changes), or `large` (15-30 files or architectural changes).");
        sb.AppendLine("- **riskLevel** — `low` (rename/move), `medium` (extract/restructure), or `high` (interface changes affecting consumers).");
        sb.AppendLine("- **technique** — Named refactoring pattern if applicable (e.g., Extract Method, Strangler Fig, Branch by Abstraction, Inline Class, Move Method).");
        sb.AppendLine("- **category** — `refactoring` (structural improvements, default if omitted), `bug` (correctness issues), `documentation` (stale/misleading docs or comments), or `dead-code` (unused artifacts to remove).");
        sb.AppendLine();

        // Constraints
        sb.AppendLine("## Constraints");
        sb.AppendLine();
        sb.AppendLine($"- Produce at most **{maxProposals} proposals** per analysis, prioritized by impact");
        sb.AppendLine("- Each proposal must address **one concern** — do not bundle unrelated changes");
        sb.AppendLine("- Include specific file paths in `affectedFiles` — do not use wildcards or vague references");
        sb.AppendLine("- The `rationale` must reference concrete evidence found during exploration");
        sb.AppendLine("- Do NOT modify any source code — only produce the proposals JSON file");
        sb.AppendLine("- If no refactoring opportunities are found, produce an empty array `[]`");
        sb.AppendLine();

        // Scope constraints
        sb.AppendLine("## Scope Requirements");
        sb.AppendLine();
        sb.AppendLine("Each proposal MUST be achievable by a single agent in one run. This means:");
        sb.AppendLine();
        sb.AppendLine("- **Maximum ~30 affected files** (source + test) per proposal");
        sb.AppendLine("- If a refactoring would touch more files, **split it** into independent, self-contained phases that can each be completed alone");
        sb.AppendLine("- Each phase must leave the codebase in a valid, buildable state");
        sb.AppendLine("- Prefer proposals that are mechanical and low-risk (file moves, renames, extractions) over sweeping architectural changes");
        sb.AppendLine("- Do NOT propose changes that require coordinated modifications across serialization boundaries (e.g., JSON schema + MessagePack wire format + all consumers simultaneously)");
        sb.AppendLine("- If a large refactoring is warranted, propose only the smallest first step that delivers value independently");

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
            sb.AppendLine("### Open Refactoring Issues (agent-generated, still pending)");
            foreach (var issue in refactoringIssues)
                sb.AppendLine($"- #{issue.Identifier} \"{issue.Title}\"");
            sb.AppendLine();
        }

        if (otherIssues.Count > 0)
        {
            sb.AppendLine("### Other Recent Open Issues (may overlap)");
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
        sb.AppendLine("## Feedback Context");
        sb.AppendLine();
        sb.AppendLine($"- **Total runs with feedback:** {feedbackCount}");
        sb.AppendLine($"- **Overall success rate:** {successRate:F1}%");
        sb.AppendLine();
        sb.AppendLine("The feedback data file (`feedback-data.json`) in this workspace contains the raw RunFeedback entries from pipeline runs.");
        sb.AppendLine("Each entry includes harness feedback (pipeline/tool issues) and optionally issue feedback (issue/repo quality problems).");
        sb.AppendLine();

        // Instructions
        sb.AppendLine("## Analysis Instructions");
        sb.AppendLine();
        sb.AppendLine("1. Read the feedback data file completely");
        sb.AppendLine("2. Identify recurring patterns across multiple runs — look for repeated categories, similar stuck reasons, and common missing capabilities");
        sb.AppendLine("3. Rank patterns by **frequency** (how many runs mention it) and **impact** (how much it affects success rate)");
        sb.AppendLine("4. Produce the **top 3-5 improvement suggestions** that would have the highest positive impact");
        sb.AppendLine();

        // Quality requirements
        sb.AppendLine("## Suggestion Quality Requirements");
        sb.AppendLine();
        sb.AppendLine("Each suggestion MUST be:");
        sb.AppendLine("- **Concrete and actionable** — specify exactly what to change (e.g., \"Add file X to the initial context provided to the agent\" not \"Provide more context\")");
        sb.AppendLine("- **Grounded in evidence** — reference specific feedback entries or patterns that motivate the suggestion");
        sb.AppendLine("- **Scoped to one change** — each suggestion addresses one improvement, not a bundle of changes");
        sb.AppendLine();
        sb.AppendLine("Do NOT produce abstract recommendations like \"improve error handling\" or \"add more tests\".");
        sb.AppendLine("Every suggestion must reference specific feedback patterns and propose a specific change.");
        sb.AppendLine();

        // Output format
        sb.AppendLine("## Output Format");
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
    /// findings to .agent/refactoring-review.md.
    /// </summary>
    public static string BuildRefactoringReviewPrompt()
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Refactoring Proposals Review");
        sb.AppendLine();
        sb.AppendLine("You are an independent reviewer evaluating refactoring proposals produced by another agent.");
        sb.AppendLine("Your review must be based solely on the proposals file and the actual codebase — you have no shared context with the generator.");
        sb.AppendLine();

        sb.AppendLine("## Input");
        sb.AppendLine();
        sb.AppendLine($"Read the proposals file at `{AgentWorkspacePaths.RefactoringProposalsFilePath}` from the workspace.");
        sb.AppendLine();

        sb.AppendLine("## Evaluation Guidance");
        sb.AppendLine();
        sb.AppendLine("Evaluate each proposal holistically. Areas to consider include (but are not limited to):");
        sb.AppendLine("- Non-existent `affectedFiles` paths — verify the referenced files actually exist in the repository");
        sb.AppendLine("- Proposals not supported by evidence in the codebase");
        sb.AppendLine("- Bundled concerns that should be separate proposals");
        sb.AppendLine("- Abstract rationales lacking concrete code references");
        sb.AppendLine("- **Scope exceeding single-agent capacity** — proposals touching more than ~30 files (source + test), spanning multiple serialization boundaries, or requiring coordinated breaking changes across projects should be flagged as [CRITICAL] with a suggestion to split into smaller phases");
        sb.AppendLine();
        sb.AppendLine("You are not limited to this checklist. Flag any quality issue you identify.");
        sb.AppendLine();

        sb.AppendLine("## Output");
        sb.AppendLine();
        sb.AppendLine($"Write your findings to `{AgentWorkspacePaths.RefactoringReviewFilePath}` using the following severity markers:");
        sb.AppendLine();
        sb.AppendLine("- `[CRITICAL]` — The output is wrong or will cause downstream failures");
        sb.AppendLine("- `[WARNING]` — The output is incomplete but not incorrect");
        sb.AppendLine("- `[SUGGESTION]` — Optional improvement, not required");
        sb.AppendLine();
        sb.AppendLine("Only `[CRITICAL]` and `[WARNING]` findings will trigger a refinement pass. `[SUGGESTION]` findings are informational only.");
        sb.AppendLine();

        sb.AppendLine("## Important Rules");
        sb.AppendLine();
        sb.AppendLine("- If the proposals are thorough and correct, state that explicitly (e.g., \"No issues found\"). Do NOT invent findings.");
        sb.AppendLine("- When stating no issues were found, do NOT echo severity marker syntax. Write \"No issues found\" — not \"No [CRITICAL] issues found\".");
        sb.AppendLine("- Do NOT modify source files, configuration files, or the proposals file. Only read the input and write the review findings file.");

        return sb.ToString();
    }

    /// <summary>
    /// Review prompt for brain consolidation changes.
    /// Instructs discriminator to read .agent/brain-consolidation-diff.md and write
    /// findings to .agent/brain-consolidation-review.md.
    /// </summary>
    public static string BuildBrainConsolidationReviewPrompt()
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Brain Consolidation Review");
        sb.AppendLine();
        sb.AppendLine("You are an independent reviewer evaluating brain consolidation changes produced by another agent.");
        sb.AppendLine("Your review must be based solely on the diff summary file — you have no shared context with the generator.");
        sb.AppendLine();

        sb.AppendLine("## Input");
        sb.AppendLine();
        sb.AppendLine($"Read the diff summary file at `{AgentWorkspacePaths.BrainConsolidationDiffFilePath}` from the workspace.");
        sb.AppendLine();

        sb.AppendLine("## Evaluation Guidance");
        sb.AppendLine();
        sb.AppendLine("Evaluate the consolidation changes holistically. Areas to consider include (but are not limited to):");
        sb.AppendLine("- Incorrectly removed valuable entries that should have been kept");
        sb.AppendLine("- Merged entries that lost important information in the process");
        sb.AppendLine("- Contradictions resolved by keeping the wrong version");
        sb.AppendLine("- Inaccurate factual updates");
        sb.AppendLine("- New contradictions introduced by the consolidation itself");
        sb.AppendLine();
        sb.AppendLine("You are not limited to this checklist. Flag any quality issue you identify.");
        sb.AppendLine();

        sb.AppendLine("## Output");
        sb.AppendLine();
        sb.AppendLine($"Write your findings to `{AgentWorkspacePaths.BrainConsolidationReviewFilePath}` using the following severity markers:");
        sb.AppendLine();
        sb.AppendLine("- `[CRITICAL]` — The output is wrong or will cause downstream failures");
        sb.AppendLine("- `[WARNING]` — The output is incomplete but not incorrect");
        sb.AppendLine("- `[SUGGESTION]` — Optional improvement, not required");
        sb.AppendLine();
        sb.AppendLine("Only `[CRITICAL]` and `[WARNING]` findings will trigger a refinement pass. `[SUGGESTION]` findings are informational only.");
        sb.AppendLine();

        sb.AppendLine("## Important Rules");
        sb.AppendLine();
        sb.AppendLine("- If the consolidation changes are thorough and correct, state that explicitly (e.g., \"No issues found\"). Do NOT invent findings.");
        sb.AppendLine("- When stating no issues were found, do NOT echo severity marker syntax. Write \"No issues found\" — not \"No [CRITICAL] issues found\".");
        sb.AppendLine("- Do NOT modify source files, configuration files, or the `.brain/` files being reviewed. Only read the input and write the review findings file.");

        return sb.ToString();
    }

    /// <summary>
    /// Review prompt for harness suggestions.
    /// Instructs discriminator to read .agent/harness-suggestions-output.json and
    /// feedback-data.json, then write findings to .agent/harness-suggestions-review.md.
    /// </summary>
    public static string BuildHarnessSuggestionsReviewPrompt()
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Harness Suggestions Review");
        sb.AppendLine();
        sb.AppendLine("You are an independent reviewer evaluating harness improvement suggestions produced by another agent.");
        sb.AppendLine("Your review must be based solely on the suggestions file and the original feedback data — you have no shared context with the generator.");
        sb.AppendLine();

        sb.AppendLine("## Input");
        sb.AppendLine();
        sb.AppendLine($"Read the suggestions file at `{AgentWorkspacePaths.HarnessSuggestionsOutputFilePath}` from the workspace.");
        sb.AppendLine("Also read `feedback-data.json` from the workspace root to cross-reference suggestions against actual feedback data.");
        sb.AppendLine();

        sb.AppendLine("## Evaluation Guidance");
        sb.AppendLine();
        sb.AppendLine("Evaluate the suggestions holistically. Areas to consider include (but are not limited to):");
        sb.AppendLine("- Suggestions not grounded in specific feedback patterns from the data");
        sb.AppendLine("- Abstract or non-actionable suggestions that lack specificity");
        sb.AppendLine("- Implausible frequency counts that don't match the feedback data");
        sb.AppendLine("- Bundled concerns that should be separate suggestions");
        sb.AppendLine("- Rationales lacking specific evidence from feedback entries");
        sb.AppendLine();
        sb.AppendLine("You are not limited to this checklist. Flag any quality issue you identify.");
        sb.AppendLine();

        sb.AppendLine("## Output");
        sb.AppendLine();
        sb.AppendLine($"Write your findings to `{AgentWorkspacePaths.HarnessSuggestionsReviewFilePath}` using the following severity markers:");
        sb.AppendLine();
        sb.AppendLine("- `[CRITICAL]` — The output is wrong or will cause downstream failures");
        sb.AppendLine("- `[WARNING]` — The output is incomplete but not incorrect");
        sb.AppendLine("- `[SUGGESTION]` — Optional improvement, not required");
        sb.AppendLine();
        sb.AppendLine("Only `[CRITICAL]` and `[WARNING]` findings will trigger a refinement pass. `[SUGGESTION]` findings are informational only.");
        sb.AppendLine();

        sb.AppendLine("## Important Rules");
        sb.AppendLine();
        sb.AppendLine("- If the suggestions are thorough and correct, state that explicitly (e.g., \"No issues found\"). Do NOT invent findings.");
        sb.AppendLine("- When stating no issues were found, do NOT echo severity marker syntax. Write \"No issues found\" — not \"No [CRITICAL] issues found\".");
        sb.AppendLine("- Do NOT modify source files, configuration files, or the suggestions file being reviewed. Only read the input files and write the review findings file.");

        return sb.ToString();
    }

    /// <summary>
    /// Refinement prompt for refactoring proposals.
    /// Instructs generator to read .agent/refactoring-review.md and update
    /// .agent/refactoring-proposals.json accordingly.
    /// </summary>
    public static string BuildRefactoringRefinementPrompt()
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Refactoring Proposals Refinement");
        sb.AppendLine();
        sb.AppendLine("A review of your refactoring proposals has been completed.");
        sb.AppendLine();
        sb.AppendLine($"Read the review findings at `{AgentWorkspacePaths.RefactoringReviewFilePath}`.");
        sb.AppendLine();
        sb.AppendLine("Address all `[CRITICAL]` and `[WARNING]` findings by updating `" + AgentWorkspacePaths.RefactoringProposalsFilePath + "`.");
        sb.AppendLine();
        sb.AppendLine("Guidelines:");
        sb.AppendLine("- You may remove proposals that the reviewer correctly identified as invalid");
        sb.AppendLine("- You may reduce the proposal count if warranted");
        sb.AppendLine("- Do not add new proposals — only refine or remove existing ones");

        return sb.ToString();
    }

    /// <summary>
    /// Refinement prompt for brain consolidation.
    /// Instructs generator to read .agent/brain-consolidation-review.md and revise
    /// its .brain/ modifications accordingly.
    /// </summary>
    public static string BuildBrainConsolidationRefinementPrompt()
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Brain Consolidation Refinement");
        sb.AppendLine();
        sb.AppendLine("A review of your brain consolidation changes has been completed.");
        sb.AppendLine();
        sb.AppendLine($"Read the review findings at `{AgentWorkspacePaths.BrainConsolidationReviewFilePath}`.");
        sb.AppendLine();
        sb.AppendLine("Address all `[CRITICAL]` and `[WARNING]` findings by revising the `.brain/` files.");
        sb.AppendLine();
        sb.AppendLine("Guidelines:");
        sb.AppendLine("- Restore incorrectly removed entries");
        sb.AppendLine("- Fix incorrect merges");
        sb.AppendLine("- Correct factual errors");
        sb.AppendLine($"- Update `{AgentWorkspacePaths.BrainConsolidationDiffFilePath}` with a note about what was revised");

        return sb.ToString();
    }

    /// <summary>
    /// Refinement prompt for harness suggestions.
    /// Instructs generator to read .agent/harness-suggestions-review.md and update
    /// .agent/harness-suggestions-output.json accordingly.
    /// </summary>
    public static string BuildHarnessSuggestionsRefinementPrompt()
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Harness Suggestions Refinement");
        sb.AppendLine();
        sb.AppendLine("A review of your harness suggestions has been completed.");
        sb.AppendLine();
        sb.AppendLine($"Read the review findings at `{AgentWorkspacePaths.HarnessSuggestionsReviewFilePath}`.");
        sb.AppendLine();
        sb.AppendLine("Address all `[CRITICAL]` and `[WARNING]` findings by updating `" + AgentWorkspacePaths.HarnessSuggestionsOutputFilePath + "`.");
        sb.AppendLine();
        sb.AppendLine("Guidelines:");
        sb.AppendLine("- Make suggestions more concrete, add specific evidence references, correct frequency counts");
        sb.AppendLine("- You may remove suggestions that the reviewer correctly identified as unfounded");

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
        sb.AppendLine("Include the following in your summary:");
        sb.AppendLine("- Files created, modified, or deleted");
        sb.AppendLine("- Entries merged (before/after)");
        sb.AppendLine("- Contradictions resolved (which version was kept)");
        sb.AppendLine("- Factual updates (with sources)");
        sb.AppendLine();
        sb.AppendLine("This summary will be reviewed by an independent agent — be thorough and specific.");
        sb.AppendLine();
        sb.AppendLine("Do NOT make additional modifications to `.brain/` files in this step.");

        return sb.ToString();
    }

    /// <summary>
    /// Builds a prompt section summarizing past refactoring proposal outcomes.
    /// Categorizes closed issues as implemented (agent:done) or rejected (agent:wont-do/agent:cancelled).
    /// Issues without agent labels are excluded. Returns empty string if no categorizable issues.
    /// </summary>
    public static string BuildProposalOutcomeContext(IReadOnlyList<IssueSummary> closedIssues)
    {
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

        if (implemented.Count == 0 && rejected.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine("## Past Proposal Outcomes — Learn From History");
        sb.AppendLine();

        if (implemented.Count > 0)
        {
            sb.AppendLine("### Implemented (team valued these)");
            foreach (var issue in implemented)
                sb.AppendLine($"- #{issue.Identifier} \"{issue.Title}\"");
            sb.AppendLine();
        }

        if (rejected.Count > 0)
        {
            sb.AppendLine("### Rejected (avoid similar proposals)");
            foreach (var issue in rejected)
                sb.AppendLine($"- #{issue.Identifier} \"{issue.Title}\"");
            sb.AppendLine();
        }

        sb.AppendLine("Do NOT propose refactorings similar to rejected items above.");
        sb.AppendLine("Proposals similar to implemented items are encouraged — the team values this type of improvement.");

        return sb.ToString();
    }
}
