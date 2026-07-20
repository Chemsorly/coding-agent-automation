using System.Text;
using System.Text.RegularExpressions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services.Prompts;

/// <summary>
/// Builds prompts for the agent from issue details and parsed issue data.
/// This is used by the orchestrator — the agent provider receives pre-built prompts.
/// </summary>
public static class PromptBuilder
{
    /// <summary>
    /// Standardized thoroughness instruction appended to all review and analysis prompts.
    /// Injected at the builder level so it applies to both default and custom/externalized prompts.
    /// </summary>
    internal const string ThoroughnessFooter =
        "\n## Thoroughness\n\n" +
        "Be exhaustive within your domain. Don't stop at the first finding — scan the entire scope systematically. " +
        "Consider what's MISSING (untested paths, unhandled errors, missing validation) as much as what's wrong. " +
        "Check interactions between changed components — a change in one area may break assumptions in another.";

    /// <summary>
    /// Scope fence prepended to review prompts. Exploits primacy effect for behavioral compliance.
    /// Uses affirmative framing (distinct from the negation-framed constraint at prompt end).
    /// Restricts source/project files only — does not conflict with designated output file writes.
    /// </summary>
    internal const string ReviewScopeFence =
        "Report findings only. Do not modify source code or project files.\n\n";

    /// <summary>
    /// Scope fence prepended to analysis prompts.
    /// </summary>
    internal const string AnalysisScopeFence =
        "Analysis only. Do not modify source code. Write output to designated files only.\n\n";

    /// <summary>
    /// Anti-fabrication clause for implementation prompts. Addresses documented 5-26% API
    /// hallucination rate. Kept brief per Compliance Gap research (arXiv:2605.01771).
    /// </summary>
    internal const string VerificationClause =
        "\n## Verification Before Use\n\n" +
        "- Confirm method exists and signature matches before calling.\n" +
        "- Verify import targets and file paths exist before referencing.\n" +
        "- Confirm parameter names, types, and order match the declaration.\n" +
        "- No stubs or placeholders. Every method must contain real logic.\n" +
        "- If referenced code is missing, search for it. Adapt — never fabricate.\n";

    /// <summary>
    /// Calibration footer for code review prompts. Addresses LLM over-rejection bias
    /// (arXiv:2508.12358) by requiring burden-of-proof for severity levels, signaling that
    /// 0 findings is valid, and debiasing against developer framing (arXiv:2603.18740).
    /// Injected at the builder level so it applies to both default and custom review prompts.
    /// </summary>
    internal const string ReviewCalibrationFooter =
        "\n## Calibration\n\n" +
        "SEVERITY GUIDELINES:\n" +
        "- [CRITICAL] requires a concrete scenario: an exact input that triggers failure, " +
        "or for concurrency issues, a specific interleaving that produces inconsistent state. " +
        "If you cannot construct such a scenario, downgrade to [WARNING].\n" +
        "- [WARNING] requires identifying specific code that deviates from best practice or could " +
        "fail under documented conditions. Vague \"might cause issues\" is not sufficient.\n" +
        "- [SUGGESTION] is for improvements that don't indicate a defect.\n\n" +
        "ACCURACY OVER THOROUGHNESS:\n" +
        "Scan the entire diff systematically, but only report findings you can support with " +
        "a specific code path or scenario. A review with 0 findings is a valid outcome when " +
        "the code is correct. Flagging correct code wastes engineering time.\n\n" +
        "Judge the CODE DIFF only. Ignore commit messages, branch names, PR titles, or any " +
        "framing about the developer's intent. A defect is present or absent regardless of " +
        "how the change is described.";

