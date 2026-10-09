using System.Text;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services.Prompts;

/// <summary>
/// Phased refactoring detection prompt builders.
/// Splits the monolithic refactoring prompt into focused phases:
/// Phase 0 (context extraction), Phase 1 (3 parallel sub-agents),
/// Phase 2 (aggregation), and strengthened adversarial review.
/// </summary>
public static partial class ConsolidationPromptBuilder
{
    private const string JsonCodeFence = "```json";

    // ─────────────────────────────────────────────────────────────────────
    //  Shared preamble injected at the TOP of every sub-agent prompt.
    //  Exploits primacy bias (arXiv:2307.03172 "lost in the middle").
    // ─────────────────────────────────────────────────────────────────────

    private const string RefactoringSubAgentPreamble =
$"""
## CRITICAL RULES — Read First

1. **Evidence over speculation.** Every finding must cite a specific file path + line number or code snippet. "This looks complex" is not a finding.
2. **Use the available tools.** Phase 0 listed them under `availableTools` in `{AgentWorkspacePaths.RefactoringConventionsFilePath}` and saved the output of the project's analyzers in `{AgentWorkspacePaths.RefactoringToolOutputDirectory}/`. Tool output is higher-confidence evidence than your own code reading. Query the listed MCP tools for data about the code you examine. Do NOT run builds or other commands that write build output: the other two agents work in this workspace at the same time. Read-only analyzers that do not build are fine, and you are allowed to install them.
3. **Declare what you did NOT check.** List files/areas you skipped due to context limits in the `notChecked` field of your output.
4. **Reasoning length scales with severity.** 1-2 sentences for low-impact observations. 4-6 sentences with full evidence chain for high-impact findings.
5. **Do NOT modify source code.** Only produce the findings output file.
6. **Out of scope:** `{AgentWorkspacePaths.MetadataDirectory}/`, `{AgentWorkspacePaths.BrainDirectory}/`, `.git/`, generated code, and files ignored by git. These are pipeline scratch space or tooling, not project code. Never report findings in them.
7. **Tag evidence honestly.** Each `evidenceSources` entry names how you got the evidence:
   - `tool:<tool>:<rule-id>` — output of a compiler, linter, analyzer or MCP tool that actually ran (by you or Phase 0). Nothing else is `tool:`.
   - `grep:<pattern>` — a text search, with the pattern you searched for.
   - `usage-search:<symbol>` — a reference search, with the result (e.g. `usage-search:FooService.Bar:0-callers`).
   - `code-reading:<file>:L<line>` — your own reading of the code.
   - `hotspot:<file>` — change frequency. This says how often a file changes, not that anything is wrong with it.

""";

