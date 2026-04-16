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
        IReadOnlyList<IssueComment>? comments = null)
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

        sb.AppendLine("---");
        sb.AppendLine("*Automated implementation via pipeline*");

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds a list of file changes by comparing the current branch HEAD against the base branch.
    /// Falls back to an empty list if the diff cannot be computed.
    /// </summary>
    public static IReadOnlyList<FileChangeSummary> GetFileChanges(string workspacePath, string baseBranch)
    {
        try
        {
            using var repo = new LibGit2Sharp.Repository(workspacePath);
            var baseBranchRef = repo.Branches[$"origin/{baseBranch}"]
                ?? repo.Branches[baseBranch];
            if (baseBranchRef == null)
                return Array.Empty<FileChangeSummary>();

            var baseCommit = baseBranchRef.Tip;
            var headCommit = repo.Head.Tip;
            var diff = repo.Diff.Compare<LibGit2Sharp.TreeChanges>(baseCommit.Tree, headCommit.Tree);

            var changes = new List<FileChangeSummary>();
            foreach (var entry in diff)
            {
                var status = entry.Status switch
                {
                    LibGit2Sharp.ChangeKind.Added => "Added",
                    LibGit2Sharp.ChangeKind.Deleted => "Deleted",
                    LibGit2Sharp.ChangeKind.Renamed => "Renamed",
                    _ => "Modified"
                };
                changes.Add(new FileChangeSummary(status, entry.Path));
            }
            return changes;
        }
        catch
        {
            return Array.Empty<FileChangeSummary>();
        }
    }

    /// <summary>
    /// Generates a commit message in conventional format.
    /// </summary>
    public static string GenerateCommitMessage(string title, string issueNumber)
    {
        return $"feat: {title} (#{issueNumber})\n\nAutomated implementation via pipeline";
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
