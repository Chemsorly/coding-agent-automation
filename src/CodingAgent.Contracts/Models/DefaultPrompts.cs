namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Default prompt templates used by the pipeline for analysis, implementation, and code review.
/// Extracted from <see cref="PipelineConfiguration"/> to reduce model file size.
/// </summary>
public static class DefaultPrompts
{
    private const string FocusAreasHeader = "FOCUS AREAS (flag only when a concrete defect exists):\n";
    private const string DoNotFlagHeader = "DO NOT FLAG:\n";
    private const string IgnoreUnchangedCodeRule = "- Issues in unchanged code outside the diff\n";
    private const string IgnoreUnstagedFilesRule = "- Untracked or unstaged files shown by `git status` (the pipeline auto-stages all files before commit)\n\n";
    private const string ReportOnlyInstruction = "Do NOT fix anything. Only report findings.";

    public const string CodeReview =
        "Review the changes against the original issue requirements. Use a sub-agent for the review.\n" +
        "Output findings as a numbered list with severity [CRITICAL], [WARNING], or [SUGGESTION].\n\n" +
        FocusAreasHeader +
        "- Unhandled null references and exception paths\n" +
        "- Off-by-one errors in loops and collections\n" +
        "- Race conditions in async/concurrent code\n" +
        "- Missing input validation on public API boundaries\n" +
        "- Edge cases not covered by the implementation\n" +
        "- Resources not properly released (file handles, connections, streams)\n" +
        "- Error handling gaps (swallowed exceptions, missing cleanup on failure)\n" +
        "- **Cross-boundary impacts** — trace each change through its callers and consumers. The most critical bugs appear at boundaries between components (authorization logic, API contracts, state machine transitions, serialization boundaries)\n\n" +
        DoNotFlagHeader +
        "- Style preferences or naming conventions\n" +
        "- Missing XML documentation comments\n" +
        "- Theoretical risks requiring unlikely preconditions\n" +
        IgnoreUnchangedCodeRule +
        "- \"Consider using library X\" suggestions\n" +
        "- Performance micro-optimizations\n" +
        "- Missing nullable annotations on internal code\n" +
        "- Test code conventions\n" +
        IgnoreUnstagedFilesRule +
        ReportOnlyInstruction;

    public const string Fix =
        "Review the findings above. Fix only items marked [CRITICAL]. " +
        "For [WARNING] items, fix them if the fix is straightforward and low-risk; " +
        "otherwise add a TODO comment at the relevant location. Ignore [SUGGESTION] items.";

    public const string CorrectnessReview =
        "Review the changes against the original issue requirements. Use a sub-agent for the review. " +
        "Output findings as a numbered list with severity [CRITICAL], [WARNING], or [SUGGESTION].\n\n" +
        FocusAreasHeader +
        "- Unhandled null references and exception paths\n" +
        "- Off-by-one errors in loops and collections\n" +
        "- Race conditions in async code\n" +
        "- Missing input validation on public API boundaries\n" +
        "- Edge cases not covered by the implementation\n" +
        "- **Cross-boundary impacts** — trace each change through its callers and consumers. The most critical bugs appear at component boundaries (API contracts, state transitions, serialization)\n\n" +
        DoNotFlagHeader +
        "- Style preferences or naming conventions\n" +
        "- Missing XML documentation comments\n" +
        "- Theoretical risks requiring unlikely preconditions\n" +
        IgnoreUnchangedCodeRule +
        "- \"Consider using library X\" suggestions\n" +
        "- Performance micro-optimizations\n" +
        IgnoreUnstagedFilesRule +
        ReportOnlyInstruction;