    // ─────────────────────────────────────────────────────────────────────
    //  Phase 0: Context Extraction
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Phase 0 prompt: extracts project conventions, layer rules, and intentional
    /// design choices. Output grounds subsequent phases — prevents flagging
    /// idiomatic patterns as smells (SmellBench: 63% of detected smells were false positives).
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
        sb.AppendLine("4. **Key configuration files** — `.editorconfig`, `Directory.Build.props`, `package.json`, `pyproject.toml`, linter configs");
        sb.AppendLine("5. **A sample of 5-10 representative source files** — identify the project's actual style");
        sb.AppendLine("6. **Test project structure** — understand the testing philosophy");
        sb.AppendLine();
        sb.AppendLine("## Evidence Sources");
        sb.AppendLine();
        sb.AppendLine("Three detection agents run after you, at the same time, in this workspace. Prepare the tool evidence");
        sb.AppendLine("they will use, so they do not each run builds:");
        sb.AppendLine();
        sb.AppendLine("1. **MCP tools.** Check your available MCP tools for additional data sources — for example code quality or");
        sb.AppendLine("   static analysis services, issue trackers, error tracking or observability. Note the ones that hold data");
        sb.AppendLine("   about this repository and what each reports.");
        sb.AppendLine("2. **The project's own analyzers.** Find the build, lint and analysis commands the project uses (CI workflows,");
        sb.AppendLine("   build scripts, README). Run the ones that report warnings without changing tracked files, once, and save");
        sb.AppendLine($"   each raw output to `{AgentWorkspacePaths.RefactoringToolOutputDirectory}/<name>.txt`. You may install tools.");
        sb.AppendLine("3. List both under `availableTools` below. If a source fails or needs credentials you do not have, skip it");
        sb.AppendLine("   and say why in its `reports` field.");
        sb.AppendLine();
        sb.AppendLine("## What to Extract");
        sb.AppendLine();
        sb.AppendLine($"Produce a JSON file at `{AgentWorkspacePaths.RefactoringConventionsFilePath}` with this structure:");
        sb.AppendLine();
        sb.AppendLine(JsonCodeFence);
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
        sb.AppendLine("  ],");
        sb.AppendLine("  \"availableTools\": [");
        sb.AppendLine($"    {{ \"name\": \"e.g., build-warnings\", \"kind\": \"command|mcp\", \"how\": \"the command, or the MCP server and tool\", \"output\": \"{AgentWorkspacePaths.RefactoringToolOutputDirectory}/build-warnings.txt, or null for MCP tools\", \"reports\": \"what it reports, or why it was skipped\" }}");
        sb.AppendLine("  ]");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Rules");
        sb.AppendLine();
        sb.AppendLine("- **Observe, don't judge.** This phase extracts what IS, not what should be.");
        sb.AppendLine("- **intentionalPatterns is critical.** If a pattern looks unusual but is clearly deliberate");
        sb.AppendLine("  (used consistently, matches docs/comments, has tests), list it here.");
        sb.AppendLine("- **knownDebt** lists debt the team acknowledged in documentation, decision records, the brain, or the issue tracker.");
        sb.AppendLine("  Do NOT copy inline TODO/FIXME/HACK comments into knownDebt — a later agent checks each of those against the current code.");
        sb.AppendLine($"- Describe the project code only. Do NOT describe `{AgentWorkspacePaths.MetadataDirectory}/` or `{AgentWorkspacePaths.BrainDirectory}/` — they are pipeline scratch space, not project code.");
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

        sb.Append(RefactoringSubAgentPreamble);

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
        sb.AppendLine("4. **Over-engineering & unnecessary abstraction** — Wrapper classes that pass through without logic;");
        sb.AppendLine("   factory/builder patterns where a constructor would suffice; configuration options nobody uses.");
        sb.AppendLine("   **This category has the highest false-positive rate in published benchmarks.** An interface with one");
        sb.AppendLine("   production implementation is NOT a finding when it is registered for dependency injection, has test");
        sb.AppendLine("   doubles (mocks or fakes in test projects), or is the seam across a project boundary.");
        sb.AppendLine("   **Check `.agent/refactoring-conventions.json` → `intentionalPatterns` before flagging.**");
        sb.AppendLine("   If the project's philosophy is \"minimal interfaces\", a missing interface is NOT a finding.");
        sb.AppendLine();
        sb.AppendLine("## Exploration Strategy");
        sb.AppendLine();
        sb.AppendLine($"1. Read `{AgentWorkspacePaths.HotspotAnalysisFilePath}` — start with the top 15 most-changed files");
        sb.AppendLine("2. Read `.agent/refactoring-conventions.json` — understand what's intentional vs accidental");
        sb.AppendLine("3. For each hotspot file: read it, assess structural health against the 4 categories above");
        sb.AppendLine("4. Then read 5 files NOT in the hotspot list (stable but potentially problematic)");
        sb.AppendLine("5. For duplication detection: when you find a pattern in one file, grep for similar patterns elsewhere");
        sb.AppendLine();

