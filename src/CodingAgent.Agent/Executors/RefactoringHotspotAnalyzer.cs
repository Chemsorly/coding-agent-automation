using System.Globalization;
using System.Text;
using CodingAgent.Pipeline;

namespace CodingAgent.Agent.Executors;

/// <summary>
/// Result of a hotspot parse pass.
/// </summary>
/// <param name="Summary">
/// The formatted hotspot summary text, or <see langword="null"/> if no candidate files were found.
/// </param>
/// <param name="ExcludedCommitCount">
/// The number of commits whose subject matched the exclusion predicate. Zero when no predicate was supplied.
/// </param>
internal sealed record HotspotParseResult(string? Summary, int ExcludedCommitCount);

/// <summary>
/// Stateless helpers for analysing git-log hotspot output. Extracted from
/// <see cref="RefactoringExecutor"/> so the parsing logic can evolve independently of scan
/// orchestration and issue-tracker context.
/// </summary>
internal static class RefactoringHotspotAnalyzer
{
    /// <summary>Length of the literal prefix <c>"COMMIT_DATE:"</c>.</summary>
    private const int CommitDatePrefixLength = 12;

    /// <summary>Length of the literal prefix <c>"COMMIT_SUBJECT:"</c>.</summary>
    private const int CommitSubjectPrefixLength = 15;

    /// <summary>
    /// Files whose churn says nothing about code health: pipeline scratch space, documentation and
    /// lock files. They are left out of the hotspot list.
    /// </summary>
    private static readonly string[] HotspotExcludedPrefixes =
        [AgentWorkspacePaths.MetadataDirectory + "/", AgentWorkspacePaths.BrainDirectory + "/"];

    private static readonly string[] HotspotExcludedFileNames =
        ["package-lock.json", "packages.lock.json", "yarn.lock", "pnpm-lock.yaml"];

    internal static bool IsHotspotCandidate(string file) =>
        !HotspotExcludedPrefixes.Any(p => file.StartsWith(p, StringComparison.OrdinalIgnoreCase))
        && !file.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        && !file.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
        && !HotspotExcludedFileNames.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parses git log output with COMMIT_DATE: markers into a time-decay-weighted hotspot summary.
    /// Each change is weighted by recency: factor = 1/(1 + days_since/30).
    /// Leaves out files that fail <see cref="IsHotspotCandidate"/> and, when <paramref name="fileExists"/>
    /// is given, files that no longer exist.
    /// When <paramref name="isExcludedCommit"/> is given, file lines for commits whose subject matches
    /// the predicate are skipped; <c>COMMIT_SUBJECT:</c> lines are never counted as files.
    /// The returned <see cref="HotspotParseResult.Summary"/> is <see langword="null"/> if no candidate
    /// files were found. Exposed as internal static for testability.
    /// </summary>
    internal static HotspotParseResult ParseHotspotOutput(
        string gitLogOutput, TimeSpan lookback, DateTime? referenceTime = null, Func<string, bool>? fileExists = null,
        Func<string, bool>? isExcludedCommit = null)
    {
        var now = referenceTime ?? DateTime.UtcNow;
        var entries = new List<(string File, double RecencyFactor, double DaysSince)>();
        // Note: files appearing before any COMMIT_DATE: marker receive max recency weight (1.0).
        // In practice, git --format always emits the date line before file names per commit.
        DateTime currentCommitDate = now;
        bool dateParseFailedForCurrentCommit = false;
        bool currentCommitExcluded = false;
        int excludedCommitCount = 0;

        foreach (var line in gitLogOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            if (trimmed.StartsWith("COMMIT_DATE:", StringComparison.Ordinal))
            {
                var dateStr = trimmed[CommitDatePrefixLength..];
                if (DateTimeOffset.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    currentCommitDate = parsed.UtcDateTime;
                    dateParseFailedForCurrentCommit = false;
                }
                else
                {
                    // Graceful degradation: neutral weight (0.5 → equivalent to 30 days old)
                    dateParseFailedForCurrentCommit = true;
                }
                // Reset excluded state; subject line (if present) will update it
                // NOTE (issue #3540): currentCommitExcluded is reset to false here (on COMMIT_DATE:) and then
                // updated by the subsequent COMMIT_SUBJECT: line. This relies on git always emitting date
                // before subject, which is guaranteed by --format="COMMIT_DATE:%ai%nCOMMIT_SUBJECT:%s".
                // If the format ever changes or test input is hand-crafted with subject before date, the
                // COMMIT_DATE: line would reset the flag after COMMIT_SUBJECT: set it, silently un-excluding
                // the commit. Consider adding a guard test for subject-before-date ordering, or restructuring
                // to accumulate both fields before acting on them.
                currentCommitExcluded = false;
                continue;
            }

            if (trimmed.StartsWith("COMMIT_SUBJECT:", StringComparison.Ordinal))
            {
                var subject = trimmed[CommitSubjectPrefixLength..];
                currentCommitExcluded = isExcludedCommit is not null && isExcludedCommit(subject);
                if (currentCommitExcluded)
                    excludedCommitCount++;
                continue;
            }

            // Skip file lines for excluded commits
            if (currentCommitExcluded)
                continue;

            double daysSince;
            double recencyFactor;

            if (dateParseFailedForCurrentCommit)
            {
                daysSince = 30.0;
                recencyFactor = 0.5;
            }
            else
            {
                daysSince = Math.Max(0, (now - currentCommitDate).TotalDays);
                recencyFactor = 1.0 / (1.0 + daysSince / 30.0);
            }

            entries.Add((trimmed, recencyFactor, daysSince));
        }

        if (entries.Count == 0)
            return new HotspotParseResult(null, excludedCommitCount);

        var hotspots = entries
            .Where(e => IsHotspotCandidate(e.File))
            .GroupBy(e => e.File)
            .Where(g => fileExists is null || fileExists(g.Key))
            .Select(g => (
                File: g.Key,
                Score: g.Sum(e => e.RecencyFactor),
                Count: g.Count(),
                AvgDaysAgo: (int)g.Average(e => e.DaysSince)))
            .OrderByDescending(x => x.Score)
            .Take(30)
            .ToList();

        if (hotspots.Count == 0)
            return new HotspotParseResult(null, excludedCommitCount);

        var sb = new StringBuilder();
        sb.AppendLine($"# Git Hotspot Analysis (last {lookback.Days} days, time-decay weighted)");
        sb.AppendLine("# Score = sum of recency factors (1/(1+days/30) per change — recent changes weighted higher)");
        sb.AppendLine();
        foreach (var (file, score, count, avgDaysAgo) in hotspots)
            sb.AppendLine($"{score.ToString("F1", CultureInfo.InvariantCulture)} — {file} ({count} changes, avg {avgDaysAgo} days ago)");

        return new HotspotParseResult(sb.ToString(), excludedCommitCount);
    }
}