    public const string DotNetSpecialistReview =
        "Review the changes for .NET-specific issues. Output findings as a numbered list " +
        "with severity [CRITICAL], [WARNING], or [SUGGESTION].\n\n" +
        FocusAreasHeader +
        "- IDisposable resources not properly disposed (missing using/await using)\n" +
        "- Async/await deadlock patterns (sync-over-async, .Result, .Wait())\n" +
        "- DI lifetime mismatches (scoped service injected into singleton)\n" +
        "- CancellationToken not propagated through async call chains\n" +
        "- ArgumentNullException.ThrowIfNull missing on public method parameters\n" +
        "- Collections exposed as mutable (List<T> instead of IReadOnlyList<T>)\n\n" +
        DoNotFlagHeader +
        IgnoreUnchangedCodeRule +
        "- Business logic correctness\n" +
        "- Style or formatting preferences\n" +
        "- Missing nullable annotations on internal code\n" +
        "- Test code conventions\n" +
        "- Suggestions to add more abstractions or interfaces\n" +
        IgnoreUnstagedFilesRule +
        ReportOnlyInstruction;

    public const string SecurityReview =
        "Review the changes for security issues. Output findings as a numbered list with " +
        "severity [CRITICAL], [WARNING], or [SUGGESTION].\n\n" +
        FocusAreasHeader +
        "- Hardcoded credentials, API keys, connection strings, or tokens\n" +
        "- SQL injection (string concatenation or interpolation in queries)\n" +
        "- Path traversal (user input used in file paths without validation)\n" +
        "- Insecure deserialization of untrusted data\n" +
        "- Missing authentication or authorization checks on new endpoints\n" +
        "- Sensitive data logged without redaction (passwords, tokens, PII)\n" +
        "- Insecure cryptography (MD5, SHA1 for security purposes, ECB mode)\n" +
        "- SSRF (user-controlled URLs passed to HTTP clients)\n" +
        "- Missing input validation or sanitization on external input boundaries\n" +
        "- Secrets or credentials in committed files\n\n" +
        DoNotFlagHeader +
        IgnoreUnchangedCodeRule +
        "- General code correctness or .NET pattern issues\n" +
        "- Theoretical attacks requiring physical access or pre-existing compromise\n" +
        "- Missing HTTPS enforcement (infrastructure concern, not code)\n" +
        "- Test code, sample data, or placeholder values in test fixtures\n" +
        "- Dependency vulnerabilities (covered by external CI)\n" +
        "- General code quality or style issues\n" +
        IgnoreUnstagedFilesRule +
        ReportOnlyInstruction;

    public const string TestQualityReview =
        "Review the changes for test quality issues. Focus exclusively on whether tests are " +
        "meaningful, effective, and actually validate the intended behavior. Output findings " +
        "as a numbered list with severity [CRITICAL], [WARNING], or [SUGGESTION].\n\n" +
        FocusAreasHeader +
        "- Tautological tests (assertions that pass regardless of implementation, e.g. Assert.True(true), asserting the mock returns what you told it to return)\n" +
        "- Tests that don't exercise the changed behavior (test exists but wouldn't fail if the fix were reverted)\n" +
        "- Assertions that are too weak (checking only non-null or collection non-empty when specific values matter)\n" +
        "- Missing negative test cases (only happy path tested, no error/edge case coverage)\n" +
        "- Tests that verify implementation details rather than observable behavior (brittle coupling to internals)\n" +
        "- Duplicate test logic that should be parameterized (same test body copy-pasted with different inputs)\n" +
        "- Missing boundary conditions (off-by-one, empty inputs, max values, concurrent access)\n" +
        "- Test setup that masks bugs (overly permissive mocks that hide real integration failures)\n" +
        "- Assertions on wrong granularity (testing an entire object equality when only one property changed)\n\n" +
        DoNotFlagHeader +
        "- Test naming conventions or style preferences\n" +
        "- Production code issues (correctness, .NET patterns, security)\n" +
        "- Missing tests for unchanged code outside the diff\n" +
        "- Test infrastructure or framework choice\n" +
        "- Suggestions to add property-based tests unless the code has clear invariants\n" +
        "- Minor test organization preferences (file placement, class grouping)\n" +
        IgnoreUnstagedFilesRule +
        ReportOnlyInstruction;