        AppendFindingsOutputFormat(sb, new FindingsOutputSpec
        {
            OutputPath = AgentWorkspacePaths.RefactoringStructuralFindingsFilePath,
            Categories = RefactoringCategories.Structural,
            EvidenceHint = "Concrete code snippet or line reference proving the issue",
            EvidenceSourcesExample = "\"code-reading:File.cs:L42\", \"grep:catch (Exception ex)\", \"tool:dotnet-build:CA1502\"",
            CrossReferenceHint = "Second file/location that corroborates (duplication partner, drift boundary, etc.)",
            ImpactHint = "What goes wrong because of this — be specific",
            SuggestedFixHint = "Brief approach, not full implementation"
        });

        sb.AppendLine("## Quality Bar");
        sb.AppendLine();
        sb.AppendLine("- Every finding MUST have `crossReference` — a second location proving the issue isn't isolated.");
        sb.AppendLine("  For duplication: the other copy. For drift: the layer rule violated + the import. For complexity: the callers affected.");
        sb.AppendLine("- For duplication, find EVERY copy, not just two: put the search that lists them in `scopeQuery`.");
        sb.AppendLine("- Findings about over-engineering require proof the abstraction is never extended: check all implementations");
        sb.AppendLine("  of the interface, check test mocks, check DI registrations, check git history for attempts to add implementations.");
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

        sb.Append(RefactoringSubAgentPreamble);

        sb.AppendLine("# Agent B: Correctness & Hygiene Detection");
        sb.AppendLine();
        sb.AppendLine("You are one of three parallel analysis agents. Your focus is **correctness and hygiene** —");
        sb.AppendLine("concrete issues that are wrong, dead, or misleading.");
        sb.AppendLine();
        sb.AppendLine("## Your Categories");
        sb.AppendLine();
        sb.AppendLine("1. **TODO/HACK/FIXME comments** — Left by previous work, indicating incomplete implementation.");
        sb.AppendLine("   Not all TODOs are actionable — only flag ones that indicate a real gap or risk.");
        sb.AppendLine("   Some TODOs are deferred review findings (e.g. `TODO [WARNING]`). Treat them like any other TODO:");
        sb.AppendLine("   verify the problem still exists in the current code, and drop it if the code already handles it.");
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
        sb.AppendLine($"1. Check the tool output in `{AgentWorkspacePaths.RefactoringToolOutputDirectory}/` and the MCP tools in `availableTools` for unused-code");
        sb.AppendLine("   reports — the highest-confidence approach. Common sources: compiler warnings (CS0219, IDE0051),");
        sb.AppendLine("   `eslint --rule no-unused-vars`, `pylint`, `deadcode`. If none covers unused code, you may install and run");
        sb.AppendLine("   a read-only analyzer that does not build.");
        sb.AppendLine("2. If no tools available: enumerate public types/methods in key files, then search for their usages.");
        sb.AppendLine("   A public method with zero callers outside its own class is a dead code candidate.");
        sb.AppendLine("3. Check git history for recently-deleted features — their support code may linger.");
        sb.AppendLine();
        sb.AppendLine("**For bugs:**");
        sb.AppendLine("1. Focus on hotspot files (high churn = more likely to contain recent regressions)");
        sb.AppendLine("2. Read error handling paths specifically — bugs hide in catch blocks and edge cases");
        sb.AppendLine("3. Check null safety: follow nullable references through code paths and verify guards exist");
        sb.AppendLine("4. When a bug is one instance of a pattern (the same faulty catch, guard or call in several places),");
        sb.AppendLine("   search for every instance and put the search in `scopeQuery`. A fix for half the instances is not a fix.");
        sb.AppendLine();
        sb.AppendLine("**For stale docs:**");
        sb.AppendLine("1. Read method signatures, then read their XML doc comments — do they match?");
        sb.AppendLine("2. Check README.md for references to files/features that no longer exist");
        sb.AppendLine("3. Check inline comments that reference specific behavior — verify the behavior still exists");
        sb.AppendLine();

