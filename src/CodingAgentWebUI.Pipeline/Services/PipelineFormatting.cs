using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Prompts;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Shared formatting utilities for branch names, PR titles/bodies, and commit messages.
/// Decoupled from any specific provider implementation so the orchestrator doesn't
/// depend on concrete provider types.
/// </summary>
public static partial class PipelineFormatting
{
    private const int MaxBranchNameLength = PipelineConstants.MaxBranchNameLength;

    /// <summary>
    /// Generates a branch name from issue number, title, and run ID.
    /// Pattern: feature/auto-{issueNumber}-{slug}-{shortRunId}
    /// The short run ID suffix ensures re-runs for the same issue don't collide with existing remote branches.
    /// The slug is truncated to keep the total branch name under <see cref="MaxBranchNameLength"/> characters.
    /// </summary>
    public static string GenerateBranchName(string issueNumber, string title, string? runId = null)
    {
        var slug = GenerateSlug(title);
        var suffix = runId != null ? $"-{runId[..8]}" : "";
        var prefix = $"{PipelineConstants.BranchPrefix}{issueNumber}";

        if (!string.IsNullOrEmpty(slug))
        {
            var maxSlugLength = MaxBranchNameLength - prefix.Length - 1 - suffix.Length; // 1 for the hyphen before slug
            if (maxSlugLength > 0)
            {
                if (slug.Length > maxSlugLength)
                    slug = slug[..maxSlugLength].TrimEnd('-');
                return $"{prefix}-{slug}{suffix}";
            }
        }

        var result = $"{prefix}{suffix}";
        return result.Length > MaxBranchNameLength ? result[..MaxBranchNameLength] : result;
    }

    /// <summary>
    /// Generates a PR title in conventional commit format.
    /// </summary>
    public static string GeneratePrTitle(string issueTitle, string issueReference)
    {
        return $"feat: {issueTitle} ({issueReference})";
    }

    /// <summary>Maximum character length for a comment body in the PR description before truncation.</summary>
    private const int MaxCommentLength = PipelineConstants.MaxCommentLength;

