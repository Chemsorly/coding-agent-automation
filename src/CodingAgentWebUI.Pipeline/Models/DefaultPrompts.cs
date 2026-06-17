namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Default prompt templates used by the pipeline for analysis, implementation, and code review.
/// Extracted from <see cref="PipelineConfiguration"/> to reduce model file size.
/// </summary>
public static class DefaultPrompts
{
    public const string CodeReview =
        "Review the changes against the original issue requirements. Use a sub-agent for the review.\n" +
        "Output findings as a numbered list with severity [CRITICAL], [WARNING], or [SUGGESTION].\n\n" +
        "CHECK FOR:\n" +
        "- Unhandled null references and exception paths\n" +
        "- Off-by-one errors in loops and collections\n" +
        "- Race conditions in async/concurrent code\n" +
        "- Missing input validation on public API boundaries\n" +
        "- Edge cases not covered by the implementation\n" +
        "- Resources not properly released (file handles, connections, streams)\n" +
        "- Error handling gaps (swallowed exceptions, missing cleanup on failure)\n\n" +
        "DO NOT FLAG:\n" +
        "- Style preferences or naming conventions\n" +
        "- Missing XML documentation comments\n" +
        "- Theoretical risks requiring unlikely preconditions\n" +
        "- Issues in unchanged code outside the diff\n" +
        "- \"Consider using library X\" suggestions\n" +
        "- Performance micro-optimizations\n" +
        "- Missing nullable annotations on internal code\n" +
        "- Test code conventions\n" +
        "- Untracked or unstaged files shown by `git status` (the pipeline auto-stages all files before commit)\n\n" +
        "Do NOT fix anything. Only report findings.";

    public const string Fix =
        "Review the findings above. Fix only items marked [CRITICAL]. " +
        "For [WARNING] items, fix them if the fix is straightforward and low-risk; " +
        "otherwise add a TODO comment at the relevant location. Ignore [SUGGESTION] items.";

    public const string CorrectnessReview =
        "Review the changes against the original issue requirements. Use a sub-agent for the review. " +
        "Output findings as a numbered list with severity [CRITICAL], [WARNING], or [SUGGESTION].\n\n" +
        "CHECK FOR:\n" +
        "- Unhandled null references and exception paths\n" +
        "- Off-by-one errors in loops and collections\n" +
        "- Race conditions in async code\n" +
        "- Missing input validation on public API boundaries\n" +
        "- Edge cases not covered by the implementation\n\n" +
        "DO NOT FLAG:\n" +
        "- Style preferences or naming conventions\n" +
        "- Missing XML documentation comments\n" +
        "- Theoretical risks requiring unlikely preconditions\n" +
        "- Issues in unchanged code outside the diff\n" +
        "- \"Consider using library X\" suggestions\n" +
        "- Performance micro-optimizations\n" +
        "- Untracked or unstaged files shown by `git status` (the pipeline auto-stages all files before commit)\n\n" +
        "Do NOT fix anything. Only report findings.";

    public const string DotNetSpecialistReview =
        "Review the changes for .NET-specific issues. Output findings as a numbered list " +
        "with severity [CRITICAL], [WARNING], or [SUGGESTION].\n\n" +
        "CHECK FOR:\n" +
        "- IDisposable resources not properly disposed (missing using/await using)\n" +
        "- Async/await deadlock patterns (sync-over-async, .Result, .Wait())\n" +
        "- DI lifetime mismatches (scoped service injected into singleton)\n" +
        "- CancellationToken not propagated through async call chains\n" +
        "- ArgumentNullException.ThrowIfNull missing on public method parameters\n" +
        "- Collections exposed as mutable (List<T> instead of IReadOnlyList<T>)\n\n" +
        "DO NOT FLAG:\n" +
        "- Issues in unchanged code outside the diff\n" +
        "- Business logic correctness\n" +
        "- Style or formatting preferences\n" +
        "- Missing nullable annotations on internal code\n" +
        "- Test code conventions\n" +
        "- Suggestions to add more abstractions or interfaces\n" +
        "- Untracked or unstaged files shown by `git status` (the pipeline auto-stages all files before commit)\n\n" +
        "Do NOT fix anything. Only report findings.";

