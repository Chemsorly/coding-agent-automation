using System.Text;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services.Prompts;

/// <summary>
/// Phased refactoring detection prompt builders.
/// Splits the monolithic refactoring prompt into focused phases:
/// Phase 0 (context extraction), Phase 1 (3 parallel sub-agents),
/// Phase 2 (aggregation), and strengthened adversarial review.
/// </summary>
public static partial class ConsolidationPromptBuilder
{
    // ─────────────────────────────────────────────────────────────────────
    //  Shared preamble injected at the TOP of every sub-agent prompt.
    //  Exploits primacy bias (arXiv:2307.03172 "lost in the middle").
    // ─────────────────────────────────────────────────────────────────────

    private static string BuildRefactoringSubAgentPreamble() =>
"""
## CRITICAL RULES — Read First

1. **Evidence over speculation.** Every finding must cite a specific file path + line number or code snippet. "This looks complex" is not a finding.
2. **Tool augmentation encouraged.** If the ecosystem has static analysis tools (linters, compilers with warning output, dead-code detectors), install and run them. Their output is higher-confidence evidence than your own code reading. You are allowed to install tools.
3. **Declare what you did NOT check.** At the end of your output, list files/areas you skipped due to context limits.
4. **Reasoning length scales with severity.** 1-2 sentences for low-impact observations. 4-6 sentences with full evidence chain for high-impact findings.
5. **Do NOT modify source code.** Only produce the findings output file.

""";

    // ─────────────────────────────────────────────────────────────────────
    //  Phase 0: Context Extraction
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Phase 0 prompt: extracts project conventions, layer rules, and intentional
    /// design choices. Output grounds subsequent phases — prevents flagging
    /// idiomatic patterns as smells (SmellBench: 63% FP rate without context).
    /// </summary>
    public static string BuildRefactoringContextExtractionPrompt()
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Phase 0: Project Context Extraction");
        sb.AppendLine();
        sb.AppendLine("You are extracting the project's own conventions and design philosophy.");
        sb.AppendLine("This context will be given to downstream analysis agents to prevent false positives.");
        sb.AppendLine("Your goal: define what THIS project considers correct, not what textbooks say.");
        sb.AppendLine();

