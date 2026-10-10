using System.Text;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Renders a triage result as the comment posted on a tracker issue. The markup is the pipeline's; every
/// value the agent wrote goes through <see cref="TextSanitizer.SanitizeMarkdown"/> first, so agent text cannot
/// break the markup, the marker or mention people. The comment stays under
/// <see cref="TriageConstants.MaxCommentChars"/>: draft bodies are shortened first, then the investigated
/// details, then the evidence; the full report is always in the app.
/// </summary>
public static class TriageCommentRenderer
{
    private const int ShortDraftBodyChars = 1_500;
    private const int ShortEvidenceCount = 15;

    private enum Detail { Full, ShortDrafts, ShortInvestigated, ShortEvidence }

    public static string Render(TriageResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        foreach (var detail in Enum.GetValues<Detail>())
        {
            var comment = RenderAt(result, detail);
            if (comment.Length <= TriageConstants.MaxCommentChars)
                return comment;
        }

        var last = RenderAt(result, Detail.ShortEvidence);
        const string Cut = "\n\n…(cut — the full report is in Coding Agent → Triage)";
        return last[..(TriageConstants.MaxCommentChars - Cut.Length)] + Cut;
    }

    private static string RenderAt(TriageResult result, Detail detail)
    {
        var sb = new StringBuilder();
        sb.AppendLine(TriageConstants.CommentMarker);
        sb.AppendLine("## Triage: root cause analysis");
        sb.AppendLine();

        var reproduced = result.Reproduced
            ? "yes" + (result.Reproduction is { } how ? $" ({V(how)})" : "")
            : "no";
        sb.Append($"**Verdict:** {VerdictText(result.Verdict)}");
        if (result.Confidence is { } confidence)
            sb.Append($" · **Confidence:** {confidence.ToString().ToLowerInvariant()}");
        sb.AppendLine($" · **Reproduced:** {reproduced}");
        sb.AppendLine();
        sb.AppendLine(V(result.Summary));
        sb.AppendLine();
        if (result.Impact is { } impact)
        {
            sb.AppendLine($"**Impact:** {V(impact)}");
            sb.AppendLine();
        }
        if (result.Verdict == TriageVerdict.Duplicate && result.DuplicateOf is { } duplicate)
        {
            sb.AppendLine($"**Duplicate of:** {V(duplicate)}");
            sb.AppendLine();
        }

        if (result.CausalChain.Count > 0)
        {
            sb.AppendLine("### From symptom to cause");
            sb.AppendLine();
            var n = 1;
            foreach (var link in result.CausalChain)
                sb.AppendLine($"{n++}. {ChainPrefix(link.Kind)}{V(link.Text)}{Refs(link.EvidenceIds)}");
            sb.AppendLine();
        }

        if (result.Evidence.Count > 0)
        {
            var evidence = detail == Detail.ShortEvidence ? result.Evidence.Take(ShortEvidenceCount).ToList() : result.Evidence.ToList();
            sb.AppendLine("### Evidence");
            sb.AppendLine();
            sb.AppendLine("| # | Claim | Source |");
            sb.AppendLine("|---|---|---|");
            foreach (var e in evidence)
            {
                var claim = Cell(e.Claim) + (e.Query is { } q ? $"<br>`{Code(q)}`" : "");
                var source = Cell(e.Source) + (e.Link is { } link && IsHttpUrl(link) ? $" [link]({link})" : "");
                sb.AppendLine($"| {Cell(e.Id)} | {claim} | {source} |");
            }
            if (evidence.Count < result.Evidence.Count)
                sb.AppendLine($"| | …and {result.Evidence.Count - evidence.Count} more in the app | |");
            sb.AppendLine();
        }

        if (result.Hypotheses.Count > 0)
        {
            sb.AppendLine("### Hypotheses");
            sb.AppendLine();
            foreach (var h in result.Hypotheses)
                sb.AppendLine($"- **{V(h.Id)}** {V(h.Text)} — {StateText(h.State)}");
            sb.AppendLine();
        }

        if (result.Investigated.Count > 0)
        {
            sb.AppendLine($"<details><summary>What I investigated ({result.Investigated.Count} checks)</summary>");
            sb.AppendLine();
            var n = 1;
            foreach (var c in result.Investigated)
            {
                var forPart = c.For is { } f ? $" (for {V(f)})" : "";
                sb.AppendLine(detail >= Detail.ShortInvestigated
                    ? $"{n++}. {V(c.Check)} — {V(c.Where)}"
                    : $"{n++}. {V(c.Check)} — {V(c.Where)}{forPart}: {V(c.Result)}{Refs(c.EvidenceIds)}");
            }
            sb.AppendLine();
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        if (result.NotChecked.Count > 0)
        {
            sb.AppendLine("### Not checked");
            sb.AppendLine();
            foreach (var g in result.NotChecked)
                sb.AppendLine($"- **{V(g.What)}**: {V(g.Why)}");
            sb.AppendLine();
        }

        if (result.Questions.Count > 0)
        {
            sb.AppendLine("### Questions");
            sb.AppendLine();
            var n = 1;
            foreach (var q in result.Questions)
                sb.AppendLine($"{n++}. {V(q.Question)}" + (q.Why is { } why ? $" — {V(why)}" : ""));
            sb.AppendLine();
        }

        if (result.Drafts.Count > 0)
        {
            sb.AppendLine($"<details open><summary>Proposed issues ({result.Drafts.Count}) — created in Coding Agent after review</summary>");
            sb.AppendLine();
            var n = 1;
            foreach (var d in result.Drafts)
            {
                sb.AppendLine($"#### {n++}. {KindText(d.Kind)} → {V(d.TargetRepository)}: {V(d.Title)}");
                sb.AppendLine();
                var body = detail >= Detail.ShortDrafts && d.Body.Length > ShortDraftBodyChars
                    ? d.Body[..ShortDraftBodyChars] + "\n\n…(shortened — the full draft is in the app)"
                    : d.Body;
                sb.AppendLine(V(body));
                sb.AppendLine();
            }
            sb.AppendLine("</details>");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("_Review, edit and create the proposed issues in Coding Agent → Triage. To ask for another attempt, " +
                      "comment with your feedback or answers, then replace the status label with `agent:triage`._");
        return sb.ToString();
    }

    /// <summary>A value the agent wrote, made safe for the markup around it.</summary>
    private static string V(string value) => TextSanitizer.SanitizeMarkdown(value.Trim());

    private static string Cell(string value) =>
        V(value).Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal);

    private static string Code(string value) =>
        TextSanitizer.SanitizeMarkdown(value.Trim())
            .Replace("`", "'", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

    private static string Refs(IReadOnlyList<string> ids) =>
        ids.Count == 0 ? "" : $" ({string.Join(", ", ids.Select(V))})";

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && !value.Contains(')', StringComparison.Ordinal) && !value.Contains(' ', StringComparison.Ordinal);

    internal static string VerdictText(TriageVerdict verdict) => verdict switch
    {
        TriageVerdict.CauseFound => "cause found",
        TriageVerdict.Inconclusive => "inconclusive",
        TriageVerdict.NotABug => "not a bug",
        TriageVerdict.Duplicate => "duplicate",
        _ => verdict.ToString()
    };

    private static string StateText(TriageHypothesisState state) => state switch
    {
        TriageHypothesisState.Confirmed => "confirmed",
        TriageHypothesisState.RuledOut => "ruled out",
        _ => "open"
    };

    private static string ChainPrefix(TriageChainKind kind) => kind switch
    {
        TriageChainKind.Symptom => "**Symptom:** ",
        TriageChainKind.Trigger => "**Trigger:** ",
        TriageChainKind.RootCause => "**Root cause:** ",
        _ => ""
    };

    internal static string KindText(TriageDraftKind kind) => kind switch
    {
        TriageDraftKind.RootFix => "Root fix",
        TriageDraftKind.Mitigation => "Mitigation",
        TriageDraftKind.Prevention => "Prevention",
        _ => kind.ToString()
    };
}