    public const string AcceptanceCriteriaReview =
        "Review the changes against the acceptance criteria from the original issue. " +
        "Focus exclusively on whether acceptance criteria are satisfied — do not " +
        "duplicate findings from other review agents.\n\n" +
        "Go through EACH acceptance criterion listed in the issue one by one. " +
        "For each criterion, determine whether the implementation satisfies it.\n\n" +
        "Output your findings as a numbered list with severity markers:\n" +
        "- [CRITICAL] — Acceptance criterion is NOT met. The implementation is missing " +
        "the required behavior or contradicts the requirement.\n" +
        "- [WARNING] — Acceptance criterion is PARTIALLY met. The core behavior exists " +
        "but edge cases or secondary aspects are missing.\n" +
        "- [SUGGESTION] — Acceptance criterion is met, but the implementation could " +
        "better align with the intent.\n\n" +
        "For each finding, quote the specific acceptance criterion and explain " +
        "what is missing or incomplete with references to the relevant code.\n\n" +
        "If ALL acceptance criteria are fully met, state that explicitly — " +
        "do not invent findings.\n\n" +
        "If the issue has no acceptance criteria section, check whether the " +
        "implementation addresses the issue description and stated goals instead.\n\n" +
        DoNotFlagHeader +
        "- Code correctness, .NET patterns, security, or test quality issues (covered by dedicated review agents)\n" +
        "- Style or formatting preferences\n" +
        "- Suggestions to add features beyond what the issue requests\n" +
        IgnoreUnstagedFilesRule +
        ReportOnlyInstruction;

    public const string Analysis =
        "Analyze the codebase in context of the following issue. Read the issue carefully, " +
        "then explore the relevant source files to understand the current architecture and identify what needs to change.\n\n" +
        "Your analysis should cover:\n" +
        "1. **Planned Approach** — What files need to change and how. Be specific about the strategy.\n" +
        "2. **Affected Components** — Which files, classes, or modules will be touched.\n" +
        "3. **Test Coverage** — What existing tests cover the affected code, and what new tests will be needed.\n" +
        "4. **Risks & Considerations** — Breaking changes, edge cases, backward compatibility, or anything that needs special attention.\n\n" +
        "## What a BAD Analysis Looks Like\n\n" +
        "Do NOT produce output like this:\n" +
        "- \"The code should be refactored to improve maintainability\" (no specifics)\n" +
        "- \"Several files may need changes\" (which files?)\n" +
        "- \"Tests should be added\" (which tests? what behavior do they verify?)\n" +
        "- Listing file paths without explaining WHAT changes each file needs\n" +
        "- Restating the issue description instead of analyzing the codebase\n\n" +
        "Every claim must reference a specific file, class, or method you actually read. " +
        "If you haven't read the code, don't speculate about it.";

    public const string AnalysisReview =
        "You are reviewing an analysis plan produced by another agent. You have no prior context about " +
        "how the analysis was produced — judge purely on completeness, feasibility, and correctness.\n\n" +
        "The analysis is at `.agent/analysis.md` and the structured assessment is at `.agent/analysis-assessment.json`. " +
        "The original issue is at `.agent/issue-context.md`.\n\n" +
        "Read all three files, then explore the codebase yourself to verify the analysis claims.\n\n" +
        "CHECK FOR:\n" +
        "- **Missing components** — Files or modules that need to change but aren't mentioned\n" +
        "- **Incorrect assumptions** — Claims about the codebase that don't match reality\n" +
        "- **Underestimated complexity** — Areas where the proposed approach is too simplistic\n" +
        "- **Missed edge cases** — Scenarios the analysis doesn't account for\n" +
        "- **Test gaps** — Missing test coverage that should be called out\n" +
        "- **Feasibility issues** — Approaches that won't work given the current architecture\n" +
        "- **Acceptance criteria gaps** — Requirements from the issue that the plan doesn't address\n\n" +
        DoNotFlagHeader +
        "- Style preferences in the analysis writing\n" +
        "- Minor wording improvements\n" +
        "- Theoretical concerns that are unlikely in practice\n" +
        "- Suggestions to add scope beyond what the issue requests\n\n" +
        "Output your findings as a numbered list with severity markers:\n" +
        "- [CRITICAL] — The analysis is wrong or missing something that will cause implementation to fail\n" +
        "- [WARNING] — The analysis is incomplete but the implementation could still succeed\n" +
        "- [SUGGESTION] — The analysis could be improved but is fundamentally sound\n\n" +
        "If the analysis is thorough and correct, state that explicitly — do not invent findings.\n\n" +
        "Do NOT modify any source files, configuration files, or project files. Only read the codebase and write your review.";