    /// <summary>
    /// Constructs an analysis-only prompt. The agent examines the codebase in context of the
    /// issue and writes its recommendation to .agent/analysis.md without making any other changes.
    /// The configurable analysis instructions are prepended, followed by pipeline mechanics.
    /// </summary>
    public static string BuildAnalysisPrompt(string analysisInstructions, IssueDetail issue, ParsedIssue parsed,
        bool brainContextWritten = false, int imageCount = 0)
    {
        ArgumentNullException.ThrowIfNull(analysisInstructions);
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(parsed);

        var sb = new StringBuilder();

        // Scope fence (non-overridable, exploits primacy effect)
        sb.AppendLine(AnalysisScopeFence);

        // Configurable instructions
        sb.AppendLine(analysisInstructions);
        sb.AppendLine();
        sb.AppendLine(ThoroughnessFooter);
        sb.AppendLine();

        // Pipeline mechanics (non-configurable)
        sb.AppendLine("Do NOT implement any changes. Only analyze and recommend.");
        sb.AppendLine();
        sb.AppendLine($"Write your analysis to the file `{AgentWorkspacePaths.AnalysisFilePath}` in the workspace. Do NOT print the analysis to stdout — only write it to that file.");
        sb.AppendLine();
        sb.AppendLine("Use sub-agents to cover more ground and provide a thorough analysis. For example, delegate parallel investigations to explore different parts of the codebase — one sub-agent could examine the data layer while another looks at the UI components, or one traces the call chain while another checks for test coverage gaps. This produces a more complete picture than a single-threaded read-through.");
        sb.AppendLine();

        AppendIssueContext(sb, issue, parsed);

        sb.AppendLine($"After writing your analysis to `{AgentWorkspacePaths.AnalysisFilePath}`, also write a structured assessment to `{AgentWorkspacePaths.AnalysisAssessmentFilePath}` with this exact JSON schema:");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"recommendation\": \"ready\",");
        sb.AppendLine("  \"reason\": \"Issue is well-scoped with clear acceptance criteria\",");
        sb.AppendLine("  \"concerns\": [\"Non-blocking concern\"],");
        sb.AppendLine("  \"blockingIssues\": [],");
        sb.AppendLine("  \"plannedApproach\": \"One-line implementation strategy\",");
        sb.AppendLine("  \"estimatedComplexity\": \"moderate\"");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Set `recommendation` to:");
        sb.AppendLine("- `\"ready\"` if the issue is clear, well-scoped, and you have a concrete implementation plan.");
        sb.AppendLine("- `\"not_ready\"` if the issue is too vague, contradictory, has hard blockers, requires information you can't determine from the codebase, OR if the scope is too broad for a single agent run (heuristic: changes affecting >30 files or spanning >3 distinct projects). When rejecting for scope, include splitting recommendations in `blockingIssues` (e.g., \"Split by concern: UI changes, data layer, test updates\"). Add any blocking issues to `blockingIssues`.");
        sb.AppendLine("- `\"wont_do\"` if, after analyzing the codebase, you determine no code changes are needed. This includes: bugs that can't be reproduced, issues that are already fixed, features that are already implemented, or behavior that is working as designed. Explain your reasoning in `reason`.");
        sb.AppendLine();

        if (brainContextWritten)
        {
            sb.AppendLine($"Project knowledge and conventions are at `{AgentWorkspacePaths.BrainContextFilePath}` — consult it for coding standards and patterns.");
            sb.AppendLine();
        }

        sb.AppendLine($"Analyze the workspace now and write your recommendation to `{AgentWorkspacePaths.AnalysisFilePath}`.");

