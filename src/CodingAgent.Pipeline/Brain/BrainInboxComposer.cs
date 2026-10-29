using System.Text;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.Brain;

/// <summary>Header metadata for a brain inbox file.</summary>
public sealed record BrainInboxHeader(
    string RunId,
    string RunType,
    string Outcome,
    string RepositoryFullName,
    string? ProjectName,
    IReadOnlyList<string> StackLabels,
    string Issue,
    string? PullRequestUrl,
    string? SkillsCommit,
    IReadOnlyList<string> SkillsFiles);

/// <summary>Result of validating candidate lines from the agent.</summary>
public sealed record BrainCandidateValidation(IReadOnlyList<string> Kept, int Dropped);

/// <summary>Result of composing a brain inbox file.</summary>
public sealed record BrainInboxComposition(
    string Text,
    IReadOnlyList<string> KeptCandidates,
    int DroppedCandidates,
    int EvidenceAttempts,
    bool Shortened);

/// <summary>
/// Composes the inbox file for a run (spec 054, req 3). Pure — no I/O, no clock.
/// </summary>
public static class BrainInboxComposer
{
    private const string ShortenedMarker = "_Evidence shortened to fit 16 KB._";

    // ── Public API ────────────────────────────────────────────────────

    /// <summary>Validates candidate lines from the agent's output.</summary>
    public static BrainCandidateValidation ValidateCandidates(string? text, IReadOnlyList<string> stackLabels)
    {
        if (string.IsNullOrEmpty(text)) return new BrainCandidateValidation([], 0);
        var kept = new List<string>();
        int dropped = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (kept.Count >= BrainLayout.MaxCandidates) { dropped++; continue; }
            if (BrainLineFormat.TryParseCandidate(line, stackLabels, out _)) kept.Add(line);
            else dropped++;
        }
        return new BrainCandidateValidation(kept, dropped);
    }

    /// <summary>Renders the evidence section.</summary>
    public static string RenderEvidence(BrainEvidence evidence)
    {
        if (evidence.Attempts.Count == 0 && evidence.Review is null && evidence.HumanComments.Count == 0)
            return "No failures, findings or human comments.";
        var sb = new StringBuilder();
        RenderAttempts(sb, evidence.Attempts);
        RenderReview(sb, evidence.Review);
        RenderHumanComments(sb, evidence.HumanComments);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Composes and masks the full inbox file text.</summary>
    public static BrainInboxComposition Compose(
        BrainInboxHeader header,
        string? candidatesText,
        BrainEvidence evidence,
        string? existingText,
        IReadOnlyCollection<KeyValuePair<string, string>> secrets)
    {
        var (keptCandidates, droppedCandidates) = ResolveCandidates(candidatesText, existingText, header.StackLabels);
        var attempts = new List<BrainEvidenceAttempt>(evidence.Attempts);
        bool shortened = false;

        string text;
        while (true)
        {
            text = SecretMasker.Mask(RenderFull(header, keptCandidates, attempts, evidence, shortened), secrets);
            if (Encoding.UTF8.GetByteCount(text) <= BrainLayout.MaxInboxBytes) break;
            if (attempts.Count <= 2)
            {
                text = SecretMasker.Mask(RenderFull(header, keptCandidates, attempts, new BrainEvidence { PostPr = evidence.PostPr }, true), secrets);
                shortened = true;
                break;
            }
            attempts.RemoveAt(1);
            shortened = true;
        }
        return new BrainInboxComposition(text, keptCandidates, droppedCandidates, evidence.Attempts.Count, shortened);
    }

    // ── Private helpers ───────────────────────────────────────────────

    private static (IReadOnlyList<string> kept, int dropped) ResolveCandidates(
        string? candidatesText,
        string? existingText,
        IReadOnlyList<string> stackLabels)
    {
        if (candidatesText is not null)
        {
            var v = ValidateCandidates(candidatesText, stackLabels);
            return (v.Kept, v.Dropped);
        }
        if (existingText is not null)
        {
            var v = ExtractCandidatesFromExisting(existingText, stackLabels);
            return (v.Kept, v.Dropped);
        }
        return ([], 0);
    }

    private static string RenderFull(
        BrainInboxHeader header,
        IReadOnlyList<string> candidates,
        List<BrainEvidenceAttempt> attempts,
        BrainEvidence evidence,
        bool shortened)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, header);
        AppendCandidates(sb, candidates);
        sb.Append("\n## Evidence\n");
        var forRender = BuildEvidenceForRender(evidence, attempts);
        sb.Append(RenderEvidence(forRender));
        sb.Append('\n');
        if (shortened) sb.Append($"{ShortenedMarker}\n");
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, BrainInboxHeader h)
    {
        sb.Append($"{BrainLayout.InboxMarker}\n# Run {h.RunId}\n");
        sb.Append($"- run type: {h.RunType}\n");
        sb.Append($"- outcome: {h.Outcome}\n");
        sb.Append($"- repository: {h.RepositoryFullName}\n");
        if (h.ProjectName is not null) sb.Append($"- project: {h.ProjectName}\n");
        sb.Append($"- stack labels: {(h.StackLabels.Count > 0 ? string.Join(", ", h.StackLabels) : "none")}\n");
        sb.Append($"- issue: {h.Issue}\n");
        if (h.PullRequestUrl is not null) sb.Append($"- pull request: {h.PullRequestUrl}\n");
        var skills = h.SkillsCommit is not null && h.SkillsFiles.Count > 0
            ? $"{h.SkillsCommit} ({string.Join(", ", h.SkillsFiles)})" : "none";
        sb.Append($"- skills injected from: {skills}\n");
    }

    private static void AppendCandidates(StringBuilder sb, IReadOnlyList<string> candidates)
    {
        if (candidates.Count == 0) return;
        sb.Append("\n## Candidates\n");
        foreach (var c in candidates) sb.Append($"{c}\n");
    }

    private static void RenderAttempts(StringBuilder sb, IReadOnlyList<BrainEvidenceAttempt> attempts)
    {
        foreach (var a in attempts)
        {
            sb.Append($"### Attempt {a.Number} — {a.Gate} ({a.Classification})\n");
            if (a.CommitSha is not null) sb.Append($"- commit: {a.CommitSha}\n");
            if (a.FailedTests.Count > 0) sb.Append($"- failed tests: {string.Join(", ", a.FailedTests)}\n");
            foreach (var job in a.CiJobs)
            {
                var steps = job.FailedSteps.Count > 0 ? $"; failed steps: {string.Join(", ", job.FailedSteps)}" : "";
                sb.Append($"- job {job.Name}{steps}\n");
                foreach (var l in job.Lines) sb.Append($"  - {l}\n");
            }
            if (a.NoCodeChange == true) sb.Append("- next attempt changed no code\n");
            else if (a.ChangedPaths.Count > 0) sb.Append($"- next attempt changed: {string.Join(", ", a.ChangedPaths)}\n");
        }
    }

    private static void RenderReview(StringBuilder sb, BrainEvidenceReview? review)
    {
        if (review is null) return;
        sb.Append("### Review findings\n");
        sb.Append($"- {string.Join("; ", review.ByReviewer.Select(kv => $"{kv.Key}: {kv.Value.Critical} critical, {kv.Value.Warning} warnings"))}\n");
        foreach (var f in review.TopFindings) sb.Append($"- {f}\n");
    }

    private static void RenderHumanComments(StringBuilder sb, IReadOnlyList<BrainEvidenceComment> comments)
    {
        if (comments.Count == 0) return;
        sb.Append("### Human review comments\n");
        foreach (var c in comments)
        {
            var lineNum = c.Line.HasValue ? $":{c.Line}" : "";
            var loc = c.FilePath is not null ? $" on {c.FilePath}{lineNum}" : "";
            sb.Append($"- {c.Author}{loc} ({c.CreatedAt:yyyy-MM-dd}): {c.Excerpt}\n");
        }
    }

    private static BrainEvidence BuildEvidenceForRender(BrainEvidence source, List<BrainEvidenceAttempt> attempts)
    {
        var e = new BrainEvidence { PostPr = source.PostPr };
        foreach (var a in attempts) e.AddAttempt(a);
        e.Review = source.Review;
        e.SetHumanComments(source.HumanComments);
        return e;
    }

    private static BrainCandidateValidation ExtractCandidatesFromExisting(
        string existingText, IReadOnlyList<string> stackLabels)
    {
        const string sectionHeader = "## Candidates";
        bool inSection = false;
        var kept = new List<string>();
        int dropped = 0;
        foreach (var rawLine in existingText.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line == sectionHeader) { inSection = true; continue; }
            if (!inSection) continue;
            if (line.StartsWith("## ", StringComparison.Ordinal)) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (kept.Count >= BrainLayout.MaxCandidates) { dropped++; continue; }
            if (BrainLineFormat.TryParseCandidate(line, stackLabels, out _)) kept.Add(line);
            else dropped++;
        }
        return new BrainCandidateValidation(kept, dropped);
    }
}