        sb.AppendLine("## What to Read");
        sb.AppendLine();
        sb.AppendLine("1. **README.md** and any top-level documentation");
        sb.AppendLine("2. **Architecture docs** in `docs/` or `.brain/` if present");
        sb.AppendLine("3. **Solution structure** — list all projects, identify layers and their intended relationships");
        sb.AppendLine("4. **Key configuration files** — `.editorconfig`, `Directory.Build.props`, linter configs");
        sb.AppendLine("5. **A sample of 5-10 representative source files** — identify the project's actual style");
        sb.AppendLine("6. **Test project structure** — understand the testing philosophy");
        sb.AppendLine();
        sb.AppendLine("## What to Extract");
        sb.AppendLine();
        sb.AppendLine("Produce a JSON file at `.agent/refactoring-conventions.json` with this structure:");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"techStack\": \"e.g., .NET 9, ASP.NET Core, Blazor Server, EF Core\",");
        sb.AppendLine("  \"projectStructure\": [");
        sb.AppendLine("    { \"name\": \"ProjectName\", \"layer\": \"domain|application|infrastructure|presentation\", \"purpose\": \"brief\" }");
        sb.AppendLine("  ],");
        sb.AppendLine("  \"namingConventions\": {");
        sb.AppendLine("    \"classes\": \"e.g., PascalCase, suffix Services with 'Service'\",");
        sb.AppendLine("    \"interfaces\": \"e.g., prefix with I\",");
        sb.AppendLine("    \"files\": \"e.g., one class per file, match class name\"");
        sb.AppendLine("  },");
        sb.AppendLine("  \"intentionalPatterns\": [");
        sb.AppendLine("    \"Description of patterns that look unusual but are intentional — do NOT flag these\"");
        sb.AppendLine("  ],");
        sb.AppendLine("  \"abstractionPhilosophy\": \"e.g., 'minimal interfaces, prefer concrete unless tested in isolation'\",");
        sb.AppendLine("  \"testingPhilosophy\": \"e.g., 'unit tests for logic, E2E for integration, no mocks for simple classes'\",");
        sb.AppendLine("  \"knownDebt\": [");
        sb.AppendLine("    \"Acknowledged technical debt the team is aware of — do NOT re-flag\"");
        sb.AppendLine("  ],");
        sb.AppendLine("  \"layerRules\": [");
        sb.AppendLine("    \"e.g., 'Infrastructure must not reference Presentation'\",");
        sb.AppendLine("    \"e.g., 'Agent projects communicate only through interfaces in Pipeline'\"");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Rules");
        sb.AppendLine();
        sb.AppendLine("- **Observe, don't judge.** This phase extracts what IS, not what should be.");
        sb.AppendLine("- **intentionalPatterns is critical.** If a pattern looks unusual but is clearly deliberate");
        sb.AppendLine("  (used consistently, matches docs/comments, has tests), list it here.");
        sb.AppendLine("- **knownDebt** should include anything acknowledged in TODOs, READMEs, or issue trackers.");
        sb.AppendLine("- If `.brain/` exists, consult project SKILL.md files — they contain curated context.");
        sb.AppendLine("- Keep the output concise. Each field should be 1-3 sentences, not paragraphs.");

        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Phase 1, Agent A: Structural Debt
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Agent A prompt: focused on duplicated logic, structural drift, complexity,
    /// and over-engineering. Receives hotspot data and Phase 0 conventions.
    /// </summary>
    public static string BuildRefactoringStructuralDebtPrompt()
    {
        var sb = new StringBuilder();

        sb.Append(BuildRefactoringSubAgentPreamble());

        sb.AppendLine("# Agent A: Structural Debt Detection");
        sb.AppendLine();
        sb.AppendLine("You are one of three parallel analysis agents. Your focus is **structural debt** —");
        sb.AppendLine("patterns where incremental changes have created global incoherence.");
        sb.AppendLine();
        sb.AppendLine("## Your Categories");
        sb.AppendLine();
        sb.AppendLine("1. **Duplicated logic** — Similar code patterns repeated across multiple files that could be extracted.");
        sb.AppendLine("   Look for: near-identical method bodies, copy-pasted error handling, repeated validation logic,");
        sb.AppendLine("   similar DTO transformations in multiple locations.");
        sb.AppendLine();
        sb.AppendLine("2. **Structural drift** — Areas where the architecture has diverged from the intended design.");
        sb.AppendLine("   Consult `.agent/refactoring-conventions.json` for layer rules. Look for: imports crossing");
        sb.AppendLine("   layer boundaries, services doing work outside their responsibility, components that grew");
        sb.AppendLine("   beyond their original scope.");
        sb.AppendLine();
        sb.AppendLine("3. **Overly complex areas** — Methods or classes that have grown too large or have too many responsibilities.");
        sb.AppendLine("   Metrics: methods >50 lines, classes >500 lines, constructors with >6 parameters,");
        sb.AppendLine("   methods with >4 levels of nesting. Focus on hotspot files — complexity in rarely-touched code");
        sb.AppendLine("   is low priority.");
        sb.AppendLine();
        sb.AppendLine("4. **Over-engineering & unnecessary abstraction** — Interfaces with only one implementation that add");
        sb.AppendLine("   indirection without value; wrapper classes that pass-through without logic; factory/builder");
        sb.AppendLine("   patterns where a constructor would suffice; configuration options nobody uses.");
        sb.AppendLine("   **Check `.agent/refactoring-conventions.json` → `intentionalPatterns` before flagging.**");
        sb.AppendLine("   If the project's philosophy is \"minimal interfaces\", a missing interface is NOT a finding.");
        sb.AppendLine();
        sb.AppendLine("## Exploration Strategy");
        sb.AppendLine();
        sb.AppendLine("1. Read `.agent/hotspot-analysis.txt` — start with the top 15 most-changed files");
        sb.AppendLine("2. Read `.agent/refactoring-conventions.json` — understand what's intentional vs accidental");
        sb.AppendLine("3. For each hotspot file: read it, assess structural health against the 4 categories above");
        sb.AppendLine("4. Then read 5 files NOT in the hotspot list (stable but potentially problematic)");
        sb.AppendLine("5. For duplication detection: when you find a pattern in one file, grep for similar patterns elsewhere");
        sb.AppendLine();
        sb.AppendLine("## Output Format");
        sb.AppendLine();
        sb.AppendLine($"Write findings to `{AgentWorkspacePaths.RefactoringStructuralFindingsFilePath}` as a JSON array:");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("[");
        sb.AppendLine("  {");
        sb.AppendLine("    \"title\": \"Short descriptive title\",");
        sb.AppendLine("    \"category\": \"duplication|structural-drift|complexity|over-engineering\",");
        sb.AppendLine("    \"affectedFiles\": [\"src/path/to/File.cs\"],");
        sb.AppendLine("    \"evidence\": \"Concrete code snippet or line reference proving the issue\",");
        sb.AppendLine("    \"evidenceSources\": [\"code-reading:File.cs:L42\", \"hotspot:18-changes\", \"tool:eslint-unused-vars\"],");
        sb.AppendLine("    \"crossReference\": \"Second file/location that corroborates (duplication partner, drift boundary, etc.)\",");
        sb.AppendLine("    \"impact\": \"What goes wrong because of this — be specific\",");
        sb.AppendLine("    \"suggestedFix\": \"Brief approach, not full implementation\"");
        sb.AppendLine("  }");
        sb.AppendLine("]");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Quality Bar");
        sb.AppendLine();
        sb.AppendLine("- Every finding MUST have `crossReference` — a second location proving the issue isn't isolated.");
        sb.AppendLine("  For duplication: the other copy. For drift: the layer rule violated + the import. For complexity: the callers affected.");
        sb.AppendLine("- Findings about over-engineering require proof the abstraction is never extended: check all implementations");
        sb.AppendLine("  of the interface, check test mocks, check git history for attempts to add implementations.");
        sb.AppendLine("- **Do NOT flag patterns listed in `intentionalPatterns`.** If unsure, skip it.");
        sb.AppendLine("- Prefer fewer high-quality findings over many shallow ones. Maximum 10 findings.");

        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Phase 1, Agent B: Correctness & Hygiene
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Agent B prompt: focused on TODOs, dead code, obvious bugs, and stale documentation.
    /// Uses enumerate-then-verify pattern — deterministic enumeration first, then assessment.
    /// </summary>
    public static string BuildRefactoringCorrectnessPrompt()
    {
        var sb = new StringBuilder();

        sb.Append(BuildRefactoringSubAgentPreamble());

        sb.AppendLine("# Agent B: Correctness & Hygiene Detection");
        sb.AppendLine();
        sb.AppendLine("You are one of three parallel analysis agents. Your focus is **correctness and hygiene** —");
        sb.AppendLine("concrete issues that are wrong, dead, or misleading.");
        sb.AppendLine();
        sb.AppendLine("## Your Categories");
        sb.AppendLine();
        sb.AppendLine("1. **TODO/HACK/FIXME comments** — Left by previous work, indicating incomplete implementation.");
        sb.AppendLine("   Not all TODOs are actionable — only flag ones that indicate a real gap or risk.");
        sb.AppendLine();
        sb.AppendLine("2. **Dead code & unused artifacts** — Unreferenced methods, classes, interfaces, or files.");
        sb.AppendLine("   Includes: unused using directives beyond IDE cleanup, orphaned files from removed features,");
        sb.AppendLine("   parameters that are never used, private methods never called.");
        sb.AppendLine();
        sb.AppendLine("3. **Obvious bugs** — High-confidence correctness issues ONLY. You must be certain the code is wrong:");
        sb.AppendLine("   null dereference after a code path that doesn't guarantee non-null, off-by-one in boundary");
        sb.AppendLine("   checks, unreachable code paths (dead branches), resource leaks (opened but never disposed),");
        sb.AppendLine("   logic errors where conditions are always true/false, race conditions in shared mutable state.");
        sb.AppendLine("   **Do NOT flag \"potential\" issues you're unsure about.** Only high-confidence bugs.");
        sb.AppendLine();
        sb.AppendLine("4. **Stale documentation & misleading comments** — XML doc comments describing behavior the code");
        sb.AppendLine("   no longer exhibits; README sections referencing removed features; comments explaining \"why\"");
        sb.AppendLine("   that reference conditions no longer true; parameter descriptions that don't match signatures.");
        sb.AppendLine();
        sb.AppendLine("## Exploration Strategy: Enumerate Then Verify");
        sb.AppendLine();
        sb.AppendLine("Research shows LLM agents miss absences when scanning for bad patterns.");
        sb.AppendLine("Flip the approach: enumerate what exists, then verify each item.");
        sb.AppendLine();
        sb.AppendLine("**For TODOs/HACKs/FIXMEs:**");
        sb.AppendLine("1. Search the codebase: grep/search for `TODO`, `HACK`, `FIXME`, `XXX`, `WORKAROUND`");
        sb.AppendLine("2. For each result: read the surrounding context and assess if it indicates a real gap");
        sb.AppendLine("3. Discard TODOs that are aspirational (\"TODO: nice to have\") — keep ones indicating broken/incomplete behavior");
        sb.AppendLine();
        sb.AppendLine("**For dead code:**");
        sb.AppendLine("1. If static analysis tools are available for this ecosystem, install and run them to detect unused code.");
        sb.AppendLine("   This is the highest-confidence approach. Common tools: `dotnet build` warnings (CS0219, IDE0051),");
        sb.AppendLine("   `eslint --rule no-unused-vars`, `pylint`, `deadcode`, etc.");
        sb.AppendLine("2. If no tools available: enumerate public types/methods in key files, then search for their usages.");
        sb.AppendLine("   A public method with zero callers outside its own class is a dead code candidate.");
        sb.AppendLine("3. Check git history for recently-deleted features — their support code may linger.");
        sb.AppendLine();
        sb.AppendLine("**For bugs:**");
        sb.AppendLine("1. Focus on hotspot files (high churn = more likely to contain recent regressions)");
        sb.AppendLine("2. Read error handling paths specifically — bugs hide in catch blocks and edge cases");
        sb.AppendLine("3. Check null safety: follow nullable references through code paths and verify guards exist");
        sb.AppendLine();
        sb.AppendLine("**For stale docs:**");
        sb.AppendLine("1. Read method signatures, then read their XML doc comments — do they match?");
        sb.AppendLine("2. Check README.md for references to files/features that no longer exist");
        sb.AppendLine("3. Check inline comments that reference specific behavior — verify the behavior still exists");
        sb.AppendLine();
        sb.AppendLine("## Output Format");
        sb.AppendLine();
        sb.AppendLine($"Write findings to `{AgentWorkspacePaths.RefactoringCorrectnessFindingsFilePath}` as a JSON array:");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("[");
        sb.AppendLine("  {");
        sb.AppendLine("    \"title\": \"Short descriptive title\",");
        sb.AppendLine("    \"category\": \"todo|dead-code|bug|stale-documentation\",");
        sb.AppendLine("    \"affectedFiles\": [\"src/path/to/File.cs\"],");
        sb.AppendLine("    \"evidence\": \"The exact code snippet or comment text proving the issue\",");
        sb.AppendLine("    \"evidenceSources\": [\"grep:TODO:File.cs:L15\", \"tool:IDE0051\", \"usage-search:zero-callers\"],");
        sb.AppendLine("    \"crossReference\": \"For dead code: proof of zero callers. For bugs: the code path that triggers it. For stale docs: the actual behavior vs documented behavior.\",");
        sb.AppendLine("    \"impact\": \"What goes wrong or what cognitive cost this imposes\",");
        sb.AppendLine("    \"suggestedFix\": \"Brief approach\"");
        sb.AppendLine("  }");
        sb.AppendLine("]");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Quality Bar");
        sb.AppendLine();
        sb.AppendLine("- **Dead code findings MUST include proof of zero usage** — either tool output or a usage search showing no callers.");
        sb.AppendLine("  Do NOT flag code as dead without searching for references. Reflection, DI registration, and test mocks can create invisible references.");
        sb.AppendLine("- **Bug findings MUST demonstrate a concrete failure scenario** — not \"this could fail\" but \"when X is null at L42, L47 dereferences it without a guard.\"");
        sb.AppendLine("- **TODO findings must include the surrounding context** — the comment alone is not enough. Show what's incomplete or broken.");
        sb.AppendLine("- **Stale doc findings must show both** the documented claim AND the actual code behavior side-by-side.");
        sb.AppendLine("- Findings sourced from deterministic tools (grep, linter, compiler warnings) are inherently higher quality.");
        sb.AppendLine("  Mark them in `evidenceSources` with a `tool:` prefix.");
        sb.AppendLine("- Maximum 10 findings. Prefer bugs > dead code > stale docs > TODOs (by impact).");

        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Phase 1, Agent C: Design Consistency
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Agent C prompt: focused on naming inconsistencies and primitive obsession.
    /// Heavily depends on Phase 0 conventions output to define what "consistent" means.
    /// </summary>
    public static string BuildRefactoringDesignConsistencyPrompt()
    {
        var sb = new StringBuilder();

        sb.Append(BuildRefactoringSubAgentPreamble());

        sb.AppendLine("# Agent C: Design Consistency Detection");
        sb.AppendLine();
        sb.AppendLine("You are one of three parallel analysis agents. Your focus is **design consistency** —");
        sb.AppendLine("patterns where naming, typing, or API shape deviates from the project's own conventions.");
        sb.AppendLine();
        sb.AppendLine("**This agent depends heavily on `.agent/refactoring-conventions.json`.** Read it first.");
        sb.AppendLine("Your job is to find deviations from the project's OWN standards, not generic best practices.");
        sb.AppendLine();
        sb.AppendLine("## Your Categories");
        sb.AppendLine();
        sb.AppendLine("1. **Naming inconsistencies** — Classes, methods, or variables that don't follow the project's naming conventions.");
        sb.AppendLine("   Use `namingConventions` from conventions.json as your reference. Examples:");
        sb.AppendLine("   - Service classes without the expected suffix (e.g., `FooHandler` when convention is `FooService`)");
        sb.AppendLine("   - Interfaces that don't follow the prefix/suffix pattern");
        sb.AppendLine("   - Files whose names don't match their primary class");
        sb.AppendLine("   - Methods using different verb patterns than the rest of the codebase (e.g., `Fetch` vs `Get` vs `Load`)");
        sb.AppendLine("   - Inconsistent casing in specific contexts (event names, configuration keys, JSON properties)");
        sb.AppendLine();
        sb.AppendLine("2. **Primitive obsession** — Using strings, ints, or raw types to represent domain concepts.");
        sb.AppendLine("   Look for:");
        sb.AppendLine("   - String parameters representing structured data (emails, URLs, IDs, file paths) without validation");
        sb.AppendLine("   - Magic numbers/strings without named constants — especially repeated across multiple files");
        sb.AppendLine("   - Repeated validation logic for the same concept in multiple call sites");
        sb.AppendLine("   - Method signatures with multiple same-typed parameters that could be confused (e.g., `void Move(string from, string to)`)");
        sb.AppendLine("   - Enums that should be polymorphic types (switch statements over the same enum in many places)");
        sb.AppendLine();
        sb.AppendLine("## Exploration Strategy");
        sb.AppendLine();
        sb.AppendLine("**For naming inconsistencies:**");
        sb.AppendLine("1. Read `namingConventions` from conventions.json — this IS the truth");
        sb.AppendLine("2. Enumerate class/interface names across projects (list files, read declarations)");
        sb.AppendLine("3. For each naming convention rule: verify compliance across a representative sample");
        sb.AppendLine("4. Focus on PUBLIC API surface — internal inconsistencies matter less");
        sb.AppendLine("5. Only flag patterns that appear more than once — a single oddly-named class might be intentional");
        sb.AppendLine();
        sb.AppendLine("**For primitive obsession:**");
        sb.AppendLine("1. Look at method signatures in service interfaces — these define the API contracts");
        sb.AppendLine("2. Search for repeated string-typed parameters with the same name across different methods");
        sb.AppendLine("   (e.g., `string repositoryUrl` appearing in 5+ method signatures = candidate for a value type)");
        sb.AppendLine("3. Search for magic strings/numbers: look for string literals and numeric constants used in");
        sb.AppendLine("   conditional logic. If the same literal appears in 3+ places, it should be a constant or enum.");
        sb.AppendLine("4. Check switch statements over enums — if the same enum is switched over in 4+ locations,");
        sb.AppendLine("   it may be a candidate for polymorphism (but check `intentionalPatterns` first).");
        sb.AppendLine();
        sb.AppendLine("## Output Format");
        sb.AppendLine();
        sb.AppendLine($"Write findings to `{AgentWorkspacePaths.RefactoringDesignFindingsFilePath}` as a JSON array:");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("[");
        sb.AppendLine("  {");
        sb.AppendLine("    \"title\": \"Short descriptive title\",");
        sb.AppendLine("    \"category\": \"naming-inconsistency|primitive-obsession\",");
        sb.AppendLine("    \"affectedFiles\": [\"src/path/to/File.cs\"],");
        sb.AppendLine("    \"evidence\": \"The specific naming deviation or primitive usage with concrete examples\",");
        sb.AppendLine("    \"evidenceSources\": [\"convention-rule:services-suffix\", \"grep:repositoryUrl:5-occurrences\"],");
        sb.AppendLine("    \"crossReference\": \"For naming: the convention rule violated + examples of correct naming elsewhere. For primitives: multiple locations using the same raw type for the same concept.\",");
        sb.AppendLine("    \"impact\": \"Cognitive cost, confusion risk, or bug risk from the inconsistency\",");
        sb.AppendLine("    \"suggestedFix\": \"Brief approach — rename to X, introduce value type Y, extract constant Z\"");
        sb.AppendLine("  }");
        sb.AppendLine("]");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Quality Bar");
        sb.AppendLine();
        sb.AppendLine("- **Naming findings require a convention rule reference.** \"This name seems odd\" is not a finding.");
        sb.AppendLine("  \"Convention says services end with 'Service' but `FooHandler` doesn't follow this\" IS a finding.");
        sb.AppendLine("- **Primitive obsession findings require 3+ occurrences.** A single string parameter is not primitive obsession.");
        sb.AppendLine("  The same concept passed as raw string through 3+ call sites IS primitive obsession.");
        sb.AppendLine("- **Do NOT flag naming in test projects** unless conventions.json explicitly covers test naming.");
        sb.AppendLine("- **Do NOT flag names that match `intentionalPatterns`** from conventions.json.");
        sb.AppendLine("- This agent has the highest false-positive risk. Be conservative. Maximum 8 findings.");

        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Phase 2: Aggregation & Prioritization
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Phase 2 prompt: aggregates findings from all three sub-agents, deduplicates,
    /// filters against project conventions, and produces final ranked proposals.
    /// </summary>
    public static string BuildRefactoringAggregationPrompt(
        int maxProposals = 3,
        string? issueContext = null,
        string? outcomeContext = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Phase 2: Aggregation & Prioritization");
        sb.AppendLine();
        sb.AppendLine("You are the orchestrator synthesizing findings from three parallel analysis agents");
        sb.AppendLine("into final, actionable refactoring proposals. Your job is quality control and ranking —");
        sb.AppendLine("not discovery. Do NOT add new findings; only curate what the agents produced.");
        sb.AppendLine();

        sb.AppendLine("## Inputs");
        sb.AppendLine();
        sb.AppendLine("Read ALL of the following files:");
        sb.AppendLine($"1. `{AgentWorkspacePaths.RefactoringStructuralFindingsFilePath}` — Agent A: structural debt");
        sb.AppendLine($"2. `{AgentWorkspacePaths.RefactoringCorrectnessFindingsFilePath}` — Agent B: correctness & hygiene");
        sb.AppendLine($"3. `{AgentWorkspacePaths.RefactoringDesignFindingsFilePath}` — Agent C: design consistency");
        sb.AppendLine($"4. `{AgentWorkspacePaths.RefactoringConventionsFilePath}` — Phase 0: project conventions");
        sb.AppendLine($"5. `{AgentWorkspacePaths.HotspotAnalysisFilePath}` — Git hotspot data");
        sb.AppendLine();

        sb.AppendLine("## Aggregation Steps");
        sb.AppendLine();
        sb.AppendLine("### Step 1: Deduplicate");
        sb.AppendLine();
        sb.AppendLine("Multiple agents may flag the same file or overlapping concerns:");
        sb.AppendLine("- If two findings reference the same file with related issues → merge into one proposal");
        sb.AppendLine("- If a structural finding and a correctness finding describe the same root cause → keep the one with stronger evidence");
        sb.AppendLine("- Preserve the best `evidence` and `crossReference` from both when merging");
        sb.AppendLine();
        sb.AppendLine("### Step 2: Filter Against Conventions");
        sb.AppendLine();
        sb.AppendLine("For each remaining finding, check against `refactoring-conventions.json`:");
        sb.AppendLine("- Does it flag something listed in `intentionalPatterns`? → **DROP IT**");
        sb.AppendLine("- Does it flag something listed in `knownDebt`? → **DROP IT** (team already knows)");
        sb.AppendLine("- Does it contradict `abstractionPhilosophy`? (e.g., flagging missing interface when philosophy says \"minimal interfaces\") → **DROP IT**");
        sb.AppendLine("- Does it contradict `testingPhilosophy`? → **DROP IT**");
        sb.AppendLine();
        sb.AppendLine("### Step 3: Rank by Impact");
        sb.AppendLine();
        sb.AppendLine("Score each surviving finding on three axes (each 1-3):");
        sb.AppendLine();
        sb.AppendLine("| Axis | 3 (high) | 2 (medium) | 1 (low) |");
        sb.AppendLine("|------|----------|------------|---------|");
        sb.AppendLine("| **Hotspot frequency** | File in top 10 hotspots | File in top 11-20 | Not in hotspot list |");
        sb.AppendLine("| **Evidence strength** | Tool-confirmed or multi-source | Code reading with crossReference | Single observation |");
        sb.AppendLine("| **Scope feasibility** | <10 files affected | 10-20 files | 20-30 files |");
        sb.AppendLine();
        sb.AppendLine("Final score = hotspot × evidence × scope. Rank descending. Take top N.");
        sb.AppendLine();
        sb.AppendLine("### Step 3.5: Evidence Quality Gate");
        sb.AppendLine();
        // TODO: "Before ranking" wording is inconsistent with placement after Step 3's "Rank descending. Take top N."
        // Consider rewording to "After scoring but before final selection" to avoid contradictory instructions.
        sb.AppendLine("Before ranking, reject proposals that fail evidence quality:");
        sb.AppendLine("- Categories `refactoring`, `bug`, `dead-code`: MUST have at least one evidence source");
        sb.AppendLine("  that is NOT \"code-reading:\" only. If all sources are \"code-reading:\", DROP the proposal.");
        sb.AppendLine("- Categories `simplification`, `documentation`: may use \"code-reading:\" alone but");
        sb.AppendLine("  receive a capped evidence score of 1 regardless of other evidence quality.");
        sb.AppendLine("- Valid non-code-reading sources: \"hotspot:\", \"grep:\", \"usage-search:\", \"tool:\"");
        sb.AppendLine();
        sb.AppendLine("### Step 4: Format as Proposals");
        sb.AppendLine();
        sb.AppendLine($"Select the top **{maxProposals}** findings by score and convert them into the final proposal format.");
        sb.AppendLine();

        // Insert issue context (open issues to avoid duplicating)
        if (!string.IsNullOrEmpty(issueContext))
        {
            sb.Append(issueContext);
            sb.AppendLine();
        }

        // Insert outcome context (past implemented/rejected)
        if (!string.IsNullOrEmpty(outcomeContext))
        {
            sb.Append(outcomeContext);
            sb.AppendLine();
        }

        sb.AppendLine("## Output Format");
        sb.AppendLine();
        sb.AppendLine($"Produce the final proposals at `{AgentWorkspacePaths.RefactoringProposalsFilePath}` as a JSON array:");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("[");
        sb.AppendLine("  {");
        sb.AppendLine("    \"title\": \"Short descriptive title of the refactoring opportunity\",");
        sb.AppendLine("    \"category\": \"refactoring|simplification|bug|documentation|dead-code\",");
        sb.AppendLine("    \"affectedFiles\": [\"src/path/to/File1.cs\", \"src/path/to/File2.cs\"],");
        sb.AppendLine("    \"description\": \"Detailed description of what should be changed and how\",");
        sb.AppendLine("    \"rationale\": \"Why — referencing concrete evidence from the sub-agent findings\",");
        sb.AppendLine("    \"evidenceSources\": [\"tool:IDE0051\", \"hotspot:18-changes\", \"code-reading:File.cs:L42\"],");
        sb.AppendLine("    \"prerequisites\": [\"Add characterization tests for X before refactoring\"],");
        sb.AppendLine("    \"dependsOn\": [\"Exact title of another proposal this depends on\"],");
        sb.AppendLine("    \"estimatedEffort\": \"small|medium|large\",");
        sb.AppendLine("    \"riskLevel\": \"low|medium|high\",");
        sb.AppendLine("    \"technique\": \"Extract Method|Inline Class|Rename|Introduce Value Type|etc.\"");
        sb.AppendLine("  }");
        sb.AppendLine("]");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### Field Definitions");
        sb.AppendLine();
        sb.AppendLine("- **evidenceSources** (required) — list of evidence that supports this proposal. Prefix with type:");
        sb.AppendLine("  `tool:` (linter/compiler output), `hotspot:` (git frequency), `code-reading:` (manual inspection),");
        sb.AppendLine("  `grep:` (pattern search), `usage-search:` (reference count). Multi-source proposals are higher quality.");
        sb.AppendLine("- **prerequisites** — prep work needed. If affected files lack test coverage, MUST include");
        sb.AppendLine("  \"Add characterization tests for X before refactoring\". Do NOT reference other proposals by number");
        sb.AppendLine("  (e.g., \"proposal #1\") — GitHub will autolink #N to wrong issues.");
        sb.AppendLine("- **dependsOn** — titles of other proposals in this batch that must be completed first.");
        sb.AppendLine("  Use the EXACT title string of the dependency. These are resolved to `Depends on #N` during issue creation.");
        sb.AppendLine("  Do NOT use `#N` notation anywhere — it creates wrong GitHub autolinks.");
        sb.AppendLine("- **estimatedEffort** — `small` (<5 files), `medium` (5-15 files), `large` (15-30 files).");
        sb.AppendLine("- **riskLevel** — `low` (rename/move), `medium` (extract/restructure), `high` (interface changes).");
        sb.AppendLine("- **technique** — named refactoring pattern if applicable.");
        sb.AppendLine();
        sb.AppendLine("## Scope Constraints");
        sb.AppendLine();
        sb.AppendLine("Each proposal MUST be achievable by a single agent in one run:");
        sb.AppendLine("- Maximum ~30 affected files (source + test) per proposal");
        sb.AppendLine("- If a finding would touch more files, split into independent phases");
        sb.AppendLine("- Each phase must leave the codebase buildable");
        sb.AppendLine("- Prefer mechanical, low-risk changes over sweeping architectural ones");
        sb.AppendLine("- Do NOT propose changes spanning serialization boundaries simultaneously");
        sb.AppendLine();
        sb.AppendLine("## Dependency Ordering");
        sb.AppendLine();
        sb.AppendLine("When splitting work into phases, express ordering via `dependsOn`:");
        sb.AppendLine("- List proposals in dependency order: independent proposals first, dependent ones later");
        sb.AppendLine("- If proposal B requires proposal A to be completed first, add A's EXACT title to B's `dependsOn` array");
        sb.AppendLine("- Do NOT use `#N`, `proposal #1`, or any numeric issue references in any text field");
        sb.AppendLine("- The system resolves title references to proper GitHub issue links during creation");
        sb.AppendLine();
        sb.AppendLine("## Also Produce");
        sb.AppendLine();
        sb.AppendLine("Write a brief analysis log at `.agent/refactoring-analysis.md` containing:");
        sb.AppendLine("- Total findings received from agents A, B, C");
        sb.AppendLine("- How many were dropped (duplicates, convention-filtered, scope-exceeded)");
        sb.AppendLine("- The ranking scores for the top candidates");
        sb.AppendLine("- Which findings were dropped and why (one line each)");

        return sb.ToString();
    }
}