    public const string SecurityReview =
        "Review the changes for security issues. Output findings as a numbered list with " +
        "severity [CRITICAL], [WARNING], or [SUGGESTION].\n\n" +
        "CHECK FOR:\n" +
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
        "DO NOT FLAG:\n" +
        "- Issues in unchanged code outside the diff\n" +
        "- General code correctness or .NET pattern issues\n" +
        "- Theoretical attacks requiring physical access or pre-existing compromise\n" +
        "- Missing HTTPS enforcement (infrastructure concern, not code)\n" +
        "- Test code, sample data, or placeholder values in test fixtures\n" +
        "- Dependency vulnerabilities (covered by external CI)\n" +
        "- General code quality or style issues\n" +
        "- Untracked or unstaged files shown by `git status` (the pipeline auto-stages all files before commit)\n\n" +
        "Do NOT fix anything. Only report findings.";

    public const string TestQualityReview =
        "Review the changes for test quality issues. Focus exclusively on whether tests are " +
        "meaningful, effective, and actually validate the intended behavior. Output findings " +
        "as a numbered list with severity [CRITICAL], [WARNING], or [SUGGESTION].\n\n" +
        "CHECK FOR:\n" +
        "- Tautological tests (assertions that pass regardless of implementation, e.g. Assert.True(true), asserting the mock returns what you told it to return)\n" +
        "- Tests that don't exercise the changed behavior (test exists but wouldn't fail if the fix were reverted)\n" +
        "- Assertions that are too weak (checking only non-null or collection non-empty when specific values matter)\n" +
        "- Missing negative test cases (only happy path tested, no error/edge case coverage)\n" +
        "- Tests that verify implementation details rather than observable behavior (brittle coupling to internals)\n" +
        "- Duplicate test logic that should be parameterized (same test body copy-pasted with different inputs)\n" +
        "- Missing boundary conditions (off-by-one, empty inputs, max values, concurrent access)\n" +
        "- Test setup that masks bugs (overly permissive mocks that hide real integration failures)\n" +
        "- Assertions on wrong granularity (testing an entire object equality when only one property changed)\n\n" +
        "DO NOT FLAG:\n" +
        "- Test naming conventions or style preferences\n" +
        "- Production code issues (correctness, .NET patterns, security)\n" +
        "- Missing tests for unchanged code outside the diff\n" +
        "- Test infrastructure or framework choice\n" +
        "- Suggestions to add property-based tests unless the code has clear invariants\n" +
        "- Minor test organization preferences (file placement, class grouping)\n" +
        "- Untracked or unstaged files shown by `git status` (the pipeline auto-stages all files before commit)\n\n" +
        "Do NOT fix anything. Only report findings.";

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
        "DO NOT FLAG:\n" +
        "- Code correctness, .NET patterns, security, or test quality issues (covered by dedicated review agents)\n" +
        "- Style or formatting preferences\n" +
        "- Suggestions to add features beyond what the issue requests\n" +
        "- Untracked or unstaged files shown by `git status` (the pipeline auto-stages all files before commit)\n\n" +
        "Do NOT fix anything. Only report findings.";

    public const string Analysis =
        "Analyze the codebase in context of the following issue. Read the issue carefully, " +
        "then explore the relevant source files to understand the current architecture and identify what needs to change.\n\n" +
        "Your analysis should cover:\n" +
        "1. **Planned Approach** — What files need to change and how. Be specific about the strategy.\n" +
        "2. **Affected Components** — Which files, classes, or modules will be touched.\n" +
        "3. **Test Coverage** — What existing tests cover the affected code, and what new tests will be needed.\n" +
        "4. **Risks & Considerations** — Breaking changes, edge cases, backward compatibility, or anything that needs special attention.";

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
        "DO NOT FLAG:\n" +
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
        "2. **Implement** — Make focused, minimal changes. Fix root causes, not symptoms. Maintain the existing code style and conventions.\n" +
        "3. **Verify** — Run the project's build, linter, and tests. If a command fails, fix the issue and re-run to confirm.\n\n" +
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
}
