using System.Text;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services.Prompts;

/// <summary>
/// Builds structured prompts for decomposition agent phases.
/// Follows the ConsolidationPromptBuilder pattern.
/// </summary>
public static class DecompositionPromptBuilder
{
    /// <summary>
    /// Builds the Phase 1 analysis prompt.
    /// Instructs the agent to explore the codebase and produce a decomposition plan.
    /// </summary>
    /// <param name="maxSubIssues">Maximum number of sub-issues the agent may propose.</param>
    /// <param name="maxFiles">Maximum files a single sub-issue may create or modify.</param>
    public static string BuildAnalysisPrompt(int maxSubIssues, int maxFiles)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Epic Decomposition Analysis");
        sb.AppendLine();
        sb.AppendLine("You are performing a decomposition analysis of an epic issue.");
        sb.AppendLine("Your goal is to explore the codebase and produce a structured decomposition plan");
        sb.AppendLine("that breaks the epic into implementation-ready sub-issues.");
        sb.AppendLine();

        // Exploration instructions
        PromptBuilder.AppendSection(sb, "## Exploration Strategy", s =>
        {
            s.AppendLine("Before proposing sub-issues, thoroughly explore the codebase:");
            s.AppendLine();
            s.AppendLine("1. **Directory tree** — Understand the project structure, solution layout, and module boundaries");
            s.AppendLine("2. **Architecture files** — Read README, design docs, and any `.brain/` knowledge if available");
            s.AppendLine("3. **DI setup** — Review dependency injection configuration to understand service wiring");
            s.AppendLine("4. **Similar features** — Find 1-2 existing features similar to the epic and study their implementation patterns");
        });

        // Open issues deduplication
        PromptBuilder.AppendSection(sb, "## Deduplication Check", s =>
        {
            s.AppendLine($"Open issues are in `{AgentWorkspacePaths.OpenIssuesDirectory}/` — read them to check for overlap before proposing sub-issues.");
            s.AppendLine("Do NOT propose sub-issues that duplicate work already tracked in existing open issues.");
            s.AppendLine("If you identify partial overlap, note the related issue in your plan and explain why your proposal is distinct.");
        });

        // Re-run feedback instructions
        PromptBuilder.AppendSection(sb, "## Re-run Feedback", s =>
        {
            s.AppendLine("If this is a re-run (a previous plan was rejected), look for comments posted after the");
            s.AppendLine("previous plan comment in `.agent/issue-context.md` as rejection feedback.");
            s.AppendLine("Address all feedback points in your revised plan.");
        });

        // Gate rejection concerns
        PromptBuilder.AppendSection(sb, "## Gate Rejection Concerns", s =>
        {
            s.AppendLine("If `.agent/issue-context.md` contains analysis gate comments (marked with `<!-- agent:gate-rejection -->`),");
            s.AppendLine("treat each concern listed in the 'Blocking Issues' or 'Concerns' section as a **hard constraint**.");
            s.AppendLine("Your decomposition plan must explicitly address how each concern is resolved:");
            s.AppendLine();
            s.AppendLine("- For each gate concern, state which sub-issue handles it and how");
            s.AppendLine("- If a concern spans multiple sub-issues, explain the handoff between them");
            s.AppendLine("- If you believe a concern is invalid, explain why with evidence from the codebase");
            s.AppendLine();
            s.AppendLine("Do NOT treat gate concerns as generic \"split it up\" guidance — they identify specific");
            s.AppendLine("technical risks that must be individually mitigated in your plan.");
        });

        // Sizing constraints
        PromptBuilder.AppendSection(sb, "## Sub-Issue Sizing Constraints", s =>
        {
            s.AppendLine("Each proposed sub-issue MUST satisfy ALL of the following constraints:");
            s.AppendLine();
            s.AppendLine($"- **File limit:** Create or modify a maximum of **{maxFiles} files** (files only read for context do not count)");
            s.AppendLine("- **One verification criterion:** Exactly one pass/fail assertion (e.g., \"unit test X passes\", \"build succeeds with no warnings\")");
            s.AppendLine("- **One agent run:** Completable in a single agent run (single context window, no multi-session work, no waiting on external feedback)");
        });

