using System.Text;

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
    public static string BuildRefactoringDetectionPrompt()
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

        // Output format
        sb.AppendLine("## Output Format");
        sb.AppendLine();
        sb.AppendLine("Produce your findings as a JSON file at `.kiro/refactoring-proposals.json`.");
        sb.AppendLine("The file must contain an array of proposal objects (maximum 3 proposals).");
        sb.AppendLine();
        sb.AppendLine("Each proposal must follow this schema:");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("[");
        sb.AppendLine("  {");
        sb.AppendLine("    \"title\": \"Short descriptive title of the refactoring opportunity\",");
        sb.AppendLine("    \"affectedFiles\": [\"src/path/to/File1.cs\", \"src/path/to/File2.cs\"],");
        sb.AppendLine("    \"description\": \"Detailed description of what should be changed and how\",");
        sb.AppendLine("    \"rationale\": \"Why this refactoring would improve the codebase (maintainability, readability, performance, etc.)\"");
        sb.AppendLine("  }");
        sb.AppendLine("]");
        sb.AppendLine("```");
        sb.AppendLine();

        // Constraints
        sb.AppendLine("## Constraints");
        sb.AppendLine();
        sb.AppendLine("- Produce at most **3 proposals** per analysis, prioritized by impact");
        sb.AppendLine("- Each proposal must address **one concern** — do not bundle unrelated changes");
        sb.AppendLine("- Include specific file paths in `affectedFiles` — do not use wildcards or vague references");
        sb.AppendLine("- The `rationale` must reference concrete evidence found during exploration");
        sb.AppendLine("- Do NOT modify any source code — only produce the proposals JSON file");
        sb.AppendLine("- If no refactoring opportunities are found, produce an empty array `[]`");

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
}