        AppendFindingsOutputFormat(sb, new FindingsOutputSpec
        {
            OutputPath = AgentWorkspacePaths.RefactoringCorrectnessFindingsFilePath,
            Categories = RefactoringCategories.Correctness,
            EvidenceHint = "The exact code snippet or comment text proving the issue",
            EvidenceSourcesExample = "\"grep:TODO\", \"tool:dotnet-build:IDE0051\", \"usage-search:FooService.Bar:0-callers\"",
            CrossReferenceHint = "For dead code: proof of zero callers. For bugs: the code path that triggers it. For stale docs: the actual behavior vs documented behavior.",
            ImpactHint = "What goes wrong or what cognitive cost this imposes",
            SuggestedFixHint = "Brief approach"
        });

        sb.AppendLine("## Quality Bar");
        sb.AppendLine();
        sb.AppendLine("- **Dead code findings MUST include proof of zero usage** — either tool output or a usage search showing no callers.");
        sb.AppendLine("  Do NOT flag code as dead without searching for references. Reflection, DI registration, and test mocks can create invisible references.");
        sb.AppendLine("- **Bug findings MUST demonstrate a concrete failure scenario** — not \"this could fail\" but \"when X is null at L42, L47 dereferences it without a guard.\"");
        sb.AppendLine("- **TODO findings must include the surrounding context** — the comment alone is not enough. Show what's incomplete or broken.");
        sb.AppendLine("- **Stale doc findings must show both** the documented claim AND the actual code behavior side-by-side.");
        sb.AppendLine("- Findings sourced from deterministic tools (grep, linter, compiler warnings) are inherently higher quality.");
        sb.AppendLine("  Tag them as described in the critical rules.");
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

        sb.Append(RefactoringSubAgentPreamble);

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

        AppendFindingsOutputFormat(sb, new FindingsOutputSpec
        {
            OutputPath = AgentWorkspacePaths.RefactoringDesignFindingsFilePath,
            Categories = RefactoringCategories.Design,
            EvidenceHint = "The specific naming deviation or primitive usage with concrete examples",
            EvidenceSourcesExample = "\"grep:string repositoryUrl\", \"usage-search:repositoryUrl:5-signatures\"",
            CrossReferenceHint = "For naming: the convention rule violated + examples of correct naming elsewhere. For primitives: multiple locations using the same raw type for the same concept.",
            ImpactHint = "Cognitive cost, confusion risk, or bug risk from the inconsistency",
            SuggestedFixHint = "Brief approach — rename to X, introduce value type Y, extract constant Z"
        });

        sb.AppendLine("## Quality Bar");
        sb.AppendLine();
        sb.AppendLine("- **Naming findings require a convention rule reference.** \"This name seems odd\" is not a finding.");
        sb.AppendLine("  \"Convention says services end with 'Service' but `FooHandler` doesn't follow this\" IS a finding.");
        sb.AppendLine("- **Primitive obsession findings require 3+ occurrences.** A single string parameter is not primitive obsession.");
        sb.AppendLine("  The same concept passed as raw string through 3+ call sites IS primitive obsession.");
        sb.AppendLine("- Every naming and primitive-obsession finding needs a `scopeQuery` that lists all occurrences, so the fix is complete.");
        sb.AppendLine("- **Do NOT flag naming in test projects** unless conventions.json explicitly covers test naming.");
        sb.AppendLine("- **Do NOT flag names that match `intentionalPatterns`** from conventions.json.");
        sb.AppendLine("- This agent has the highest false-positive risk. Be conservative. Maximum 8 findings.");