    /// <summary>
    /// Generates a PR body with all required sections including file changes and issue context.
    /// </summary>
    public static string GeneratePrBody(PrBodyParameters parameters)
    {
        var sb = new StringBuilder();

        if (parameters.IsDraft)
        {
            sb.AppendLine("⚠️ **This is a draft PR — implementation is incomplete.**");
            sb.AppendLine();
        }

        // Issue context — link to the issue instead of duplicating its body
        sb.AppendLine("## Issue Context");
        sb.AppendLine($"**{parameters.IssueTitle}** ({parameters.IssueReference})");
        sb.AppendLine();

        // Input comments
        AppendInputComments(sb, parameters.Comments);

        // Files changed
        sb.AppendLine("## Files Changed");
        if (parameters.FileChanges.Count > 0)
        {
            sb.AppendLine("| Status | File |");
            sb.AppendLine("|--------|------|");
            const int maxFiles = 50;
            foreach (var fc in parameters.FileChanges.Take(maxFiles))
                sb.AppendLine($"| {fc.Status} | `{fc.Path}` |");
            if (parameters.FileChanges.Count > maxFiles)
                sb.AppendLine($"| | *(and {parameters.FileChanges.Count - maxFiles} more)* |");
        }
        else
        {
            sb.AppendLine("No file changes detected.");
        }
        sb.AppendLine();

        // Test results
        sb.AppendLine("## Test Results");
        sb.AppendLine($"- Passed: {parameters.TestsPassed}");
        sb.AppendLine($"- Failed: {parameters.TestsFailed}");
        sb.AppendLine($"- Skipped: {parameters.TestsSkipped}");
        sb.AppendLine();

        sb.AppendLine("## Coverage");
        sb.AppendLine(parameters.CoveragePercent.HasValue
            ? $"{parameters.CoveragePercent.Value.ToString("F1", CultureInfo.InvariantCulture)}%"
            : "Not available");
        sb.AppendLine();

        if (parameters.CloseReference is not null)
        {
            sb.AppendLine("## Issue Reference");
            sb.AppendLine(parameters.CloseReference);
            sb.AppendLine();
        }

        AppendCodeReviewSection(sb, parameters.CodeReviewSummary);
        AppendComplianceSection(sb, parameters.ComplianceReport);

        sb.AppendLine("---");
        if (!string.IsNullOrEmpty(parameters.ModelName))
            sb.AppendLine($"*Model: {parameters.ModelName} · {PipelineConstants.AutomatedCommitSuffix}*");
        else
            sb.AppendLine($"*{PipelineConstants.AutomatedCommitSuffix}*");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Generates a commit message in conventional format.
    /// </summary>
    public static string GenerateCommitMessage(string title, string issueReference)
    {
        return $"feat: {title} ({issueReference})\n\n{PipelineConstants.AutomatedCommitSuffix}";
    }

    /// <summary>
    /// Checks whether a file path matches any of the blacklisted path prefixes.
    /// Matching is prefix-based, case-insensitive, and normalizes backslashes to forward slashes.
    /// </summary>
    public static bool IsPathBlacklisted(string filePath, IReadOnlyList<string> blacklistedPrefixes)
    {
        if (blacklistedPrefixes.Count == 0) return false;
        var normalized = filePath.Replace('\\', '/');
        foreach (var prefix in blacklistedPrefixes)
        {
            var normalizedPrefix = prefix.Replace('\\', '/').TrimEnd('/');
            if (normalized.StartsWith(normalizedPrefix + "/", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static void AppendCodeReviewSection(StringBuilder sb, CodeReviewSummary? summary)
    {
        if (summary is null)
            return;

        sb.AppendLine("## AI Code Review Findings");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(summary.VerdictSummary))
        {
            sb.AppendLine($"**Review verdict**: {TextTruncation.TruncateAtSentenceBoundary(summary.VerdictSummary, 500)}");
            sb.AppendLine();
        }

        if (summary.CriticalCount == 0 && summary.WarningCount == 0 && summary.SuggestionCount == 0
            && summary.AgentFindings.Count == 0)
        {
            sb.AppendLine("Code review: no findings");
            sb.AppendLine();
            return;
        }

        if (summary.AgentsRun.Count > 0)
            sb.AppendLine($"**Agents**: {string.Join(", ", summary.AgentsRun)}");
        sb.AppendLine();

        sb.AppendLine("| Severity | Count | Action |");
        sb.AppendLine("|----------|-------|--------|");
        if (summary.CriticalCount > 0)
            sb.AppendLine($"| CRITICAL | {summary.CriticalCount} | Fixed |");
        if (summary.WarningCount > 0)
            sb.AppendLine($"| WARNING | {summary.WarningCount} | Reported (TODO comments added) |");
        if (summary.SuggestionCount > 0)
            sb.AppendLine($"| SUGGESTION | {summary.SuggestionCount} | Reported only |");
        sb.AppendLine();

        const int maxFindingsPerAgent = 10_000;
        foreach (var agent in summary.AgentFindings)
        {
            if (string.IsNullOrEmpty(agent.Findings))
                continue;

            var truncated = TruncateMarkdown(agent.Findings, maxFindingsPerAgent);
            sb.AppendLine("<details>");
            sb.AppendLine($"<summary>{agent.AgentName}</summary>");
            sb.AppendLine();
            sb.AppendLine(truncated);
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }
    }

    private static void AppendComplianceSection(StringBuilder sb, AcceptanceCriteriaReport? report)
    {
        if (report is null || report.Criteria.Count == 0)
            return;

        sb.AppendLine("## Acceptance Criteria Compliance");
        sb.AppendLine();
        sb.AppendLine("| Status | Criterion | Notes |");
        sb.AppendLine("|--------|-----------|-------|");
        foreach (var c in report.Criteria)
        {
            var icon = c.Status switch
            {
                CriterionStatus.Compliant => "✅",
                CriterionStatus.NonCompliant => "❌",
                CriterionStatus.NotApplicable => "⚠️",
                _ => "❓"
            };
            var notes = SanitizeTableCell(c.Evidence ?? c.Reasoning ?? "");
            sb.AppendLine($"| {icon} | {SanitizeTableCell(c.Criterion)} | {notes} |");
        }
        sb.AppendLine();
        sb.AppendLine($"*{report.Summary}*");
        sb.AppendLine();
    }

    /// <summary>Escapes pipe characters and replaces newlines to keep markdown table cells intact.</summary>
    private static string SanitizeTableCell(string text)
        => text.Replace("|", "\\|").Replace("\r\n", " ").Replace("\n", " ");

    private static void AppendInputComments(StringBuilder sb, IReadOnlyList<IssueComment>? comments)
    {
        if (comments == null || comments.Count == 0)
            return;

        var filtered = comments
            .Where(c => !PromptBuilder.ExcludedCommentMarkers.Any(marker => c.Body.Contains(marker)))
            .TakeLast(PipelineConstants.OutputTailLineCount)
            .ToList();

        if (filtered.Count == 0)
            return;

        sb.AppendLine("## Input Comments");
        foreach (var comment in filtered)
        {
            var body = TruncateMarkdown(comment.Body, MaxCommentLength);
            sb.AppendLine($"- **@{comment.Author}** ({comment.CreatedAt:yyyy-MM-dd HH:mm} UTC): {body}");
        }
        sb.AppendLine();
    }

    /// <summary>
    /// Truncates a markdown string to the specified length and ensures any open
    /// code fences (```) are properly closed so downstream markdown isn't swallowed.
    /// </summary>
    private static string TruncateMarkdown(string text, int maxLength)
    {
        if (text.Length <= maxLength)
            return text;

        var truncated = text[..maxLength] + "…";

        // Count code fence markers (``` at line start) in the truncated text.
        // An odd count means a fence was left open.
        var fenceCount = 0;
        foreach (var line in truncated.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                fenceCount++;
        }

        if (fenceCount % 2 != 0)
            truncated += "\n```";

        return truncated;
    }

    /// <summary>
    /// Formats a one-line quality gate summary with emoji status indicators.
    /// Example: "🏗️ Quality gates: Compilation ✅ | Tests ✅ (42 passed, 0 failed) | Coverage ❌ (26.7%, threshold 40%)"
    /// </summary>
    public static string FormatQualityGateSummary(QualityGateReport report)
    {
        var parts = new List<string>
        {
            $"Compilation {(report.Compilation.Passed ? "✅" : "❌")}",
            FormatTestGateSummary(report.Tests)
        };

        if (report.Coverage is not null)
            parts.Add($"Coverage {(report.Coverage.Passed ? "✅" : "❌")} ({report.Coverage.Details})");
        if (report.SecurityScan is not null)
            parts.Add($"Security {(report.SecurityScan.Passed ? "✅" : "❌")}");
        if (report.ExternalCi is not null)
            parts.Add($"External CI {(report.ExternalCi.Passed ? "✅" : "❌")}");

        return $"🏗️ Quality gates: {string.Join(" | ", parts)}";
    }

    private static string FormatTestGateSummary(GateResult tests)
    {
        var status = tests.Passed ? "✅" : "❌";
        if (tests.TestsPassed.HasValue || tests.TestsFailed.HasValue)
            return $"Tests {status} ({tests.TestsPassed ?? 0} passed, {tests.TestsFailed ?? 0} failed)";
        return $"Tests {status}";
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlphanumericPattern();

    private static string GenerateSlug(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var slug = title.ToLowerInvariant();
        slug = NonAlphanumericPattern().Replace(slug, "-");
        slug = slug.Trim('-');
        return slug;
    }
}
