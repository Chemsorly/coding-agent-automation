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

    /// <summary>
    /// Validates candidate lines from the agent's output.
    /// </summary>
    public static BrainCandidateValidation ValidateCandidates(
        string? text,
        IReadOnlyList<string> stackLabels)
    {
        if (string.IsNullOrEmpty(text))
            return new BrainCandidateValidation([], 0);

        var kept = new List<string>();
        int dropped = 0;
        var lines = text.Split('\n');
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (kept.Count >= BrainLayout.MaxCandidates)
            {
                dropped++;
                continue;
            }
            if (BrainLineFormat.TryParseCandidate(line, stackLabels, out _))
                kept.Add(line);
            else
                dropped++;
        }
        return new BrainCandidateValidation(kept, dropped);
    }

    /// <summary>
    /// Renders the evidence section from a <see cref="BrainEvidence"/> instance.
    /// </summary>
    public static string RenderEvidence(BrainEvidence evidence)
    {
        if (evidence.Attempts.Count == 0 && evidence.Review is null && evidence.HumanComments.Count == 0)
            return "No failures, findings or human comments.";

        var sb = new StringBuilder();
        foreach (var attempt in evidence.Attempts)
        {
            sb.Append($"### Attempt {attempt.Number} — {attempt.Gate} ({attempt.Classification})");
            sb.Append('\n');
            if (attempt.CommitSha is not null)
            {
                sb.Append($"- commit: {attempt.CommitSha}");
                sb.Append('\n');
            }
            if (attempt.FailedTests.Count > 0)
            {
                sb.Append($"- failed tests: {string.Join(", ", attempt.FailedTests)}");
                sb.Append('\n');
            }
            foreach (var job in attempt.CiJobs)
            {
                var stepsText = job.FailedSteps.Count > 0
                    ? $"; failed steps: {string.Join(", ", job.FailedSteps)}"
                    : "";
                sb.Append($"- job {job.Name}{stepsText}");
                sb.Append('\n');
                foreach (var line in job.Lines)
                {
                    sb.Append($"  - {line}");
                    sb.Append('\n');
                }
            }
            if (attempt.NoCodeChange == true)
            {
                sb.Append("- next attempt changed no code");
                sb.Append('\n');
            }
            else if (attempt.ChangedPaths.Count > 0)
            {
                sb.Append($"- next attempt changed: {string.Join(", ", attempt.ChangedPaths)}");
                sb.Append('\n');
            }
        }

        if (evidence.Review is { } review)
        {
            sb.Append("### Review findings");
            sb.Append('\n');
            var reviewerSummaries = review.ByReviewer
                .Select(kv => $"{kv.Key}: {kv.Value.Critical} critical, {kv.Value.Warning} warnings");
            sb.Append($"- {string.Join("; ", reviewerSummaries)}");
            sb.Append('\n');
            foreach (var finding in review.TopFindings)
            {
                sb.Append($"- {finding}");
                sb.Append('\n');
            }
        }

        if (evidence.HumanComments.Count > 0)
        {
            sb.Append("### Human review comments");
            sb.Append('\n');
            foreach (var comment in evidence.HumanComments)
            {
                var location = comment.FilePath is not null
                    ? $" on {comment.FilePath}" + (comment.Line.HasValue ? $":{comment.Line}" : "")
                    : "";
                sb.Append($"- {comment.Author}{location} ({comment.CreatedAt:yyyy-MM-dd}): {comment.Excerpt}");
                sb.Append('\n');
            }
        }

        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// Composes and masks the full inbox file text.
    /// </summary>
    public static BrainInboxComposition Compose(
        BrainInboxHeader header,
        string? candidatesText,
        BrainEvidence evidence,
        string? existingText,
        IReadOnlyCollection<KeyValuePair<string, string>> secrets)
    {
        // 1. Validate candidates (or recover them from existing text)
        var candidateValidation = ValidateCandidates(candidatesText, header.StackLabels);
        IReadOnlyList<string> keptCandidates = candidateValidation.Kept;
        int droppedCandidates = candidateValidation.Dropped;

        // If candidatesText is null but existingText has candidates, recover them
        if (candidatesText is null && existingText is not null)
        {
            var recoveredCandidates = ExtractCandidatesFromExisting(existingText, header.StackLabels);
            keptCandidates = recoveredCandidates.Kept;
            droppedCandidates = recoveredCandidates.Dropped;
        }

        // 2. Build the evidence list — may be shortened later
        var attempts = new List<BrainEvidenceAttempt>(evidence.Attempts);
        bool shortened = false;

        // 3. Render, mask and size-check; drop oldest attempts when over budget
        string text;
        while (true)
        {
            text = RenderFull(header, keptCandidates, attempts, evidence, shortened);
            text = SecretMasker.Mask(text, secrets);
            if (Encoding.UTF8.GetByteCount(text) <= BrainLayout.MaxInboxBytes) break;
            if (attempts.Count <= 2)
            {
                // Drop human comments, then review section
                var evidenceWithoutComments = new BrainEvidence();
                foreach (var a in attempts) evidenceWithoutComments.Attempts.Add(a);
                evidenceWithoutComments.PostPr = evidence.PostPr;
                // Don't copy HumanComments or Review
                text = RenderFull(header, keptCandidates, attempts, evidenceWithoutComments, true);
                text = SecretMasker.Mask(text, secrets);
                shortened = true;
                break;
            }
            // Drop the oldest attempt that is neither first nor newest
            attempts.RemoveAt(1);
            shortened = true;
        }

        return new BrainInboxComposition(text, keptCandidates, droppedCandidates, evidence.Attempts.Count, shortened);
    }

    private static string RenderFull(
        BrainInboxHeader header,
        IReadOnlyList<string> candidates,
        List<BrainEvidenceAttempt> attempts,
        BrainEvidence evidence,
        bool shortened)
    {
        var sb = new StringBuilder();

        // Marker
        sb.Append(BrainLayout.InboxMarker);
        sb.Append('\n');

        // Title
        sb.Append($"# Run {header.RunId}");
        sb.Append('\n');

        // Header fields
        sb.Append($"- run type: {header.RunType}");
        sb.Append('\n');
        sb.Append($"- outcome: {header.Outcome}");
        sb.Append('\n');
        sb.Append($"- repository: {header.RepositoryFullName}");
        sb.Append('\n');
        if (header.ProjectName is not null)
        {
            sb.Append($"- project: {header.ProjectName}");
            sb.Append('\n');
        }
        var labelsText = header.StackLabels.Count > 0
            ? string.Join(", ", header.StackLabels)
            : "none";
        sb.Append($"- stack labels: {labelsText}");
        sb.Append('\n');
        sb.Append($"- issue: {header.Issue}");
        sb.Append('\n');
        if (header.PullRequestUrl is not null)
        {
            sb.Append($"- pull request: {header.PullRequestUrl}");
            sb.Append('\n');
        }
        if (header.SkillsCommit is not null && header.SkillsFiles.Count > 0)
        {
            sb.Append($"- skills injected from: {header.SkillsCommit} ({string.Join(", ", header.SkillsFiles)})");
        }
        else
        {
            sb.Append("- skills injected from: none");
        }
        sb.Append('\n');

        // Candidates section (omit for drafts unless recovered from existing text)
        if (candidates.Count > 0)
        {
            sb.Append('\n');
            sb.Append("## Candidates");
            sb.Append('\n');
            foreach (var c in candidates)
            {
                sb.Append(c);
                sb.Append('\n');
            }
        }

        // Evidence section using a temporary evidence with possibly truncated attempts
        var evidenceForRender = BuildEvidenceForRender(evidence, attempts);
        sb.Append('\n');
        sb.Append("## Evidence");
        sb.Append('\n');
        sb.Append(RenderEvidence(evidenceForRender));
        sb.Append('\n');

        if (shortened)
        {
            sb.Append(ShortenedMarker);
            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static BrainEvidence BuildEvidenceForRender(BrainEvidence source, List<BrainEvidenceAttempt> attempts)
    {
        var e = new BrainEvidence();
        foreach (var a in attempts) e.Attempts.Add(a);
        e.Review = source.Review;
        foreach (var c in source.HumanComments) e.HumanComments.Add(c);
        e.PostPr = source.PostPr;
        return e;
    }

    private static BrainCandidateValidation ExtractCandidatesFromExisting(
        string existingText,
        IReadOnlyList<string> stackLabels)
    {
        const string sectionHeader = "## Candidates";
        var lines = existingText.Split('\n');
        bool inSection = false;
        var kept = new List<string>();
        int dropped = 0;
        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            if (line == sectionHeader) { inSection = true; continue; }
            if (inSection)
            {
                if (line.StartsWith("## ", StringComparison.Ordinal)) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (kept.Count >= BrainLayout.MaxCandidates) { dropped++; continue; }
                if (BrainLineFormat.TryParseCandidate(line, stackLabels, out _))
                    kept.Add(line);
                else
                    dropped++;
            }
        }
        return new BrainCandidateValidation(kept, dropped);
    }
}
