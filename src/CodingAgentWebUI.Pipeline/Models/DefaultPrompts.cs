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
        "For [WARNING] items, add a TODO comment at the relevant location. Ignore [SUGGESTION] items.";

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
        "Review the changes for .NET-specific issues. The previous review covered correctness — " +
        "do not duplicate those findings. Output findings as a numbered list with severity " +
        "[CRITICAL], [WARNING], or [SUGGESTION].\n\n" +
        "CHECK FOR:\n" +
        "- IDisposable resources not properly disposed (missing using/await using)\n" +
        "- Async/await deadlock patterns (sync-over-async, .Result, .Wait())\n" +
        "- DI lifetime mismatches (scoped service injected into singleton)\n" +
        "- CancellationToken not propagated through async call chains\n" +
        "- ArgumentNullException.ThrowIfNull missing on public method parameters\n" +
        "- Collections exposed as mutable (List<T> instead of IReadOnlyList<T>)\n\n" +
        "DO NOT FLAG:\n" +
        "- Issues in unchanged code outside the diff\n" +
        "- Business logic correctness (already covered by previous review)\n" +
        "- Style or formatting preferences\n" +
        "- Missing nullable annotations on internal code\n" +
        "- Test code conventions\n" +
        "- Suggestions to add more abstractions or interfaces\n" +
        "- Untracked or unstaged files shown by `git status` (the pipeline auto-stages all files before commit)\n\n" +
        "Do NOT fix anything. Only report findings.";

    public const string SecurityReview =
        "Review the changes for security issues. The previous review covered correctness " +
        "— do not duplicate those findings. Output findings as a numbered list with severity " +
        "[CRITICAL], [WARNING], or [SUGGESTION].\n\n" +
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
        "- Issues already covered by previous reviews\n" +
        "- Theoretical attacks requiring physical access or pre-existing compromise\n" +
        "- Missing HTTPS enforcement (infrastructure concern, not code)\n" +
        "- Test code, sample data, or placeholder values in test fixtures\n" +
        "- Dependency vulnerabilities (covered by external CI)\n" +
        "- General code quality or style issues\n" +
        "- Untracked or unstaged files shown by `git status` (the pipeline auto-stages all files before commit)\n\n" +
        "Do NOT fix anything. Only report findings.";

    public const string AcceptanceCriteriaReview =
        "Review the changes against the acceptance criteria from the original issue. " +
        "The previous reviews covered code correctness, .NET patterns, and security — " +
        "do not duplicate those findings.\n\n" +
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
        "- Code quality issues (already covered by previous reviews)\n" +
        "- .NET-specific patterns (already covered by previous reviews)\n" +
        "- Security issues (already covered by previous reviews)\n" +
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
}
