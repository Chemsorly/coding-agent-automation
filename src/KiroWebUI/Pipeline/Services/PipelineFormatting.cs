using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace KiroWebUI.Pipeline.Services;

/// <summary>
/// Shared formatting utilities for branch names, PR titles/bodies, and commit messages.
/// Decoupled from any specific provider implementation so the orchestrator doesn't
/// depend on concrete provider types.
/// </summary>
public static partial class PipelineFormatting
{
    /// <summary>
    /// Generates a branch name from issue number, title, and run ID.
    /// Pattern: feature/auto-{issueNumber}-{slug}-{shortRunId}
    /// The short run ID suffix ensures re-runs for the same issue don't collide with existing remote branches.
    /// </summary>
    public static string GenerateBranchName(string issueNumber, string title, string? runId = null)
    {
        var slug = GenerateSlug(title);
        var suffix = runId != null ? $"-{runId[..8]}" : "";
        return string.IsNullOrEmpty(slug)
            ? $"feature/auto-{issueNumber}{suffix}"
            : $"feature/auto-{issueNumber}-{slug}{suffix}";
    }

    /// <summary>
    /// Generates a PR title in conventional commit format.
    /// </summary>
    public static string GeneratePrTitle(string issueTitle, string issueNumber)
    {
        return $"feat: {issueTitle} (#{issueNumber})";
    }

    /// <summary>
    /// Generates a PR body with all required sections.
    /// </summary>
    public static string GeneratePrBody(
        string issueNumber,
        int testsPassed,
        int testsFailed,
        int testsSkipped,
        double? coveragePercent,
        string implementationSummary,
        bool isDraft = false)
    {
        var sb = new StringBuilder();

        if (isDraft)
        {
            sb.AppendLine("⚠️ **This is a draft PR — implementation is incomplete.**");
            sb.AppendLine();
        }

        sb.AppendLine("## Implementation Summary");
        sb.AppendLine(implementationSummary);
        sb.AppendLine();

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

        sb.AppendLine("---");
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