    public const string AnalysisRefinement =
        "A review agent has evaluated your analysis and provided feedback.\n\n" +
        "Read the review, then update your analysis:\n" +
        "1. Address all [CRITICAL] findings — these indicate errors or gaps that must be fixed\n" +
        "2. Consider [WARNING] findings — incorporate them if they improve the plan\n" +
        "3. Ignore [SUGGESTION] items unless they are clearly valuable\n\n" +
        "Do NOT just append the review feedback — produce a clean, updated analysis that incorporates the improvements.";

    public const string Implementation =
        "Implement the following issue. Write the code — do not just analyze or plan.\n\n" +
        "Follow this approach:\n" +
        "1. **Understand** — Read the analysis and the issue. Explore relevant files before making changes.\n" +
        "2. **Test first (bug fixes)** — If the issue is a bug fix, write a failing test that reproduces the bug before implementing the fix.\n" +
        "3. **Implement** — Make focused, minimal changes. Fix root causes, not symptoms. Maintain the existing code style and conventions.\n" +
        "4. **Verify** — Run the project's build, linter, and tests. If a command fails:\n" +
        "   a. Read the error output carefully\n" +
        "   b. Identify the root cause (not just the symptom)\n" +
        "   c. Determine what specific change caused the failure\n" +
        "   d. Apply a targeted fix that addresses the root cause without reverting intended behavior\n" +
        "   e. Re-run to confirm the fix works\n\n" +
        "Keep working until the implementation is complete. If something fails, diagnose and fix it rather than stopping.";

    public const string AcceptanceCriteriaCompliance =
        "Evaluate the implementation against the acceptance criteria from the original issue.\n\n" +
        "Read the issue context from `.agent/issue-context.md` or `.agent/linked-issue-*.md` files " +
        "to find the acceptance criteria. If no acceptance criteria are found, write an empty criteria array.\n\n" +
        "For EACH criterion, determine whether the implementation satisfies it.\n\n" +
        "Write your assessment as a JSON file to `.agent/acceptance-criteria.json` with this exact schema:\n\n" +
        "```json\n" +
        "{\n" +
        "  \"criteria\": [\n" +
        "    {\n" +
        "      \"criterion\": \"Description of the requirement\",\n" +
        "      \"status\": \"compliant\",\n" +
        "      \"evidence\": \"What satisfies this criterion (file/feature reference)\"\n" +
        "    },\n" +
        "    {\n" +
        "      \"criterion\": \"Another requirement\",\n" +
        "      \"status\": \"non_compliant\",\n" +
        "      \"reasoning\": \"What is missing or incomplete\"\n" +
        "    }\n" +
        "  ],\n" +
        "  \"summary\": \"X of Y criteria addressed.\"\n" +
        "}\n" +
        "```\n\n" +
        "Status values:\n" +
        "- `compliant` — criterion is satisfied, provide `evidence` (file/feature reference)\n" +
        "- `non_compliant` — criterion is NOT satisfied, provide `reasoning` (what's missing)\n" +
        "- `not_applicable` — criterion is out of scope or contradicts constraints, provide `reasoning`\n\n" +
        "Use snake_case for status values in the JSON output.\n\n" +
        "Do NOT fix anything. Only evaluate and write the JSON file.";