        return sb.ToString();
    }

    /// <summary>
    /// The per-agent texts of the findings output format written by <see cref="AppendFindingsOutputFormat"/>.
    /// </summary>
    private sealed record FindingsOutputSpec
    {
        public required string OutputPath { get; init; }
        public required IReadOnlyList<string> Categories { get; init; }
        public required string EvidenceHint { get; init; }
        public required string EvidenceSourcesExample { get; init; }
        public required string CrossReferenceHint { get; init; }
        public required string ImpactHint { get; init; }
        public required string SuggestedFixHint { get; init; }
    }

    /// <summary>
    /// Appends the findings output format shared by the three Phase 1 agents: a JSON object with the
    /// findings and the areas the agent did not check.
    /// </summary>
    private static void AppendFindingsOutputFormat(StringBuilder sb, FindingsOutputSpec spec)
    {
        PromptBuilder.AppendOutputFormatHeading(sb);
        sb.AppendLine();
        sb.AppendLine($"Write findings to `{spec.OutputPath}` as a JSON object:");
        sb.AppendLine();
        sb.AppendLine(JsonCodeFence);
        sb.AppendLine("{");
        sb.AppendLine("  \"findings\": [");
        sb.AppendLine("    {");
        sb.AppendLine("      \"title\": \"Short descriptive title\",");
        sb.AppendLine($"      \"category\": \"{RefactoringCategories.ToSchemaList(spec.Categories)}\",");
        sb.AppendLine("      \"affectedFiles\": [\"src/path/to/File.cs\"],");
        sb.AppendLine($"      \"evidence\": \"{spec.EvidenceHint}\",");
        sb.AppendLine($"      \"evidenceSources\": [{spec.EvidenceSourcesExample}],");
        sb.AppendLine($"      \"crossReference\": \"{spec.CrossReferenceHint}\",");
        sb.AppendLine("      \"scopeQuery\": \"When the finding is one instance of a repeated pattern: the search that lists every instance, e.g. git grep -n 'pattern' -- src\",");
        sb.AppendLine($"      \"impact\": \"{spec.ImpactHint}\",");
        sb.AppendLine($"      \"suggestedFix\": \"{spec.SuggestedFixHint}\"");
        sb.AppendLine("    }");
        sb.AppendLine("  ],");
        sb.AppendLine("  \"notChecked\": [\"Files or areas you skipped, and why\"]");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
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
        sb.AppendLine("Each findings file holds a `findings` array and a `notChecked` list.");
        sb.AppendLine("If a findings file is missing or is not valid JSON, that agent failed. Continue with the other files");
        sb.AppendLine("and record the failure in the analysis log.");
        sb.AppendLine();

        sb.AppendLine("## Aggregation Steps");
        sb.AppendLine();
        sb.AppendLine("### Step 1: Deduplicate");
        sb.AppendLine();
        sb.AppendLine("Multiple agents may flag the same file or overlapping concerns:");
        sb.AppendLine("- If two findings reference the same file with related issues → merge into one proposal");
        sb.AppendLine("- If a structural finding and a correctness finding describe the same root cause → keep the one with stronger evidence");
        sb.AppendLine("- Preserve the best `evidence`, `crossReference` and `scopeQuery` from both when merging");
        sb.AppendLine();
        sb.AppendLine("### Step 2: Filter Against Conventions");
        sb.AppendLine();
        sb.AppendLine("For each remaining finding, check against `refactoring-conventions.json`:");
        sb.AppendLine("- Does it flag something listed in `intentionalPatterns`? → **DROP IT**");
        sb.AppendLine($"- Does it flag something listed in `knownDebt`? → **DROP IT** (team already knows). This does not apply to `{RefactoringCategories.Todo}` findings: they come from inline comments, which knownDebt does not cover.");
        sb.AppendLine("- Does it contradict `abstractionPhilosophy`? (e.g., flagging missing interface when philosophy says \"minimal interfaces\") → **DROP IT**");
        sb.AppendLine("- Does it contradict `testingPhilosophy`? → **DROP IT**");
        sb.AppendLine($"- Does it target `{AgentWorkspacePaths.MetadataDirectory}/`, `{AgentWorkspacePaths.BrainDirectory}/`, generated code, or files ignored by git? → **DROP IT**");
        sb.AppendLine();
        sb.AppendLine("### Step 3: Evidence Quality Gate");
        sb.AppendLine();
        sb.AppendLine("Reject findings whose evidence does not meet the bar for their category:");
        sb.AppendLine($"- `{RefactoringCategories.Bug}`: `evidence` MUST show a concrete failure scenario — the input or state, the line that goes wrong, and the wrong outcome. Otherwise DROP the proposal.");
        sb.AppendLine($"- `{RefactoringCategories.DeadCode}`: MUST have a `usage-search:` or `tool:` source showing zero references. Otherwise DROP the proposal.");
        sb.AppendLine($"- `{RefactoringCategories.Duplication}`: `crossReference` MUST name the other copy. Otherwise DROP the proposal.");
        sb.AppendLine($"- `{RefactoringCategories.NamingInconsistency}`: MUST cite the convention rule it violates. Otherwise DROP the proposal.");
        sb.AppendLine($"- `{RefactoringCategories.PrimitiveObsession}`: MUST show 3+ occurrences with a `grep:` or `usage-search:` source. Otherwise DROP the proposal.");
        sb.AppendLine($"- `{RefactoringCategories.StructuralDrift}`, `{RefactoringCategories.Complexity}`, `{RefactoringCategories.OverEngineering}`, `{RefactoringCategories.Todo}`, `{RefactoringCategories.StaleDocumentation}`:");
        sb.AppendLine("  may use \"code-reading:\" alone but receive a capped evidence score of 1 unless a `tool:`, `grep:` or `usage-search:` source corroborates them.");
        sb.AppendLine("- `hotspot:` is a priority signal, not evidence. It never satisfies this gate and never raises the evidence score.");
        sb.AppendLine("- A `tool:` source counts only when it names a compiler, linter, analyzer or MCP tool and its rule or query. A search or a file read tagged `tool:` counts as `grep:` or `code-reading:`.");
        sb.AppendLine();
        sb.AppendLine("### Step 4: Rank by Impact");
        sb.AppendLine();
        sb.AppendLine($"1. **Bugs first.** Every `{RefactoringCategories.Bug}` finding that passed the gate ranks above all other findings —");
        sb.AppendLine("   wrong behavior today matters more than any cleanup. Order bugs by evidence strength.");
        sb.AppendLine("2. Score every other surviving finding on three axes (each 1-3):");
        sb.AppendLine();
        sb.AppendLine("| Axis | 3 (high) | 2 (medium) | 1 (low) |");
        sb.AppendLine("|------|----------|------------|---------|");
        sb.AppendLine("| **Hotspot frequency** | File in top 10 hotspots | File in top 11-20 | Not in hotspot list |");
        sb.AppendLine("| **Evidence strength** | Tool-confirmed or multi-source | Code reading with crossReference | Single observation |");
        sb.AppendLine("| **Scope feasibility** | <10 files affected | 10-20 files | 20-30 files |");
        sb.AppendLine();
        sb.AppendLine("   Final score = hotspot × evidence × scope. Rank descending.");
        sb.AppendLine("3. **Spread the work.** Select at most one proposal per primary file (the first entry of `affectedFiles`).");
        sb.AppendLine("   Skip a finding whose primary file is the main subject of an open issue listed below — that file already has work queued.");
        sb.AppendLine();
        sb.AppendLine("### Step 5: Format as Proposals");
        sb.AppendLine();
        sb.AppendLine($"Select the top **{maxProposals}** findings by rank and convert them into the final proposal format.");
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

        PromptBuilder.AppendOutputFormatHeading(sb);
        sb.AppendLine();
        sb.AppendLine($"Produce the final proposals at `{AgentWorkspacePaths.RefactoringProposalsFilePath}` as a JSON array:");
        sb.AppendLine();
        sb.AppendLine(JsonCodeFence);
        sb.AppendLine("[");
        sb.AppendLine("  {");
        sb.AppendLine("    \"title\": \"Short descriptive title of the refactoring opportunity\",");
        sb.AppendLine($"    \"category\": \"{RefactoringCategories.ToSchemaList(RefactoringCategories.All)}\",");
        sb.AppendLine("    \"affectedFiles\": [\"src/path/to/File1.cs\", \"src/path/to/File2.cs\"],");
        sb.AppendLine("    \"rationale\": \"The problem: what goes wrong or what it costs, and why it matters\",");
        sb.AppendLine("    \"description\": \"The change: what to change, as one concrete approach\",");
        sb.AppendLine("    \"evidence\": \"File.cs:L42-L47\\n<the decisive code or tool output, quoted verbatim>\",");
        sb.AppendLine("    \"evidenceSources\": [\"tool:dotnet-build:IDE0051\", \"usage-search:Foo.Bar:0-callers\", \"code-reading:File.cs:L42\"],");
        sb.AppendLine("    \"scopeQuery\": \"git grep -n 'pattern' -- src\",");
        sb.AppendLine("    \"prerequisites\": [\"Add characterization tests for X before refactoring\"],");
        sb.AppendLine("    \"dependsOn\": [\"Exact title of another proposal this depends on\"],");
        sb.AppendLine("    \"estimatedEffort\": \"small|medium|large\",");
        sb.AppendLine("    \"riskLevel\": \"low|medium|high\",");
        sb.AppendLine("    \"technique\": \"Extract Method|Inline Class|Rename|Introduce Value Type|etc.\",");
        sb.AppendLine("    \"acceptanceCriteria\": [");
        sb.AppendLine("      \"Zero references to old name OldService remain in .cs files\",");
        sb.AppendLine("      \"Extracted class registered in DI container\"");
        sb.AppendLine("    ]");
        sb.AppendLine("  }");
        sb.AppendLine("]");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("### Field Definitions");
        sb.AppendLine();
        sb.AppendLine("- **category** (required) — the category of the finding the proposal comes from, exactly as listed in the schema above.");
        sb.AppendLine("- **rationale** (required) — the problem. State what goes wrong, or what it costs, and commit to it: \"X causes Y because Z.\"");
        sb.AppendLine($"  For `{RefactoringCategories.Bug}`, state the failure scenario: the input or state, and the wrong outcome.");
        sb.AppendLine("- **description** (required) — the change. Propose ONE approach; do not offer alternatives (\"X or Y\").");
        sb.AppendLine("- **evidence** (required) — the decisive code or tool output, quoted verbatim, starting with the file and line range.");
        sb.AppendLine("  Copy it from the sub-agent finding; do not paraphrase.");
        sb.AppendLine("- **evidenceSources** (required) — list of evidence that supports this proposal. Prefix with type:");
        sb.AppendLine("  `tool:` (linter/compiler output), `code-reading:` (manual inspection), `grep:` (pattern search),");
        sb.AppendLine("  `usage-search:` (reference count), `hotspot:` (git frequency). Multi-source proposals are higher quality.");
        sb.AppendLine("- **scopeQuery** — the search that lists every instance this proposal must change. Required when the finding is one");
        sb.AppendLine($"  instance of a repeated pattern (always for `{RefactoringCategories.Duplication}`, `{RefactoringCategories.NamingInconsistency}`, `{RefactoringCategories.PrimitiveObsession}`).");
        sb.AppendLine("  Every match is in scope unless `description` excludes it by name. Run it before you finish: every file it");
        sb.AppendLine("  matches must be in `affectedFiles` or excluded in `description`.");
        sb.AppendLine("- **prerequisites** — prep work needed. If affected files lack test coverage, MUST include");
        sb.AppendLine("  \"Add characterization tests for X before refactoring\". Do NOT reference other proposals by number");
        sb.AppendLine("  (e.g., \"proposal #1\") — GitHub will autolink #N to wrong issues.");
        sb.AppendLine("- **dependsOn** — titles of other proposals in this batch that must be completed first.");
        sb.AppendLine("  Use the EXACT title string of the dependency. These are resolved to `Depends on #N` during issue creation.");
        sb.AppendLine("  Do NOT use `#N` notation anywhere — it creates wrong GitHub autolinks.");
        sb.AppendLine("- **estimatedEffort** — `small` (<5 files), `medium` (5-15 files), `large` (15-30 files).");
        sb.AppendLine("- **riskLevel** — `low` (rename/move), `medium` (extract/restructure), `high` (interface changes).");
        sb.AppendLine("- **technique** — named refactoring pattern if applicable.");
        sb.AppendLine("- **acceptanceCriteria** (required, 2-4 items) — Verifiable conditions proving the refactoring was correctly applied.");
        sb.AppendLine("  A separate review agent validates each criterion against the PR diff. Criteria drive automatic fix iterations");
        sb.AppendLine("  when violated — write them to catch what a careless implementation would miss.");
        sb.AppendLine();
        sb.AppendLine("  Rules:");
        sb.AppendLine("  - Describe WHAT must be true after, not HOW to do it. Good: \"No remaining callers of X\". Bad: \"Create file Y.cs\"");
        sb.AppendLine("  - Must be verifiable from the git diff or test results alone. No runtime, no benchmarks, no subjective quality.");
        sb.AppendLine("  - Do NOT repeat pipeline invariants (\"build passes\", \"tests pass\") — those are enforced separately.");
        sb.AppendLine("  - Each criterion must test a DISTINCT concern. No rephrased duplicates.");
        sb.AppendLine("  - Prefer negative assertions (\"no references remain\", \"no callers exist\") — they catch incomplete implementations.");
        sb.AppendLine("  - When `scopeQuery` is set, one criterion MUST state that no match of the pattern remains (apart from the exclusions in `description`).");
        sb.AppendLine($"  - For `{RefactoringCategories.Bug}`, one criterion MUST require a test that reproduces the failure scenario and passes after the fix.");
        sb.AppendLine("  - Do NOT use #N notation — GitHub autolinks to wrong issues.");
        sb.AppendLine();
        sb.AppendLine("  Examples by category:");
        sb.AppendLine("  - dead-code: \"No remaining callers or references to deleted methods in .cs files\"");
        sb.AppendLine("  - rename: \"Zero occurrences of old name in source, test, and configuration files\"");
        sb.AppendLine("  - extract-class: \"Original class no longer contains the extracted methods\", \"New class is registered in DI\"");
        sb.AppendLine("  - duplication: \"Duplicated logic consolidated to single call site\"");
        sb.AppendLine("  - bug: \"A test that sets X to null before calling Y fails before the fix and passes after it\"");
        sb.AppendLine();
        sb.AppendLine("## Writing for the Implementer");
        sb.AppendLine();
        sb.AppendLine("Each proposal becomes an issue for an engineer who has not seen this analysis:");
        sb.AppendLine("- Do NOT mention the analysis agents (A, B, C), phases, scores, rankings, or this scan in any text field.");
        sb.AppendLine("- Keep the issue self-contained: name the files, symbols and line ranges the engineer needs.");
        sb.AppendLine("- Keep it short. A shorter, well-scoped issue is more likely to be implemented correctly.");
        sb.AppendLine();
        sb.AppendLine("## Scope Constraints");
        sb.AppendLine();
        sb.AppendLine("Each proposal MUST be achievable by a single agent in one run:");
        sb.AppendLine("- Maximum ~30 affected files (source + test) per proposal");
        sb.AppendLine("- If a finding would touch more files, split into independent phases");
        sb.AppendLine("- Each phase must leave the codebase buildable");
        sb.AppendLine("- Prefer mechanical, low-risk changes over sweeping architectural ones");
        sb.AppendLine("- Do NOT propose changes spanning serialization boundaries simultaneously");
        sb.AppendLine("- One concern per proposal: do not bundle two classes' decompositions into one proposal");
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
        sb.AppendLine($"Write a brief analysis log at `{AgentWorkspacePaths.RefactoringAnalysisFilePath}` containing:");
        sb.AppendLine("- Total findings received from agents A, B, C, and any agent whose findings file was missing or invalid");
        sb.AppendLine("- How many were dropped (duplicates, convention-filtered, evidence gate, scope-exceeded)");
        sb.AppendLine("- The ranking scores for the top candidates");
        sb.AppendLine("- Which findings were dropped and why (one line each)");
        sb.AppendLine("- The `notChecked` areas the agents reported");

        return sb.ToString();
    }
}
