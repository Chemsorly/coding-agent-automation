using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Services;

/// <summary>Which half of a run's <see cref="RunFeedback"/> is ranked.</summary>
public enum FeedbackKind { Harness, Issue }

/// <summary>One run's feedback within a category.</summary>
public sealed record FeedbackEntry(
    string RunId, string IssueIdentifier, string IssueTitle, string? ProjectName,
    DateTimeOffset StartedAt, FeedbackOutcome Outcome, string Text);

/// <summary>A feedback category and the runs that reported it, newest first.</summary>
// TODO: Entries[0] is accessed in Rank's sort key (ThenByDescending) and in orderedPairs[0] without
// a bounds check. FeedbackCategoryGroup is a public record whose primary constructor accepts any
// IReadOnlyList<FeedbackEntry>, including an empty list. If an instance is ever constructed externally
// with Entries = [], both the secondary sort and FeedbackCategoryCard's Entries[0] render will throw
// IndexOutOfRangeException. Consider adding a guard in the constructor or restricting construction
// to internal callers only.
public sealed record FeedbackCategoryGroup(string Category, IReadOnlyList<FeedbackEntry> Entries)
{
    public int Count => Entries.Count;
    public int FailureCount => Entries.Count(e => e.Outcome == FeedbackOutcome.Failure);
}

/// <summary>
/// Groups the runs' feedback by category so recurring problems show up: the Insights page's
/// "Harness feedback" and "Issue feedback" cards.
/// </summary>
public static class FeedbackCategoryRanking
{
    public const string Uncategorized = "Uncategorized";
    public const int MaxTextLength = 200;

    /// <summary>
    /// Groups the feedback from <paramref name="runs"/> by category for the given <paramref name="kind"/>.
    /// Only runs with non-null <see cref="PipelineRunSummary.Feedback"/> and content matching the kind are included.
    /// Groups are ordered by count descending, then by newest entry's StartedAt descending, then by Category (ordinal).
    /// </summary>
    public static IReadOnlyList<FeedbackCategoryGroup> Rank(IEnumerable<PipelineRunSummary> runs, FeedbackKind kind)
    {
        // Build a flat list of (rawCategory, entry) pairs
        var pairs = new List<(string RawCategory, FeedbackEntry Entry)>();

        foreach (var run in runs)
        {
            if (run.Feedback is not { } feedback)
                continue;

            string? rawCategory;
            string text;

            if (kind == FeedbackKind.Harness)
            {
                var h = feedback.Harness;

                // A run has an entry when any harness content is present
                bool hasContent =
                    !string.IsNullOrWhiteSpace(h.Category) ||
                    !string.IsNullOrWhiteSpace(h.StuckReason) ||
                    h.MissingContext.Count > 0 ||
                    h.MissingCapabilities.Count > 0 ||
                    h.PromptIssues.Count > 0 ||
                    h.Suggestions.Count > 0;

                if (!hasContent)
                    continue;

                rawCategory = h.Category;

                // Text: first non-blank of StuckReason, MissingContext[0], MissingCapabilities[0],
                // PromptIssues[0], Suggestions[0]; otherwise ""
                text = FirstNonBlank(
                    h.StuckReason,
                    h.MissingContext.Count > 0 ? h.MissingContext[0] : null,
                    h.MissingCapabilities.Count > 0 ? h.MissingCapabilities[0] : null,
                    h.PromptIssues.Count > 0 ? h.PromptIssues[0] : null,
                    h.Suggestions.Count > 0 ? h.Suggestions[0] : null);
            }
            else // Issue
            {
                var issue = feedback.Issue;

                if (issue is null)
                    continue;

                // A run has an entry when Issue has a non-blank Category or Description
                if (string.IsNullOrWhiteSpace(issue.Category) && string.IsNullOrWhiteSpace(issue.Description))
                    continue;

                rawCategory = issue.Category;

                // Text: first non-blank of Description, HumanActionNeeded; otherwise ""
                text = FirstNonBlank(issue.Description, issue.HumanActionNeeded);
            }

            // Trim text and truncate if over MaxTextLength
            text = text.Trim();
            if (text.Length > MaxTextLength)
                text = text[..MaxTextLength] + "…";

            // Resolve category: trim, blank → Uncategorized
            string category = string.IsNullOrWhiteSpace(rawCategory) ? Uncategorized : rawCategory.Trim();

            var entry = new FeedbackEntry(
                RunId: run.RunId,
                IssueIdentifier: run.IssueIdentifier.Value,
                IssueTitle: run.IssueTitle,
                ProjectName: run.ProjectName,
                StartedAt: run.StartedAtOffset,
                Outcome: feedback.Outcome,
                Text: text);

            pairs.Add((category, entry));
        }

        // Group by category (case-insensitive)
        var grouped = pairs
            .GroupBy(p => p.RawCategory, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                // Order pairs by StartedAt descending — newest first
                var orderedPairs = g.OrderByDescending(p => p.Entry.StartedAt).ToList();

                // Entries (newest first)
                var entries = orderedPairs.Select(p => p.Entry).ToList();

                // Group's Category is the trimmed spelling of the newest entry's category
                string groupCategory = orderedPairs[0].RawCategory;

                return new FeedbackCategoryGroup(groupCategory, entries);
            })
            .OrderByDescending(g => g.Count)
            .ThenByDescending(g => g.Entries[0].StartedAt)
            .ThenBy(g => g.Category, StringComparer.Ordinal)
            .ToList();

        return grouped;
    }

    private static string FirstNonBlank(params string?[] candidates)
    {
        foreach (var s in candidates)
            if (!string.IsNullOrWhiteSpace(s))
                return s;
        return "";
    }
}
