using System.Globalization;
using System.Text;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Triage;

/// <summary>
/// Renders what a triage run is told besides the report: the triage's earlier attempts with the feedback people
/// gave, and the project's other recent triages for duplicate detection. Also turns an operator's form into the
/// issue the run investigates.
/// </summary>
public static class TriageContextRenderer
{
    private const int MaxSummaryChars = 800;

    /// <summary>The issue an operator triage's run investigates: the form, as an issue body.</summary>
    public static IssueDetail ToIssueDetail(TriageRecord triage)
    {
        ArgumentNullException.ThrowIfNull(triage);
        var request = triage.Request ?? throw new ArgumentException("An operator triage has a request", nameof(triage));

        var sb = new StringBuilder();
        sb.AppendLine("## What happened");
        sb.AppendLine(request.WhatHappened.Trim());
        sb.AppendLine();
        sb.AppendLine("## Expected");
        sb.AppendLine(request.Expected.Trim());
        sb.AppendLine();
        sb.AppendLine("## When and where");
        sb.AppendLine($"- First seen: {Time(request.From) ?? "unknown"}");
        sb.AppendLine($"- Until: {Time(request.Until) ?? "still happening"}");
        if (!string.IsNullOrWhiteSpace(request.Environment))
            sb.AppendLine($"- Environment: {request.Environment.Trim()}");
        if (!string.IsNullOrWhiteSpace(request.Where))
            sb.AppendLine($"- Where: {request.Where.Trim()}");
        if (!string.IsNullOrWhiteSpace(request.Version))
            sb.AppendLine($"- Version that was running: {request.Version.Trim()}");
        if (!string.IsNullOrWhiteSpace(request.Links))
        {
            sb.AppendLine();
            sb.AppendLine("## Links and IDs");
            sb.AppendLine(request.Links.Trim());
        }
        if (!string.IsNullOrWhiteSpace(request.AlreadyTried))
        {
            sb.AppendLine();
            sb.AppendLine("## Already tried");
            sb.AppendLine(request.AlreadyTried.Trim());
        }
        sb.AppendLine();
        sb.AppendLine($"_Reported by {triage.RequestedBy ?? "an operator"} in Coding Agent._");

        return new IssueDetail
        {
            Identifier = TriageConstants.IssueIdentifierFor(triage.Id),
            Title = triage.Title,
            Description = sb.ToString(),
            Labels = [],
        };
    }

    /// <summary>
    /// Renders <c>.agent/triage-context.md</c>.
    /// </summary>
    /// <param name="triage">The run's triage, or null for a tracker issue's first triage.</param>
    /// <param name="currentWorkItemId">The run's WorkItem id: its attempt carries the feedback given for it.</param>
    /// <param name="others">The project's other recent triages, newest first.</param>
    /// <param name="reportToTracker">
    /// True when the result is posted on a tracker issue: other triages are then named only by title, verdict and
    /// created issues, so an operator's private report cannot leak into a public comment.
    /// </param>
    public static string RenderContext(
        TriageRecord? triage, string? currentWorkItemId, IReadOnlyList<TriageRecord> others, bool reportToTracker)
    {
        ArgumentNullException.ThrowIfNull(others);

        var sb = new StringBuilder();
        sb.AppendLine("# Triage Context");
        sb.AppendLine();

        var earlier = triage?.Attempts.Where(a => a.WorkItemId != currentWorkItemId && a.Result is not null).ToList() ?? [];
        sb.AppendLine("## Earlier attempts of this triage");
        sb.AppendLine();
        if (earlier.Count == 0)
        {
            sb.AppendLine("None: this is the first attempt.");
            sb.AppendLine();
        }
        else
        {
            var n = 1;
            foreach (var attempt in earlier)
            {
                var result = attempt.Result!;
                sb.AppendLine($"### Attempt {n++} · {Time(attempt.CompletedAt ?? attempt.StartedAt)} · {Verdict(result.Verdict)}");
                sb.AppendLine();
                sb.AppendLine(Cut(result.Summary));
                if (result.Questions.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("Questions it asked:");
                    foreach (var q in result.Questions)
                        sb.AppendLine($"- {q.Question}");
                }
                if (result.Investigated.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"It checked {result.Investigated.Count} things, among them: " +
                                  string.Join("; ", result.Investigated.Take(8).Select(c => $"{c.Check} ({c.Where})")));
                }
                sb.AppendLine();
            }
        }

        var current = triage?.Attempts.FirstOrDefault(a => a.WorkItemId == currentWorkItemId);
        if (current?.Feedback is { } feedback)
        {
            sb.AppendLine("## Feedback for this attempt");
            sb.AppendLine();
            sb.AppendLine($"From {feedback.Author ?? "a person"} — take it into account; it may correct an earlier attempt.");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(feedback.Text))
            {
                sb.AppendLine(feedback.Text.Trim());
                sb.AppendLine();
            }
            foreach (var answer in feedback.Answers)
            {
                sb.AppendLine($"- **Q:** {answer.Question}");
                sb.AppendLine($"  **A:** {answer.Answer}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Other recent triages of this project");
        sb.AppendLine();
        if (others.Count == 0)
        {
            sb.AppendLine("None.");
        }
        else
        {
            if (reportToTracker)
                sb.AppendLine("Use these to detect duplicates. Do not quote them in your result; name a duplicate only by its issue link.");
            else
                sb.AppendLine("Use these to detect duplicates.");
            sb.AppendLine();
            foreach (var other in others)
            {
                var latest = other.LatestResult;
                var verdict = latest is null ? "no result yet" : Verdict(latest.Verdict);
                var issues = other.CreatedIssues.Count > 0
                    ? " — created " + string.Join(", ", other.CreatedIssues.Select(i => i.Url ?? $"#{i.Identifier}"))
                    : "";
                var source = other.Source == TriageSource.Issue && other.IssueUrl is not null ? $" ({other.IssueUrl})" : "";
                sb.AppendLine($"- **{other.Title}**{source} · {Time(other.UpdatedAt)} · {verdict}{issues}");
                if (!reportToTracker && latest is not null)
                    sb.AppendLine($"  {Cut(latest.Summary)}");
            }
        }

        return sb.ToString();
    }

    private static string Verdict(TriageVerdict verdict) => verdict switch
    {
        TriageVerdict.CauseFound => "cause found",
        TriageVerdict.Inconclusive => "inconclusive",
        TriageVerdict.NotABug => "not a bug",
        TriageVerdict.Duplicate => "duplicate",
        _ => verdict.ToString()
    };

    private static string? Time(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Cut(string value) =>
        value.Length <= MaxSummaryChars ? value : value[..MaxSummaryChars] + "…";
}