    public const string ProjectReview =
        "Review the changes for consistency with the rest of the project. The project is one product, which may span " +
        "several repositories; this change is in one of them. Output findings as a numbered list with severity " +
        "[CRITICAL], [WARNING], or [SUGGESTION].\n\n" +
        "WHERE THE PROJECT IS DEFINED:\n" +
        "- The project's other repositories, if it has any, are checked out read-only, on their default branches, in the " +
        "folders listed under \"Project repositories\" below. Before you report anything about another repository, open " +
        "the code there and check it; never conclude from names alone.\n" +
        "- The brain in `.brain/`, if the workspace has one, holds lessons learned in earlier runs; `.brain/AGENTS.md` " +
        "describes its structure. One brain can serve several projects, so check that a lesson concerns this project " +
        "before you rely on it. Only read the brain; never modify it or run git commands that change it.\n" +
        "- The connected MCP servers reach more of the project, for example the ticketing system (related tickets and " +
        "epics) or the wiki (architecture decision records, specifications). Explore them for the decisions and " +
        "specifications that concern the changed code.\n" +
        "- The project steering, the issue, and the documentation in the repositories describe the project as well.\n\n" +
        "HOW CURRENT THE INFORMATION IS:\n" +
        "Documentation is often outdated, and sources can contradict each other. Before you rely on a document or a " +
        "lesson, find out how old it is: `git log -1 --format=%cs -- <file>` in the repository the file belongs to (the " +
        "brain is one too), the last-updated date of a wiki page or ticket. When a source is older than the code it " +
        "describes, or another source contradicts it, look for a more recent one. The code on the default branches and " +
        "recent decisions outweigh old documents. Report a contradiction between sources only when it affects the change, " +
        "and say which source you trusted and why.\n\n" +
        "FOCUS AREAS (flag only when a concrete inconsistency exists):\n" +
        "- Contracts between repositories: endpoints and their request and response shapes, status codes, message and " +
        "event schemas, shared database tables, configuration keys and environment variables. Find every producer and " +
        "consumer of a changed contract in the other repositories.\n" +
        "- Breaking changes without a migration path: removed or renamed fields, changed types or meaning, new required " +
        "inputs, changed defaults that another repository relies on.\n" +
        "- Rules, limits, enum values and error codes duplicated in another repository that now disagree.\n" +
        "- Project decisions: architecture decision records, specifications, domain terms and the names of product " +
        "concepts that hold across all repositories.\n" +
        "- Lessons learned: the change repeats a mistake that a lesson in the brain warns about, where the lesson " +
        "concerns this project.\n" +
        "- Changes that only work if another repository changes too, when the issue does not say so.\n" +
        "- Project documentation in this or another repository (README, API docs, decision records) that the change " +
        "makes wrong.\n\n" +
        "SEVERITY FOR CROSS-REPOSITORY FINDINGS:\n" +
        "- [CRITICAL]: the change breaks another repository as it is today, and this change can avoid the break " +
        "(for example by keeping the old field or accepting both formats).\n" +
        "- [WARNING]: the fix belongs in another repository. Name the repository, the file and what has to change there; " +
        "it cannot be fixed in this change.\n" +
        "- [SUGGESTION]: an alignment that is not needed for correctness.\n\n" +
        DoNotFlagHeader +
        "- Correctness, security, test quality or stack-specific issues inside this repository (other reviewers cover them)\n" +
        "- Problems in other repositories that the change neither touches nor depends on\n" +
        "- Differences between repositories that the project steering or a current decision allows\n" +
        "- Outdated documentation that the change neither touches nor relies on\n" +
        "- Style or formatting preferences\n" +
        IgnoreUnstagedFilesRule +
        "Do NOT fix anything, and never modify the other repositories. Only report findings.";
}
