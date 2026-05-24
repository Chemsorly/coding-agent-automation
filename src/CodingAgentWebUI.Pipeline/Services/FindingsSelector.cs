using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Pure transformation pipeline: filter by threshold → order by severity → cap at max → consolidate same file:line.
/// Extracted from PostReviewFindingsStep for independent testability.
/// </summary>
internal static class FindingsSelector
{
    /// <summary>
    /// Selects and transforms structured findings into review comments.
    /// Returns the selected ReviewComments and the count of findings excluded by the cap
    /// (eligible findings that exceeded MaxInlineComments).
    /// </summary>
    /// <param name="findings">All structured findings with non-null FilePath.</param>
    /// <param name="settings">Inline comment settings (threshold, max, ordering).</param>
    /// <returns>Tuple of (selected ReviewComments, excluded count beyond cap).</returns>
    public static (IReadOnlyList<ReviewComment> Comments, int ExcludedCount) Select(
        IReadOnlyList<StructuredFinding> findings,
        InlineCommentSettings settings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(settings);

        // Step 1: Filter — only findings with non-null FilePath and severity >= threshold
        var eligible = findings
            .Where(f => f.FilePath is not null && (int)f.Severity >= (int)settings.SeverityThreshold)
            .ToList();

        // Step 2: Order — stable sort by severity descending (Critical=2 first) if enabled
        if (settings.OrderBySeverity)
        {
            // Use a stable sort (OrderBy in LINQ is stable)
            eligible = eligible.OrderByDescending(f => (int)f.Severity).ToList();
        }

        // Step 3: Cap — take first N findings where N = Math.Clamp(MaxInlineComments, 1, 50)
        var cap = Math.Clamp(settings.MaxInlineComments, 1, 50);
        var excludedCount = Math.Max(0, eligible.Count - cap);
        var selected = eligible.Take(cap).ToList();

        // Step 4: Consolidate — group by (FilePath, LineNumber), format each group
        var groups = selected
            .GroupBy(f => (f.FilePath!, f.LineNumber))
            .ToList();

        // Step 5: Build — create ReviewComment for each group with Side = DiffSide.Right
        var comments = new List<ReviewComment>(groups.Count);
        foreach (var group in groups)
        {
            var groupFindings = group.ToList();
            var body = InlineCommentFormatter.FormatConsolidated(groupFindings);

            comments.Add(new ReviewComment
            {
                Path = group.Key.Item1,
                Line = group.Key.Item2,
                Side = DiffSide.Right,
                Body = body
            });
        }

        // Step 6: Return comments + excluded count
        return (comments, excludedCount);
    }
}