        if (imageCount > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"This issue includes {imageCount} screenshot(s)/image(s) in `.agent/images/` — examine them for visual context about the problem or expected behavior.");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Constructs a prompt for the isolated analysis review agent. The reviewer reads the
    /// analysis artifacts and the issue context, explores the codebase independently, and
    /// writes findings to .agent/analysis-review.md.
    /// </summary>
    public static string BuildAnalysisReviewPrompt(string reviewInstructions, IssueDetail issue, ParsedIssue parsed)
    {
        ArgumentNullException.ThrowIfNull(reviewInstructions);
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(parsed);

        var sb = new StringBuilder();

        // Scope fence (non-overridable, exploits primacy effect)
        sb.AppendLine(ReviewScopeFence);

        sb.AppendLine(reviewInstructions);
        sb.AppendLine();
        sb.AppendLine(ThoroughnessFooter);
        sb.AppendLine();
        sb.AppendLine($"Write your findings to `{AgentWorkspacePaths.AnalysisReviewFilePath}`. Do NOT print findings to stdout — only write them to that file.");
        sb.AppendLine();
        sb.AppendLine("Do NOT modify `.agent/analysis.md` or `.agent/analysis-assessment.json` — only write your review.");
        sb.AppendLine();

        AppendIssueContext(sb, issue, parsed);

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Constructs a refinement prompt sent back to the original analysis session.
    /// The agent reads the review feedback and updates its analysis and assessment.
    /// </summary>
    public static string BuildAnalysisRefinementPrompt(string refinementInstructions)
    {
        ArgumentNullException.ThrowIfNull(refinementInstructions);

        var sb = new StringBuilder();

        sb.AppendLine(refinementInstructions);
        sb.AppendLine();
        sb.AppendLine($"The review findings are at `{AgentWorkspacePaths.AnalysisReviewFilePath}`. Read them, then rewrite `{AgentWorkspacePaths.AnalysisFilePath}` and update `{AgentWorkspacePaths.AnalysisAssessmentFilePath}` as needed.");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Constructs a prompt containing action instructions, the issue title, description,
    /// and all acceptance criteria. The prompt explicitly instructs the agent to implement
    /// the changes in the workspace, not just analyze them.
    /// The configurable implementation instructions are prepended, followed by pipeline mechanics.
    /// </summary>
    public static string BuildPrompt(string implementationInstructions, IssueDetail issue, ParsedIssue parsed,
        string? brainWriteInstructions = null, bool brainContextWritten = false, int imageCount = 0)
    {
        ArgumentNullException.ThrowIfNull(implementationInstructions);
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(parsed);

        var sb = new StringBuilder();

        // Configurable instructions
        sb.AppendLine(implementationInstructions);
        sb.AppendLine(VerificationClause);

        // Pipeline mechanics (non-configurable)
        sb.AppendLine(PipelineConstants.GitRestrictionFull);
        sb.AppendLine($"The analysis for this issue is at `{AgentWorkspacePaths.AnalysisFilePath}` — read it before implementing.");
        sb.AppendLine();

        AppendIssueContext(sb, issue, parsed);

        if (brainContextWritten)
        {
            sb.AppendLine($"Project knowledge and conventions are at `{AgentWorkspacePaths.BrainContextFilePath}` — consult it for coding standards and patterns.");
            sb.AppendLine();
        }

        sb.AppendLine("Implement these changes now.");

        if (!string.IsNullOrEmpty(brainWriteInstructions))
        {
            sb.AppendLine();
            sb.AppendLine(brainWriteInstructions);
        }

        if (imageCount > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"This issue includes {imageCount} screenshot(s)/image(s) in `.agent/images/` — examine them for visual context about the problem or expected behavior.");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Constructs a code review prompt that includes the original issue context so the
    /// reviewing agent does not rely solely on conversation history for requirements.
    /// The configurable review instructions are prepended, followed by the full issue
    /// details (title, description, requirements, acceptance criteria, and comments).
    /// </summary>
    public static string BuildReviewPrompt(string reviewInstructions, IssueDetail issue,
        ParsedIssue parsed, string findingsFilePath, bool inlineCommentsEnabled = false, bool hasLinkedPr = false, int imageCount = 0)
    {
        ArgumentNullException.ThrowIfNull(reviewInstructions);
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(findingsFilePath);

        var sb = new StringBuilder();

        // Scope fence (non-overridable, exploits primacy effect)
        sb.AppendLine(ReviewScopeFence);

        sb.AppendLine("You are reviewing code changes made by another agent. You have no prior context about how or why these changes were made — judge purely on correctness, security, and adherence to requirements.");
        sb.AppendLine();
        sb.AppendLine("The diff has been pre-computed for you. Read these files to understand the changes:");
        sb.AppendLine($"- `{AgentWorkspacePaths.DiffStatFilePath}` — summary of changed files with line counts (read this FIRST to triage)");
        sb.AppendLine($"- `{AgentWorkspacePaths.FullDiffFilePath}` — full diff between origin/main and the working tree");
        sb.AppendLine();
        sb.AppendLine("IMPORTANT: Do NOT run `git diff` yourself — the diff is already captured in the files above. Read the diff-stat first to identify which files are relevant to your review focus, then selectively read sections of the full diff for those files. You do NOT need to read the entire full-diff file.");
        sb.AppendLine();
        sb.AppendLine("You may also run read-only git commands for additional context:");
        sb.AppendLine("- `git log origin/main..HEAD --oneline` — shows commits on the branch");
        sb.AppendLine("- `git status` — shows working tree state (NOTE: untracked files are expected — the pipeline automatically stages and commits ALL new/modified files before creating the PR)");
        sb.AppendLine();

        sb.AppendLine(reviewInstructions);
        sb.AppendLine();
        sb.AppendLine(ThoroughnessFooter);
        sb.AppendLine();
        sb.AppendLine(ReviewCalibrationFooter);
        sb.AppendLine();

        // PR conversation context reference
        if (hasLinkedPr)
        {
            sb.AppendLine($"PR conversation and prior review context is available at `{AgentWorkspacePaths.PrConversationContextFilePath}`.");
            sb.AppendLine("Comments marked [HUMAN] represent feedback from team members — treat these as authoritative context about intentional design decisions. Comments from the PR author are marked [HUMAN/AUTHOR].");
            sb.AppendLine("If prior review findings are present, verify whether they were addressed by the current changes AND look for new issues.");
            sb.AppendLine();
        }

        sb.AppendLine($"Write your findings to the file `{findingsFilePath}` in the workspace. Do NOT print the findings to stdout — only write them to that file.");
        sb.AppendLine();
        sb.AppendLine(PipelineConstants.GitRestrictionFull);
        sb.AppendLine();
        sb.AppendLine("Below is the original issue for reference. Review the changes against these requirements.");
        sb.AppendLine();

        AppendIssueContext(sb, issue, parsed);

        if (inlineCommentsEnabled)
        {
            sb.AppendLine();
            AppendStructuredOutputInstructions(sb);
        }

        if (imageCount > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"This issue includes {imageCount} screenshot(s)/image(s) in `.agent/images/` — examine them for visual context about the problem or expected behavior.");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Markers identifying bot-generated comments that should be excluded from context.</summary>
    // NOTE: [ARC-08a] Gate comment markers rely on exact substring match — if a human edits the comment to remove the HTML marker, the gate comment leaks into prompt context
    internal static readonly string[] ExcludedCommentMarkers =
    [
        CommentMarkers.AnalysisHeader,
        CommentMarkers.GateRejection,
        CommentMarkers.GateWontDo,
        CommentMarkers.IssueFeedback
    ];

    /// <summary>
    /// Constructs a fix prompt that references the review findings file instead of inlining
    /// the raw findings. The agent reads .agent/review-findings.md on demand.
    /// </summary>
    public static string BuildFixPrompt(string fixInstructions)
    {
        ArgumentNullException.ThrowIfNull(fixInstructions);

        var sb = new StringBuilder();
        sb.AppendLine(fixInstructions);
        sb.AppendLine();
        sb.AppendLine(PipelineConstants.GitRestrictionFull);
        sb.AppendLine();
        sb.AppendLine($"Review findings have been written to `{AgentWorkspacePaths.ReviewFindingsFilePath}`. Read the file, then fix only items marked [CRITICAL].");
        return sb.ToString().TrimEnd();
    }

    private static void AppendIssueContext(StringBuilder sb, IssueDetail issue, ParsedIssue parsed,
        string? issueContextPathOverride = null)
    {
        var contextPath = issueContextPathOverride ?? AgentWorkspacePaths.IssueContextFilePath;
        sb.AppendLine($"# Issue #{issue.Identifier}: {issue.Title}");
        sb.AppendLine();
        sb.AppendLine($"The full issue description and discussion thread are at `{contextPath}` — read it for context.");
        sb.AppendLine();

        if (parsed.AcceptanceCriteria.Count > 0)
        {
            sb.AppendLine("## Acceptance Criteria");
            foreach (var criterion in parsed.AcceptanceCriteria)
            {
                sb.AppendLine($"- {criterion}");
            }
            sb.AppendLine();
        }
    }

    /// <summary>
    /// Builds the markdown content for the issue context file (.agent/issue-context.md).
    /// Contains the full issue description, requirements, and filtered comments.
    /// When downloaded images are provided, replaces inline image URLs with local paths
    /// and appends an attached images table.
    /// </summary>
    public static string BuildIssueContextFileContent(IssueDetail issue, ParsedIssue parsed,
        IReadOnlyList<IssueComment>? comments = null, IReadOnlyList<DownloadedImage>? downloadedImages = null)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(parsed);

        var hasImages = downloadedImages is { Count: > 0 };
        var urlToLocalPath = hasImages
            ? downloadedImages!.ToDictionary(d => d.Reference.Url, d => d.LocalPath)
            : null;

        var sb = new StringBuilder();
        sb.AppendLine($"# Issue: {issue.Title}");
        sb.AppendLine();
        sb.AppendLine("## Description");

        if (hasImages)
            AppendProcessedText(sb, issue.Description, urlToLocalPath!);
        else
            sb.AppendLine(issue.Description);

        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(parsed.RequirementsSection))
        {
            sb.AppendLine("## Requirements");
            sb.AppendLine(parsed.RequirementsSection);
            sb.AppendLine();
        }

        if (parsed.AcceptanceCriteria.Count > 0)
        {
            sb.AppendLine("## Acceptance Criteria");
            foreach (var criterion in parsed.AcceptanceCriteria)
            {
                sb.AppendLine($"- {criterion}");
            }
            sb.AppendLine();
        }

        AppendComments(sb, comments);

        if (hasImages)
            AppendAttachedImagesTable(sb, downloadedImages!);

        return sb.ToString().TrimEnd();
    }

    private static void AppendComments(StringBuilder sb, IReadOnlyList<IssueComment>? comments)
    {
        if (comments == null || comments.Count == 0)
            return;

        var filtered = comments
            .Where(c => !ExcludedCommentMarkers.Any(marker => c.Body.Contains(marker)))
            .TakeLast(PipelineConstants.OutputTailLineCount)
            .ToList();

        if (filtered.Count == 0)
            return;

        sb.AppendLine("## Comments");
        sb.AppendLine("The following comments were left on the issue and may contain clarifications, updated requirements, or stakeholder feedback.");
        sb.AppendLine();
        foreach (var comment in filtered)
        {
            sb.AppendLine($"**@{comment.Author}** ({comment.CreatedAt:yyyy-MM-dd}):");
            sb.AppendLine(comment.Body);
            sb.AppendLine();
        }
    }

    /// <summary>
    /// Regex matching inline markdown images: ![alt](url) or ![alt](url "title")
    /// </summary>
    private static readonly Regex InlineImageRegex = new(
        @"!\[([^\]]*)\]\(([^)\s]+)(?:\s+""[^""]*"")?\)",
        RegexOptions.Compiled);

    /// <summary>
    /// Regex matching HTML img tags with src attribute (any attribute order).
    /// </summary>
    private static readonly Regex HtmlImgSrcRegex = new(
        @"<img\s[^>]*\bsrc\s*=\s*[""']([^""']+)[""'][^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Processes text line-by-line with fence-tracking state machine.
    /// Replaces image URLs with local paths outside code blocks.
    /// </summary>
    private static void AppendProcessedText(StringBuilder sb, string text, Dictionary<string, string> urlToLocalPath)
    {
        var lines = text.Split('\n');
        string? fenceDelimiter = null; // null = not in fence, "```" or "~~~" = which opened it

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var trimmed = line.TrimStart();

            if (fenceDelimiter is null)
            {
                // Not inside a fence — check if this line opens one
                if (trimmed.StartsWith("```"))
                {
                    fenceDelimiter = "```";
                    sb.AppendLine(line);
                    continue;
                }
                if (trimmed.StartsWith("~~~"))
                {
                    fenceDelimiter = "~~~";
                    sb.AppendLine(line);
                    continue;
                }

                // Outside code blocks — perform replacements
                var processedLine = ReplaceInlineImages(line, urlToLocalPath);

                // Check for HTML <img> tags and add comment below if URL matched
                var imgMatch = HtmlImgSrcRegex.Match(processedLine);
                if (imgMatch.Success && urlToLocalPath.TryGetValue(imgMatch.Groups[1].Value, out var localPath))
                {
                    sb.AppendLine(processedLine);
                    sb.AppendLine($"<!-- Downloaded as {localPath} -->");
                }
                else
                {
                    sb.AppendLine(processedLine);
                }
            }
            else
            {
                // Inside a fence — check if this line closes it (must match opening delimiter)
                if (trimmed.StartsWith(fenceDelimiter))
                {
                    fenceDelimiter = null;
                }
                sb.AppendLine(line);
            }
        }
    }

    /// <summary>
    /// Replaces ![alt](url) patterns with ![alt](localPath) when url is in the lookup.
    /// </summary>
    private static string ReplaceInlineImages(string line, Dictionary<string, string> urlToLocalPath)
    {
        return InlineImageRegex.Replace(line, match =>
        {
            var altText = match.Groups[1].Value;
            var url = match.Groups[2].Value;

            if (urlToLocalPath.TryGetValue(url, out var localPath))
                return $"![{altText}]({localPath})";

            return match.Value;
        });
    }

    /// <summary>
    /// Appends the ## Attached Images table listing all downloaded images.
    /// </summary>
    private static void AppendAttachedImagesTable(StringBuilder sb, IReadOnlyList<DownloadedImage> downloadedImages)
    {
        sb.AppendLine("## Attached Images");
        sb.AppendLine();
        sb.AppendLine("| # | File | Alt Text | Source |");
        sb.AppendLine("|---|------|----------|--------|");

        for (var i = 0; i < downloadedImages.Count; i++)
        {
            var img = downloadedImages[i];
            var source = img.Reference.SourceType == ImageSourceType.Body
                ? "Body"
                : $"Comment #{img.Reference.SourceIndex}";
            sb.AppendLine($"| {i + 1} | {img.LocalFilename} | {img.Reference.AltText} | {source} |");
        }
    }

    /// <summary>
    /// Appends structured output format instructions to the review prompt.
    /// These instructions guide the review agent to output findings in a parseable format
    /// with file:line references for inline comment placement.
    /// </summary>
    private static void AppendStructuredOutputInstructions(StringBuilder sb)
    {
        sb.AppendLine("## Output Format");
        sb.AppendLine();
        sb.AppendLine("Format each finding on its own line using this structure:");
        sb.AppendLine("[SEVERITY] path/to/file.ext:LINE — description of the issue");
        sb.AppendLine();
        sb.AppendLine("Where:");
        sb.AppendLine("- SEVERITY is one of: CRITICAL, WARNING, SUGGESTION");
        sb.AppendLine("- path is relative to the repository root using forward slashes");
        sb.AppendLine("- LINE is the 1-based line number in the file");
        sb.AppendLine("- description explains the finding");
        sb.AppendLine();
        sb.AppendLine("Example:");
        sb.AppendLine("[CRITICAL] src/Service.cs:42 — Null reference possible when input is not validated");
        sb.AppendLine("[WARNING] src/Controllers/UserController.cs:15 — Missing input validation on email parameter");
        sb.AppendLine();
        sb.AppendLine("For findings without a specific file location:");
        sb.AppendLine("[WARNING] — General observation about architecture");
    }

    /// <summary>
    /// Builds a cleanup prompt for the PreparingForPullRequest step.
    /// The agent cleans up the working directory without making functional changes.
    /// </summary>
    public static string BuildCleanupPrompt()
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Pre-Pull Request Cleanup");
        sb.AppendLine();
        sb.AppendLine("The implementation is complete and quality gates have passed. Before creating the pull request, clean up the working directory.");
        sb.AppendLine();
        sb.AppendLine("Do the following:");
        sb.AppendLine("- Remove any debug/temporary code added during development (e.g., debug print statements, TODO-REMOVE comments)");
        sb.AppendLine("- Clean up unused imports and dead code");
        sb.AppendLine("- Ensure consistent formatting");
        sb.AppendLine("- Remove any test scaffolding that isn't part of the deliverable");
        sb.AppendLine("- Verify no sensitive data or credentials were accidentally added");
        sb.AppendLine();
        sb.AppendLine("Do NOT make functional changes — cleanup only.");
        sb.AppendLine();
        sb.AppendLine(PipelineConstants.GitRestrictionShort);
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds the brain context section to inject into agent prompts.
    /// Returns empty string when brain context is not available.
    /// </summary>
    public static string BuildBrainContextSection(
        bool brainAvailable,
        string? projectName = null,
        string? techStack = null)
    {
        if (!brainAvailable)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("## Brain Repository — Accumulated Knowledge");
        sb.AppendLine();
        sb.AppendLine("A `.brain/` directory is available in this workspace containing accumulated project knowledge");
        sb.AppendLine("from previous pipeline runs. It is a SEPARATE Git repository — do NOT reference `.brain/` files");
        sb.AppendLine("in code repository commits, commit messages, or pull request descriptions.");
        sb.AppendLine();
        sb.AppendLine("### How to Retrieve Brain Knowledge");
        sb.AppendLine();
        sb.AppendLine("Delegate brain exploration to a subagent. Do NOT read brain files inline in your main session.");
        sb.AppendLine("Use a subagent to search the brain and return only relevant entries:");
        sb.AppendLine();
        sb.AppendLine("1. Invoke a subagent with a prompt like:");
        sb.AppendLine("   \"Read `.brain/AGENTS.md` for the structure. Then find entries in `.brain/` relevant to: {your issue summary}.");
        sb.AppendLine("   Check for a `SKILL.md` in the relevant project folder first — it contains a distilled summary.");

        if (!string.IsNullOrWhiteSpace(projectName))
            sb.AppendLine($"   Check `.brain/projects/{projectName}/` for project-specific knowledge.");

        if (!string.IsNullOrWhiteSpace(techStack))
            sb.AppendLine($"   Check `.brain/technology/` for knowledge about: {techStack}.");

        sb.AppendLine("   Return the full text of the 5-8 most relevant entries. Only read, do not modify any files.\"");
        sb.AppendLine("2. Use the subagent's returned knowledge to inform your work.");
        sb.AppendLine("3. If the subagent finds nothing relevant, proceed without brain context.");
        sb.AppendLine();
        sb.AppendLine("This keeps your main context clean and avoids spending tool calls on navigation.");
        sb.AppendLine();
        sb.AppendLine("Do NOT run git commands (commit, push, pull) inside the `.brain/` directory.");
        sb.AppendLine("The orchestrator handles all git operations on the brain repository.");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds a reflection prompt that asks the agent to review the entire run and
    /// update .brain/ knowledge files with lessons learned, including failures and
    /// review findings. Called after quality gates pass but before brain post-run sync.
    /// When a history service is provided, appends feedback collection questions grounded
    /// in retry context, elapsed time, and previously-used category labels.
    /// </summary>
    public static string BuildReflectionPrompt(
        PipelineRun run,
        string? issueTitle = null,
        string? projectName = null)
    {
        ArgumentNullException.ThrowIfNull(run);

        var sb = new StringBuilder();
        sb.AppendLine("## Reflect on This Run and Update Brain Knowledge");
        sb.AppendLine();
        sb.AppendLine("The implementation is complete and quality gates have passed.");
        sb.AppendLine("Now it's time to reflect on this run and update the brain repository.");
        sb.AppendLine("Read `.brain/AGENTS.md` for instructions on what to write and where.");
        sb.AppendLine();

        // Run context — data the agent can't get elsewhere
        sb.AppendLine("### Run Context");
        sb.AppendLine($"- **Run ID:** {run.RunId}");
        sb.AppendLine($"- **Date:** {DateTime.UtcNow:yyyy-MM-dd}");
        sb.AppendLine($"- **Issue:** #{run.IssueIdentifier}" + (!string.IsNullOrWhiteSpace(issueTitle) ? $" — {issueTitle}" : ""));
        if (!string.IsNullOrWhiteSpace(projectName))
            sb.AppendLine($"- **Project:** {projectName}");
        sb.AppendLine($"- **Outcome:** success");
        if (run.RetryCount > 0)
            sb.AppendLine($"- **Quality gate retries:** {run.RetryCount}");
        if (run.CodeReviewIterationsCompleted > 0)
            sb.AppendLine($"- **Code review iterations:** {run.CodeReviewIterationsCompleted}");
        if (run.CodeReviewCriticalCount > 0 || run.CodeReviewWarningCount > 0)
            sb.AppendLine($"- **Review findings:** {run.CodeReviewCriticalCount} critical, {run.CodeReviewWarningCount} warnings, {run.CodeReviewSuggestionCount} suggestions");

        // Retry errors
        var retryErrors = run.RetryErrors.ToArray();
        if (retryErrors.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Quality Gate Failures (before passing)");
            foreach (var error in retryErrors)
                sb.AppendLine($"- {error}");
        }

        // Review agent findings
        if (run.CodeReviewAgentFindings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Code Review Findings");
            foreach (var (agent, findings) in run.CodeReviewAgentFindings)
            {
                sb.AppendLine($"**{agent}:**");
                sb.AppendLine(findings.Length > 2000 ? findings[..2000] + "\n[truncated]" : findings);
            }
        }

        sb.AppendLine();
        sb.AppendLine("Do NOT commit these changes — the orchestrator handles git operations.");
        sb.AppendLine("Do NOT modify any source code files — only update `.brain/` files.");
        sb.AppendLine();
        sb.AppendLine("### Brain Entry Citations");
        sb.AppendLine();
        sb.AppendLine("In your session log, include a `## Brain Entries Referenced` section.");
        sb.AppendLine("List each `.brain/` entry you consulted during this run with its usefulness:");
        sb.AppendLine("- `{file}#{entry-title}` — **used, helpful** | **read, not applicable** | **used, outdated**");
        sb.AppendLine();
        sb.AppendLine("This data helps the consolidation process identify high-value entries and prune unused ones.");
        sb.AppendLine("If you did not consult any brain entries, write `No brain entries referenced.`");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds a rework-specific prompt containing merge conflict info and/or review feedback.
    /// Returns null if there is nothing to rework (no conflicts, no comments, and PR is not a draft),
    /// signaling the pipeline to skip code generation.
    /// When isDraft is true (previous failed run), always returns a prompt with draft context.
    /// </summary>
    public static string? BuildReworkPrompt(
        IReadOnlyList<string> conflictFiles,
        IReadOnlyList<PullRequestReviewComment> reviewComments,
        bool isDraft = false,
        bool forceResolved = false)
    {
        if (conflictFiles.Count == 0 && reviewComments.Count == 0 && !isDraft)
            return null; // Nothing to rework — skip code gen

        var sb = new StringBuilder();
        sb.AppendLine("You are reworking an existing pull request. " +
            "Address the issues described below, then verify your changes compile and pass tests.");
        sb.AppendLine();

        if (isDraft && conflictFiles.Count == 0 && reviewComments.Count == 0)
        {
            sb.AppendLine("## Draft PR — Previous Failed Run");
            sb.AppendLine();
            sb.AppendLine("This is a draft PR from a previous failed run. " +
                "Review the code for build/test issues and fix them.");
            sb.AppendLine();
        }

        if (conflictFiles.Count > 0 && forceResolved)
        {
            sb.AppendLine("## Force-Rebased Files (Main Wins)");
            sb.AppendLine();
            sb.AppendLine("The branch was rebased onto the latest main. The following files had " +
                "conflicts that were **force-resolved by accepting main's version** (incoming wins). " +
                "Your previous branch changes for these files were discarded.");
            sb.AppendLine();
            sb.AppendLine("You must **re-implement your changes** for these files on top of the " +
                "current main state. The files currently contain main's version — there are NO " +
                "conflict markers. Review the issue requirements and apply the necessary changes:");
            sb.AppendLine();
            foreach (var file in conflictFiles)
                sb.AppendLine($"- `{file}`");
            sb.AppendLine();
        }
        else if (conflictFiles.Count > 0)
        {
            sb.AppendLine("## Merge Conflicts");
            sb.AppendLine();
            sb.AppendLine("The following files have merge conflict markers " +
                "(`<<<<<<<`, `=======`, `>>>>>>>`) that must be resolved:");
            sb.AppendLine();
            foreach (var file in conflictFiles)
                sb.AppendLine($"- `{file}`");
            sb.AppendLine();
            sb.AppendLine("Open each file, resolve all conflict markers, and ensure the " +
                "merged result is correct.");
            sb.AppendLine();
        }

        if (reviewComments.Count > 0)
        {
            sb.AppendLine("## Review Feedback");
            sb.AppendLine();
            sb.AppendLine($"Review comments have been left on the pull request. " +
                $"Read `{AgentWorkspacePaths.PrConversationContextFilePath}` for the full conversation context " +
                $"(including review bodies, inline comments, and discussion). Address all human feedback.");
            sb.AppendLine();
        }

        sb.AppendLine($"Refer to `{AgentWorkspacePaths.IssueContextFilePath}` for the full issue description and comments.");
        sb.AppendLine();
        sb.AppendLine(PipelineConstants.GitRestrictionShort);

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds the brain write instructions section for the implementation prompt.
    /// Returns empty string when brain context is not available or when BrainReadOnly is true.
    /// </summary>
    public static string BuildBrainWriteInstructions(
        bool brainAvailable, string runId, string issueIdentifier,
        bool brainReadOnly = false)
    {
        if (!brainAvailable || brainReadOnly)
            return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("## Brain Repository — Write Back What You Learned");
        sb.AppendLine();
        sb.AppendLine("After completing your work, write lessons learned back to the `.brain/` directory:");
        sb.AppendLine("- General pitfalls and solutions → `.brain/general/lessons-learned.md`");
        sb.AppendLine("- Technology-specific discoveries → `.brain/technology/{tech}.md`");
        sb.AppendLine("- Project-specific knowledge → `.brain/projects/{project}/`");
        sb.AppendLine($"- Session log for this run → `.brain/sessions/{DateTime.UtcNow:yyyy-MM-dd}_{runId}.md`");
        sb.AppendLine("- Update the operation log → `.brain/log.md`");
        sb.AppendLine();
        sb.AppendLine("APPEND to existing files — never overwrite. Follow the entry format in `.brain/AGENTS.md`.");
        sb.AppendLine("Include source attribution with typed source tags ([docs], [community], [experience], [verified]).");
        sb.AppendLine("You may create new files and folders as needed.");
        sb.AppendLine();
        sb.AppendLine("### Brain Entry Citations");
        sb.AppendLine();
        sb.AppendLine("In your session log, include a `## Brain Entries Referenced` section listing which");
        sb.AppendLine("`.brain/` entries you consulted during this run and their usefulness:");
        sb.AppendLine();
        sb.AppendLine("```markdown");
        sb.AppendLine("## Brain Entries Referenced");
        sb.AppendLine();
        sb.AppendLine("- `technology/dotnet.md#entry-title` — **used, helpful** — brief reason");
        sb.AppendLine("- `general/lessons-learned.md#entry-title` — **read, not applicable** — why it didn't apply");
        sb.AppendLine("- `technology/blazor.md#entry-title` — **used, outdated** — what was wrong");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("Use one of: `used, helpful` | `read, not applicable` | `used, outdated` | `not consulted`.");
        sb.AppendLine("Only list entries you actually read. If you did not consult the brain, write `No brain entries referenced.`");
        sb.AppendLine();
        sb.AppendLine("Do NOT commit these changes — the orchestrator handles git operations.");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds the prompt for the acceptance criteria compliance agent.
    /// Instructs the agent to evaluate the implementation and write structured JSON.
    /// </summary>
    public static string BuildAcceptanceCriteriaPrompt(string instructions)
    {
        ArgumentNullException.ThrowIfNull(instructions);

        var sb = new StringBuilder();

        // Scope fence (non-overridable, exploits primacy effect)
        sb.AppendLine(ReviewScopeFence);

        sb.AppendLine(instructions);
        sb.AppendLine();
        sb.AppendLine($"Write your assessment to `{AgentWorkspacePaths.AcceptanceCriteriaFilePath}`. Do NOT print results to stdout — only write the JSON file.");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds a prompt that asks the agent to produce a structured PR description
    /// summarizing what changed and why.
    /// </summary>
    public static string BuildPrDescriptionPrompt(PipelineRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        var sb = new StringBuilder();
        sb.AppendLine("## Generate a Pull Request Description");
        sb.AppendLine();
        sb.AppendLine("Write a structured summary of the changes you made. Output ONLY the markdown below — no file writes, no code changes.");
        sb.AppendLine();
        sb.AppendLine("Use this format:");
        sb.AppendLine();
        sb.AppendLine("### Summary");
        sb.AppendLine("2-3 sentences explaining what was done and why.");
        sb.AppendLine();
        sb.AppendLine("### Approach");
        sb.AppendLine("Brief description of the implementation strategy.");
        sb.AppendLine();
        sb.AppendLine("### Key Changes");
        sb.AppendLine("File-level walkthrough of the most important changes (not every file — focus on the interesting ones).");
        sb.AppendLine();
        sb.AppendLine("### Breaking Changes");
        sb.AppendLine("Only if applicable. Omit this section entirely if there are none.");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"**Issue:** #{run.IssueIdentifier} — {run.IssueTitle}");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds a prompt that asks the agent to summarize code review findings into a
    /// change summary and review verdict.
    /// </summary>
    /// <param name="diffStat">The git diff stat output showing files changed.</param>
    /// <param name="issueTitle">The issue title for context.</param>
    /// <param name="findings">Concatenated per-agent findings text.</param>
    public static string BuildReviewSummaryPrompt(string diffStat, string issueTitle, string findings)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are summarizing a code review. Read the findings below and produce two sections:");
        sb.AppendLine();
        sb.AppendLine("## Change Summary");
        sb.AppendLine("In 2-3 sentences, describe what this PR does. Reference specific files/components affected.");
        sb.AppendLine();
        sb.AppendLine("## Review Verdict");
        sb.AppendLine("In 1-2 sentences, summarize the review outcome. Name the 1-2 most impactful findings specifically (e.g., \"race condition in drain service's pre-reservation flow\", not \"some issues\"). Include severity count and disposition (fixed/reported/clean). If no findings, state \"No issues found\" and briefly note quality.");
        sb.AppendLine();
        sb.AppendLine("Be specific about WHAT was found rather than generic. Maximum 3 sentences per section.");
        sb.AppendLine();
        sb.AppendLine("Output ONLY the two markdown sections above — no other text, no code changes, no file writes.");
        sb.AppendLine();
        sb.AppendLine("--- DIFF STAT ---");
        sb.AppendLine(string.IsNullOrWhiteSpace(diffStat) ? "(not available)" : diffStat);
        sb.AppendLine();
        sb.AppendLine("--- ISSUE CONTEXT ---");
        sb.AppendLine(string.IsNullOrWhiteSpace(issueTitle) ? "(not available)" : issueTitle);
        sb.AppendLine();
        sb.AppendLine("--- FINDINGS ---");
        sb.AppendLine(string.IsNullOrWhiteSpace(findings) ? "(no findings — clean review)" : findings);

        return sb.ToString().TrimEnd();
    }
}