        // Cap and ordering
        PromptBuilder.AppendSection(sb, "## Constraints", s =>
        {
            s.AppendLine($"- Propose at most **{maxSubIssues}** sub-issues");
            s.AppendLine("- Order sub-issues so that **dependencies always point backward** — earlier-numbered sub-issues are depended upon by later-numbered ones");
            s.AppendLine("- Each sub-issue must have a unique, descriptive title");
            s.AppendLine("- Do NOT propose sub-issues that require multi-session execution or external feedback");
        });

        // Output format
        // NOTE (issue #3534): This is the final section of BuildAnalysisPrompt(int, int). AppendSection appends a
        // trailing blank line that the original inline block did not emit before return. The new snapshot
        // Snapshot_BuildAnalysisPrompt_Decomposition locks in the +\n+\n ending rather than a pre-refactor
        // baseline, so it does not prove AC3 ("unchanged output"). Additionally, the cross-repo overload
        // BuildAnalysisPrompt(int, int, DecompositionProjectContext?) concatenates prompt + routing instructions,
        // so the extra trailing blank now appears at the seam — the concatenated output also has one more blank
        // line than pre-refactor. No snapshot covers the projectContext != null path.
        // (Review finding: correctness agent, DecompositionPromptBuilder.cs)
        PromptBuilder.AppendSection(sb, "## Output", s =>
        {
            s.AppendLine($"Write your decomposition plan to `{AgentWorkspacePaths.DecompositionPlanFilePath}` in the workspace.");
            s.AppendLine();
            s.AppendLine("The plan must include:");
            s.AppendLine();
            s.AppendLine("1. **Strategy rationale** — 2-3 sentences explaining why you split the epic this way");
            s.AppendLine("2. **Sub-issue table** with columns: #, Title, Scope (one sentence), Files (estimated count), Dependencies (by title or \"None\"), Verification (one criterion)");
            s.AppendLine("3. **Dependency graph** — Sub-issues listed in execution order showing blocking relationships");
            s.AppendLine();
            s.AppendLine("Do NOT create any source code files. Only produce the decomposition plan.");
        });

