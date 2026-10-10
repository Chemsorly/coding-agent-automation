using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Services;

/// <summary>
/// Labels and pill tones of the triage pages. One place, so the list, the triage page, Attention and the
/// run page name a status the same way.
/// </summary>
public static class TriageDisplay
{
    public static string StatusLabel(TriageStatus status) => status switch
    {
        TriageStatus.New => "Queued",
        TriageStatus.Investigating => "Investigating",
        TriageStatus.NeedsReview => "RCA ready",
        TriageStatus.NeedsInput => "Needs input",
        TriageStatus.NotABug => "Not a bug",
        TriageStatus.Duplicate => "Duplicate",
        TriageStatus.IssuesCreated => "Issues created",
        TriageStatus.Dismissed => "Dismissed",
        TriageStatus.Failed => "Failed",
        TriageStatus.Cancelled => "Cancelled",
        _ => status.ToString()
    };

    /// <summary>The pill tone: <c>run</c>, <c>warn</c>, <c>ok</c>, <c>err</c> or <c>mute</c>.</summary>
    public static string StatusTone(TriageStatus status) => status switch
    {
        TriageStatus.New or TriageStatus.Investigating => "run",
        TriageStatus.NeedsReview or TriageStatus.NeedsInput => "warn",
        TriageStatus.IssuesCreated => "ok",
        TriageStatus.Failed => "err",
        _ => "mute"
    };

    public static string VerdictLabel(TriageVerdict verdict) => verdict switch
    {
        TriageVerdict.CauseFound => "Cause found",
        TriageVerdict.Inconclusive => "Inconclusive",
        TriageVerdict.NotABug => "Not a bug",
        TriageVerdict.Duplicate => "Duplicate",
        _ => verdict.ToString()
    };

    public static string VerdictTone(TriageVerdict verdict) => verdict == TriageVerdict.CauseFound ? "ok" : "mute";

    /// <summary>"Cause found · high", or the verdict alone when the agent gave no confidence.</summary>
    public static string VerdictWithConfidence(TriageVerdict verdict, TriageConfidence? confidence) =>
        confidence is { } c ? $"{VerdictLabel(verdict)} · {ConfidenceLabel(c)}" : VerdictLabel(verdict);

    public static string ConfidenceLabel(TriageConfidence confidence) => confidence switch
    {
        TriageConfidence.High => "high",
        TriageConfidence.Medium => "medium",
        TriageConfidence.Low => "low",
        _ => confidence.ToString().ToLowerInvariant()
    };

    public static string KindLabel(TriageDraftKind kind) => kind switch
    {
        TriageDraftKind.RootFix => "Root fix",
        TriageDraftKind.Mitigation => "Mitigation",
        TriageDraftKind.Prevention => "Prevention",
        _ => kind.ToString()
    };

    /// <summary>The draft kind's pill tone: <c>kind-fix</c>, <c>kind-mit</c> or <c>kind-prev</c>.</summary>
    public static string KindTone(TriageDraftKind kind) => kind switch
    {
        TriageDraftKind.Mitigation => "kind-mit",
        TriageDraftKind.Prevention => "kind-prev",
        _ => "kind-fix"
    };

    public static string HypothesisLabel(TriageHypothesisState state) => state switch
    {
        TriageHypothesisState.Confirmed => "confirmed",
        TriageHypothesisState.RuledOut => "ruled out",
        _ => "open"
    };

    /// <summary>The tag a chain step shows, or null for a plain step.</summary>
    public static string? ChainTag(TriageChainKind kind) => kind switch
    {
        TriageChainKind.Symptom => "Symptom",
        TriageChainKind.Trigger => "Trigger",
        TriageChainKind.RootCause => "Root cause",
        _ => null
    };

    /// <summary>"Operator · anna" or "Issue #431".</summary>
    public static string SourceText(TriageSource source, string? requestedBy, string? issueIdentifier) =>
        source == TriageSource.Issue
            ? $"Issue #{issueIdentifier}"
            : string.IsNullOrEmpty(requestedBy) ? "Operator" : $"Operator · {requestedBy}";

    /// <summary>
    /// Checks per source, derived from the agent's investigated list: the part of "where" before the first
    /// " · " ("grafana · Loki" counts for grafana), most checks first.
    /// </summary>
    public static IReadOnlyList<(string Source, int Count)> ChecksBySource(IEnumerable<TriageCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);
        return checks
            .Select(c => SourceOf(c.Where))
            .Where(s => s.Length > 0)
            .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Source: g.First(), Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Source, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string SourceOf(string? where)
    {
        if (string.IsNullOrWhiteSpace(where))
            return "";
        var cut = where.IndexOf('·');
        return (cut >= 0 ? where[..cut] : where).Trim();
    }

    /// <summary>A link target that is safe to render as <c>href</c>: http(s) only, else null.</summary>
    public static string? SafeLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.AbsoluteUri
            : null;

    public static string Ago(DateTimeOffset t, DateTimeOffset now)
    {
        var d = now - t;
        if (d.TotalMinutes < 1) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        return d.TotalDays < 2 ? "yesterday" : $"{(int)d.TotalDays} days ago";
    }

    public static string Duration(DateTimeOffset start, DateTimeOffset? end)
    {
        if (end is null) return "running";
        var d = end.Value - start;
        if (d.TotalHours >= 1) return $"{(int)d.TotalHours} h {d.Minutes} min";
        if (d.TotalMinutes >= 1) return $"{(int)d.TotalMinutes} min";
        return $"{Math.Max(0, (int)d.TotalSeconds)} s";
    }

    /// <summary>The text shown for an operator's "when": "Oct 8 13:45 → now".</summary>
    public static string? WhenText(TriageRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.From is null && request.Until is null)
            return null;
        var from = request.From is { } f ? f.ToLocalTime().ToString("MMM d HH:mm") : "?";
        var until = request.Until is { } u ? u.ToLocalTime().ToString("MMM d HH:mm") : "now";
        return $"{from} → {until}";
    }
}
