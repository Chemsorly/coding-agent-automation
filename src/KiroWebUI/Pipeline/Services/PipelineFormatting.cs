using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Services;

/// <summary>
/// Shared formatting utilities for branch names, PR titles/bodies, and commit messages.
/// Decoupled from any specific provider implementation so the orchestrator doesn't
/// depend on concrete provider types.
/// </summary>
public static partial class PipelineFormatting
{
    private const int MaxBranchNameLength = 100;

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
        var prefix = $"feature/auto-{issueNumber}";

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
    public static string GeneratePrTitle(string issueTitle, string issueNumber)
    {
        return $"feat: {issueTitle} (#{issueNumber})";
    }

    /// <summary>Maximum character length for a comment body in the PR description before truncation.</summary>
    private const int MaxCommentLength = 200;

    /// <summary>
    /// Generates a PR body with all required sections including file changes and issue context.
    /// </summary>
    public static string GeneratePrBody(
        string issueNumber,
        int testsPassed,
        int testsFailed,
        int testsSkipped,
        double? coveragePercent,
        IReadOnlyList<FileChangeSummary> fileChanges,
        string issueTitle,
        string issueDescription,
        IReadOnlyList<string> acceptanceCriteria,
        bool isDraft = false,
        IReadOnlyList<IssueComment>? comments = null,
        IReadOnlyList<string>? blacklistedFilesDetected = null,
        string? modelName = null,
        CodeReviewSummary? codeReviewSummary = null)
    {
        var sb = new StringBuilder();

        if (isDraft)
        {
            sb.AppendLine("⚠️ **This is a draft PR — implementation is incomplete.**");
            sb.AppendLine();
        }

        // Issue context
        sb.AppendLine("## Issue Context");
        sb.AppendLine($"**{issueTitle}** (#{issueNumber})");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(issueDescription))
        {
            var desc = issueDescription.Length > 500
                ? issueDescription[..500] + "…"
                : issueDescription;
            sb.AppendLine(desc);
            sb.AppendLine();
        }
        if (acceptanceCriteria.Count > 0)
        {
            sb.AppendLine("**Acceptance Criteria:**");
            foreach (var criterion in acceptanceCriteria)
                sb.AppendLine($"- {criterion}");
            sb.AppendLine();
        }

        // Input comments
        AppendInputComments(sb, comments);

        // Files changed
        sb.AppendLine("## Files Changed");
        if (fileChanges.Count > 0)
        {
            sb.AppendLine("| Status | File |");
            sb.AppendLine("|--------|------|");
            const int maxFiles = 50;
            foreach (var fc in fileChanges.Take(maxFiles))
                sb.AppendLine($"| {fc.Status} | `{fc.Path}` |");
            if (fileChanges.Count > maxFiles)
                sb.AppendLine($"| | *(and {fileChanges.Count - maxFiles} more)* |");
        }
        else
        {
            sb.AppendLine("No file changes detected.");
        }
        sb.AppendLine();

        // Test results
        sb.AppendLine("## Test Results");
        sb.AppendLine($"- Passed: {testsPassed}");
        sb.AppendLine($"- Failed: {testsFailed}");
        sb.AppendLine($"- Skipped: {testsSkipped}");
        sb.AppendLine();

        sb.AppendLine("## Coverage");
        sb.AppendLine(coveragePercent.HasValue
            ? $"{coveragePercent.Value.ToString("F1", CultureInfo.InvariantCulture)}%"
            : "Not available");
        sb.AppendLine();

        sb.AppendLine("## Issue Reference");
        sb.AppendLine($"Closes #{issueNumber}");
        sb.AppendLine();

        if (blacklistedFilesDetected is { Count: > 0 })
        {
            sb.AppendLine("## ⚠️ Blacklisted Files Excluded");
            sb.AppendLine("The following agent-modified files were excluded from this commit (protected paths):");
            foreach (var file in blacklistedFilesDetected)
                sb.AppendLine($"- `{file}`");
            sb.AppendLine();
        }

        AppendCodeReviewSection(sb, codeReviewSummary);

        sb.AppendLine("---");
        if (!string.IsNullOrEmpty(modelName))
            sb.AppendLine($"*Model: {modelName} · Automated implementation via pipeline*");
        else
            sb.AppendLine("*Automated implementation via pipeline*");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Generates a commit message in conventional format.
    /// </summary>
    public static string GenerateCommitMessage(string title, string issueNumber)
    {
        return $"feat: {title} (#{issueNumber})\n\nAutomated implementation via pipeline";
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

            var truncated = agent.Findings.Length > maxFindingsPerAgent
                ? agent.Findings[..maxFindingsPerAgent] + "…"
                : agent.Findings;
            sb.AppendLine("<details>");
            sb.AppendLine($"<summary>{agent.AgentName}</summary>");
            sb.AppendLine();
            sb.AppendLine(truncated);
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }
    }

    private static void AppendInputComments(StringBuilder sb, IReadOnlyList<IssueComment>? comments)
    {
        if (comments == null || comments.Count == 0)
            return;

        var filtered = comments
            .Where(c => !PromptBuilder.ExcludedCommentMarkers.Any(marker => c.Body.Contains(marker)))
            .TakeLast(10)
            .ToList();

        if (filtered.Count == 0)
            return;

        sb.AppendLine("## Input Comments");
        foreach (var comment in filtered)
        {
            var body = comment.Body.Length > MaxCommentLength
                ? comment.Body[..MaxCommentLength] + "…"
                : comment.Body;
            sb.AppendLine($"- **@{comment.Author}** ({comment.CreatedAt:yyyy-MM-dd HH:mm} UTC): {body}");
        }
        sb.AppendLine();
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