        return sb.ToString();
    }

    /// <summary>
    /// Builds the Phase 1 analysis prompt with optional cross-repo project context.
    /// When project context is present, appends routing instructions for cross-repo decomposition.
    /// When project context is null, returns the standard analysis prompt (backward compatible).
    /// </summary>
    /// <param name="maxSubIssues">Maximum number of sub-issues the agent may propose.</param>
    /// <param name="maxFiles">Maximum files a single sub-issue may create or modify.</param>
    /// <param name="projectContext">Project context for cross-repo decomposition, or null for single-repo decomposition.</param>
    public static string BuildAnalysisPrompt(int maxSubIssues, int maxFiles, DecompositionProjectContext? projectContext)
    {
        var prompt = BuildAnalysisPrompt(maxSubIssues, maxFiles);

        if (projectContext is null)
            return prompt;

        // NOTE (issue #3534): Because BuildAnalysisPrompt(int, int) now ends with AppendSection's trailing blank line,
        // the concatenated output here has one more blank line at the seam between the base prompt and the routing
        // instructions than the pre-refactor output. No snapshot test covers this projectContext != null path;
        // a regression here would go undetected. Add a deterministic snapshot for this cross-repo overload.
        // (Review finding: correctness agent, DecompositionPromptBuilder.cs)
        return prompt + BuildCrossRepoRoutingInstructions();
    }

    /// <summary>
    /// Builds the Phase 2 sub-issue generation prompt.
    /// Instructs the agent to produce full issue descriptions as JSON files.
    /// </summary>
    /// <param name="maxSubIssues">Maximum number of sub-issues the agent may produce.</param>
    /// <param name="maxFiles">Maximum files a single sub-issue may create or modify.</param>
    public static string BuildDecompositionPrompt(int maxSubIssues, int maxFiles)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Epic Decomposition — Sub-Issue Generation");
        sb.AppendLine();
        sb.AppendLine("You are generating full implementation-ready sub-issue descriptions from an approved decomposition plan.");
        sb.AppendLine("The approved plan is available in the issue comments within `.agent/issue-context.md`.");
        sb.AppendLine();

        // Context
        PromptBuilder.AppendSection(sb, "## Context", s =>
        {
            s.AppendLine("Read the approved plan from `.agent/issue-context.md` (the plan comment is identified by the");
            s.AppendLine("`<!-- agent:decomposition-plan -->` marker in the comment thread).");
            s.AppendLine("Also explore the codebase to produce accurate file paths and implementation details.");
        });

        // Deduplication
        PromptBuilder.AppendSection(sb, "## Deduplication", s =>
        {
            s.AppendLine("If existing agent-generated sub-issues are listed in the context, do NOT duplicate them.");
            s.AppendLine("Only produce sub-issues for items in the plan that are not already created.");
        });

        // Output format — JSON schema
        PromptBuilder.AppendSection(sb, "## Output Format", s =>
        {
            s.AppendLine($"Produce full issue descriptions as JSON files at `{AgentWorkspacePaths.SubIssuesDirectory}/{{NN}}-{{title-slug}}.json`");
            s.AppendLine("where `{NN}` is a zero-padded two-digit sequence number (01, 02, ...) and `{title-slug}` is a");
            s.AppendLine("lowercase, hyphen-separated slug derived from the title (max 60 characters).");
            s.AppendLine();
            s.AppendLine($"Produce at most **{maxSubIssues}** sub-issue files.");
            s.AppendLine();
            s.AppendLine("Each JSON file MUST conform to this schema:");
            s.AppendLine();
            s.AppendLine("```json");
            s.AppendLine("{");
            s.AppendLine("  \"title\": \"Short descriptive title (max 256 characters)\",");
            s.AppendLine("  \"body\": \"Full markdown issue body (see template below)\",");
            s.AppendLine("  \"dependencies\": [\"Title of another sub-issue this depends on\"],");
            s.AppendLine("  \"labels\": [\"enhancement\"]");
            s.AppendLine("}");
            s.AppendLine("```");
            s.AppendLine();
            s.AppendLine("### Required Fields");
            s.AppendLine();
            s.AppendLine("- **title** — Non-empty string, maximum 256 characters");
            s.AppendLine("- **body** — Non-empty markdown string following the issue template");
            s.AppendLine("- **dependencies** — Array of title strings referencing other sub-issues in this decomposition (use exact titles, not issue numbers)");
            s.AppendLine("- **labels** — Array of additional label strings (the `agent:next` and `agent:generated` labels are applied automatically)");
        });

        // Issue template sections
        PromptBuilder.AppendSection(sb, "## Issue Body Template", s =>
        {
            s.AppendLine("The `body` field MUST include the following sections in order:");
            s.AppendLine();
            s.AppendLine("1. **## Summary** — 2-3 sentence problem statement");
            s.AppendLine("2. **## Affected Components** — List of file paths with brief roles");
            s.AppendLine("3. **## Requirements** — Hard constraints the implementation must satisfy");
            s.AppendLine("4. **## Acceptance Criteria** — Specific observable outcomes (checkboxes)");
            s.AppendLine();
            s.AppendLine("Optional sections (include when relevant):");
            s.AppendLine("- **## Suggested Approach** — One possible implementation path");
            s.AppendLine("- **## Out of Scope** — What this issue intentionally does NOT cover");
            s.AppendLine("- **## Related Issues** — References to related issues");
        });

        // Dependency ordering
        PromptBuilder.AppendSection(sb, "## Dependency Ordering", s =>
        {
            s.AppendLine("Order files so that **dependencies always point backward** — earlier-numbered sub-issues");
            s.AppendLine("are depended upon by later-numbered ones. The numeric prefix in the filename determines");
            s.AppendLine("creation order, so a sub-issue at `02-*.json` can depend on `01-*.json` but NOT vice versa.");
            s.AppendLine();
            s.AppendLine("Use exact title strings in the `dependencies` array (they will be resolved to `#N` format during creation).");
        });

        // Constraints
        PromptBuilder.AppendSection(sb, "## Constraints", s =>
        {
            s.AppendLine($"- Each sub-issue must create or modify a maximum of **{maxFiles} files**");
            s.AppendLine("- Each sub-issue must have exactly **one verification criterion** in its acceptance criteria");
            s.AppendLine("- Each sub-issue must be completable in **one agent run**");
            s.AppendLine("- All sub-issue titles must be **unique**");
            s.AppendLine("- Files must be encoded as **UTF-8 without BOM**");
            s.AppendLine("- Do NOT create any source code files. Only produce the sub-issue JSON files.");
        });

        return sb.ToString();
    }

    /// <summary>
    /// Builds the Phase 2 sub-issue generation prompt with optional cross-repo project context.
    /// When project context is present, appends routing instructions including targetRepository field guidance.
    /// When project context is null, returns the standard prompt (backward compatible).
    /// </summary>
    /// <param name="maxSubIssues">Maximum number of sub-issues the agent may produce.</param>
    /// <param name="maxFiles">Maximum files a single sub-issue may create or modify.</param>
    /// <param name="projectContext">Project context for cross-repo decomposition, or null for single-repo decomposition.</param>
    public static string BuildDecompositionPrompt(int maxSubIssues, int maxFiles, DecompositionProjectContext? projectContext)
    {
        var prompt = BuildDecompositionPrompt(maxSubIssues, maxFiles);

        if (projectContext is null)
            return prompt;

        // NOTE (issue #3534): Same seam issue as BuildAnalysisPrompt(int, int, projectContext) above — BuildDecompositionPrompt(int, int)
        // now ends with AppendSection's trailing blank line, so the concatenated output has one extra blank line at the
        // seam. No snapshot covers this path. Add a snapshot for BuildDecompositionPrompt with projectContext != null.
        // (Review finding: correctness agent, DecompositionPromptBuilder.cs)
        return prompt + BuildCrossRepoRoutingInstructions();
    }

    /// <summary>
    /// Builds the adversarial review prompt for plan validation.
    /// Instructs the reviewer to validate the decomposition plan against quality criteria.
    /// </summary>
    /// <param name="maxFiles">Maximum files a single sub-issue may create or modify.</param>
    public static string BuildReviewPrompt(int maxFiles)
        => BuildReviewPromptCore(maxFiles, maxSubIssues: null);

    /// <summary>
    /// Builds the adversarial review prompt for plan validation with sub-issue cap enforcement.
    /// </summary>
    /// <param name="maxFiles">Maximum files a single sub-issue may create or modify.</param>
    /// <param name="maxSubIssues">
    /// Maximum number of sub-issues the plan may contain.
    /// A plan that exceeds this number is flagged as <c>[CRITICAL]</c>.
    /// When null, no sub-issue count check is included (backward compatible).
    /// </param>
    public static string BuildReviewPrompt(int maxFiles, int? maxSubIssues)
        => BuildReviewPromptCore(maxFiles, maxSubIssues);

    /// <summary>
    /// Builds the adversarial review prompt for plan validation with optional cross-repo routing validation.
    /// When project context is present, appends validation rules for targetRepository values.
    /// When project context is null, returns the standard review prompt (backward compatible).
    /// </summary>
    /// <param name="maxFiles">Maximum files a single sub-issue may create or modify.</param>
    /// <param name="projectContext">Project context for cross-repo decomposition, or null for single-repo decomposition.</param>
    public static string BuildReviewPrompt(int maxFiles, DecompositionProjectContext? projectContext)
        => BuildReviewPrompt(maxFiles, maxSubIssues: null, projectContext);

    /// <summary>
    /// Builds the adversarial review prompt for plan validation with sub-issue cap enforcement
    /// and optional cross-repo routing validation.
    /// </summary>
    /// <param name="maxFiles">Maximum files a single sub-issue may create or modify.</param>
    /// <param name="maxSubIssues">
    /// Maximum number of sub-issues the plan may contain.
    /// When provided, a plan that exceeds this count is flagged as <c>[CRITICAL]</c>.
    /// When null, no sub-issue count check is included.
    /// </param>
    /// <param name="projectContext">Project context for cross-repo decomposition, or null for single-repo decomposition.</param>
    public static string BuildReviewPrompt(int maxFiles, int? maxSubIssues, DecompositionProjectContext? projectContext)
    {
        var prompt = BuildReviewPromptCore(maxFiles, maxSubIssues);

        if (projectContext is null)
            return prompt;

        return prompt + BuildCrossRepoReviewAdditions();
    }

    /// <summary>
    /// Core implementation for <see cref="BuildReviewPrompt(int)"/> and its overloads.
    /// </summary>
    private static string BuildReviewPromptCore(int maxFiles, int? maxSubIssues)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Decomposition Plan Review");
        sb.AppendLine();
        sb.AppendLine("You are an independent reviewer evaluating a decomposition plan produced by another agent.");
        sb.AppendLine("Your review must be based solely on the plan file and the actual codebase — you have no shared context with the generator.");
        sb.AppendLine();

        // Input
        sb.AppendLine("## Input");
        sb.AppendLine();
        sb.AppendLine($"Read the decomposition plan at `{AgentWorkspacePaths.DecompositionPlanFilePath}` from the workspace.");
        sb.AppendLine($"Also read open issues in `{AgentWorkspacePaths.OpenIssuesDirectory}/` for overlap checking.");
        sb.AppendLine();

        // Evaluation criteria
        sb.AppendLine("## Evaluation Criteria");
        sb.AppendLine();
        sb.AppendLine("Validate the plan against ALL of the following criteria:");
        sb.AppendLine();
        sb.AppendLine("### 1. Overlap Check");
        sb.AppendLine();
        sb.AppendLine($"Compare proposed sub-issues against open issues in `{AgentWorkspacePaths.OpenIssuesDirectory}/`.");
        sb.AppendLine("Flag any sub-issue that substantially overlaps with an existing open issue as `[CRITICAL]`.");
        sb.AppendLine();
        sb.AppendLine("### 2. Sizing Validation");
        sb.AppendLine();
        sb.AppendLine("Each sub-issue must be right-sized for autonomous agent execution:");
        sb.AppendLine();
        sb.AppendLine($"- Creates or modifies **≤{maxFiles} files** (files only read for context do not count)");
        sb.AppendLine("- Has exactly **one verification criterion** (single pass/fail assertion)");
        sb.AppendLine("- Is completable in **one agent run** (single context window, no multi-session work)");
        sb.AppendLine();
        sb.AppendLine("Flag violations as `[CRITICAL]`.");

        if (maxSubIssues.HasValue)
        {
            // TODO: This Section 2 blurb duplicates the cap constraint that is stated more completely
            // in Section 6 below. An agent that applies Section 2 but misses Section 6 would treat
            // this as guidance about *how* to fix a sizing finding, but would not flag an over-cap
            // plan as [CRITICAL] on its own. Consider removing this blurb and relying solely on
            // Section 6 to avoid inconsistent agent interpretation.
            sb.AppendLine();
            sb.AppendLine("When a sub-issue exceeds the file limit, the fix is to move scope to another sub-issue or merge");
            sb.AppendLine($"sub-issues — **NOT** to add a new sub-issue that would push the count above {maxSubIssues.Value}.");
        }

        sb.AppendLine();
        sb.AppendLine("### 3. Acyclic Dependencies");
        sb.AppendLine();
        sb.AppendLine("Verify that the dependency graph is acyclic (no circular dependencies).");
        sb.AppendLine("Dependencies must point backward (earlier-numbered sub-issues are depended upon by later ones).");
        sb.AppendLine("Flag cycles as `[CRITICAL]`.");
        sb.AppendLine();
        sb.AppendLine("### 4. Coverage Check");
        sb.AppendLine();
        sb.AppendLine("Verify that all acceptance criteria from the epic (in `.agent/issue-context.md`) are covered");
        sb.AppendLine("by at least one proposed sub-issue. Flag uncovered acceptance criteria as `[CRITICAL]`.");
        sb.AppendLine();
        sb.AppendLine("### 5. Duplicate Title Check");
        sb.AppendLine();
        sb.AppendLine("Verify that all proposed sub-issue titles are unique (case-insensitive comparison).");
        sb.AppendLine("Flag duplicate titles as `[CRITICAL]`.");
        sb.AppendLine();

        if (maxSubIssues.HasValue)
        {
            sb.AppendLine($"### 6. Sub-Issue Cap ({maxSubIssues.Value})");
            sb.AppendLine();
            sb.AppendLine($"Count the total number of sub-issues proposed in the plan.");
            sb.AppendLine($"If the count exceeds **{maxSubIssues.Value}**, flag it as `[CRITICAL]`.");
            sb.AppendLine($"The cap is as binding as the per-sub-issue file limit.");
            sb.AppendLine($"A sizing finding must propose rebalancing or merging scope between existing sub-issues,");
            sb.AppendLine($"**never** adding a new sub-issue when the plan is already at or above the cap.");
            sb.AppendLine();
        }

        // Output
        sb.AppendLine("## Output");
        sb.AppendLine();
        sb.AppendLine($"Write your findings to `{AgentWorkspacePaths.DecompositionReviewFilePath}` using the following severity markers:");
        sb.AppendLine();
        sb.AppendLine("- `[CRITICAL]` — The plan has a defect that must be fixed before approval");
        sb.AppendLine("- `[WARNING]` — The plan is incomplete or suboptimal but not incorrect");
        sb.AppendLine("- `[SUGGESTION]` — Optional improvement, not required");
        sb.AppendLine();
        sb.AppendLine("Only `[CRITICAL]` and `[WARNING]` findings will trigger a refinement pass. `[SUGGESTION]` findings are informational only.");
        sb.AppendLine();

        // Important rules
        sb.AppendLine("## Important Rules");
        sb.AppendLine();
        sb.AppendLine("- If the plan is thorough and correct, state that explicitly (e.g., \"No issues found\"). Do NOT invent findings.");
        sb.AppendLine("- When stating no issues were found, do NOT echo severity marker syntax. Write \"No issues found\" — not \"No [CRITICAL] issues found\".");
        sb.AppendLine($"- {PromptBuilder.FindingLineRule}");
        sb.AppendLine("- Do NOT modify source files, configuration files, or the plan file. Only read the inputs and write the review findings file.");

        return sb.ToString();
    }

    /// <summary>
    /// Builds the refinement prompt sent back to the generator after review findings.
    /// Instructs the generator to address CRITICAL and WARNING findings.
    /// </summary>
    /// <param name="maxFiles">Maximum files a single sub-issue may create or modify.</param>
    public static string BuildRefinementPrompt(int maxFiles)
        => BuildRefinementPrompt(maxFiles, maxSubIssues: null);

    /// <summary>
    /// Builds the refinement prompt sent back to the generator after review findings,
    /// including a sub-issue cap constraint.
    /// </summary>
    /// <param name="maxFiles">Maximum files a single sub-issue may create or modify.</param>
    /// <param name="maxSubIssues">
    /// Maximum number of sub-issues the refined plan may contain.
    /// When provided, the constraint is listed alongside the file limit so the agent
    /// cannot resolve sizing findings by adding new sub-issues.
    /// When null, no sub-issue count constraint is included (backward compatible).
    /// </param>
    public static string BuildRefinementPrompt(int maxFiles, int? maxSubIssues)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Decomposition Plan Refinement");
        sb.AppendLine();
        sb.AppendLine("Your decomposition plan has been reviewed and findings have been identified.");
        sb.AppendLine("You must address the review findings and update your plan accordingly.");
        sb.AppendLine();

        // Input
        sb.AppendLine("## Input");
        sb.AppendLine();
        sb.AppendLine($"Read the review findings at `{AgentWorkspacePaths.DecompositionReviewFilePath}`.");
        sb.AppendLine();

        // Instructions
        sb.AppendLine("## Instructions");
        sb.AppendLine();
        sb.AppendLine("1. Read all findings in the review file");
        sb.AppendLine("2. Address **all `[CRITICAL]` findings** — these MUST be resolved");
        sb.AppendLine("3. Address **all `[WARNING]` findings** — these SHOULD be resolved");
        sb.AppendLine("4. `[SUGGESTION]` findings are optional — address them if straightforward");
        sb.AppendLine();

        // Output
        sb.AppendLine("## Output");
        sb.AppendLine();
        sb.AppendLine($"Update `{AgentWorkspacePaths.DecompositionPlanFilePath}` with the refined plan that addresses all findings.");
        sb.AppendLine();
        sb.AppendLine("Ensure the updated plan still satisfies all original constraints:");
        sb.AppendLine();
        sb.AppendLine($"- Each sub-issue creates or modifies ≤{maxFiles} files");
        sb.AppendLine("- Each sub-issue has exactly one verification criterion");
        sb.AppendLine("- Each sub-issue is completable in one agent run");
        sb.AppendLine("- Dependencies point backward (no cycles)");
        sb.AppendLine("- No overlap with existing open issues");
        sb.AppendLine("- All sub-issue titles are unique");

        if (maxSubIssues.HasValue)
        {
            sb.AppendLine($"- **At most {maxSubIssues.Value} sub-issues** — this is as binding as the file limit");
            sb.AppendLine();
            sb.AppendLine($"**IMPORTANT:** If the plan currently exceeds {maxSubIssues.Value} sub-issues, you MUST reduce the");
            sb.AppendLine($"count by merging or removing sub-issues. A split that would keep the count above {maxSubIssues.Value}");
            sb.AppendLine("is not an acceptable resolution of a sizing finding.");
        }

        sb.AppendLine();
        sb.AppendLine("Do NOT create any source code files. Only update the decomposition plan.");

        return sb.ToString();
    }

    /// <summary>
    /// Builds the cross-repo routing instructions appended to analysis and decomposition prompts
    /// when a project-level EpicIssueProviderId is set (cross-repo decomposition).
    /// </summary>
    private static string BuildCrossRepoRoutingInstructions()
    {
        var sb = new StringBuilder();

        sb.AppendLine();
        sb.AppendLine("## Cross-Repository Routing");
        sb.AppendLine();
        sb.AppendLine("This is a cross-repository decomposition. Multiple project repositories are available for exploration.");
        sb.AppendLine();
        sb.AppendLine("Read `.agent/project-context.md` to understand all repositories in this project.");
        sb.AppendLine("Each repository's **Local path** tells you where its code is cloned:");
        sb.AppendLine("- The primary repository is at workspace root (`.`)");
        sb.AppendLine("- Additional repositories are at `repos/{template-name}/`");
        sb.AppendLine();
        sb.AppendLine("EXPLORATION STRATEGY:");
        sb.AppendLine("- Start by exploring the primary repository at workspace root");
        sb.AppendLine("- When you identify sub-issues that target a specific secondary repository, explore its code at the listed path to validate file paths and understand its patterns");
        sb.AppendLine("- Do NOT exhaustively explore all repos — only explore a secondary repo when a sub-issue clearly belongs there");
        sb.AppendLine();
        sb.AppendLine("ROUTING RULES:");
        sb.AppendLine("- For each sub-issue JSON file, include a `targetRepository` field matching EXACTLY one of the template names from the project context (case-sensitive).");
        sb.AppendLine("- If a sub-issue requires changes in multiple repositories, assign it to the PRIMARY repository and note cross-cutting dependencies in the issue body.");
        sb.AppendLine("- If you cannot determine the appropriate repository, omit the `targetRepository` field (the issue will be created in the default repository).");
        sb.AppendLine("- Do NOT route sub-issues to repositories marked as unavailable in the project context.");

        return sb.ToString();
    }

    /// <summary>
    /// Builds additional adversarial review criteria for cross-repo targetRepository validation.
    /// Appended to the review prompt when project context is present.
    /// </summary>
    private static string BuildCrossRepoReviewAdditions()
    {
        var sb = new StringBuilder();

        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("## Cross-Repo Routing Validation");
        sb.AppendLine();
        sb.AppendLine("CROSS-REPO ROUTING VALIDATION:");
        sb.AppendLine("- Verify all `targetRepository` values in the decomposition plan match valid template names from `.agent/project-context.md`.");
        sb.AppendLine("- Flag any unresolvable `targetRepository` value as [CRITICAL] — the issue would fall back to the default repository, which may be incorrect.");
        sb.AppendLine("- Flag any sub-issue that clearly belongs in a specific repository but lacks a `targetRepository` field as [WARNING].");

        return sb.ToString();
    }
}
