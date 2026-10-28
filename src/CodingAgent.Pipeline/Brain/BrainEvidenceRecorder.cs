using CodingAgent.Pipeline.CodeReview;
using CodingAgent.Pipeline.CodeReview.Models;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.Brain;

/// <summary>
/// Mutates <see cref="PipelineRun.BrainEvidence"/>, creating it lazily.
/// All methods are non-throwing — brain steps are non-fatal (req 2.7, T6).
/// Agent-only: never serialised or sent to the API.
/// </summary>
public static class BrainEvidenceRecorder
{
    // ── Gate recording ────────────────────────────────────────────────

    /// <summary>
    /// Records a failed or passed gate attempt. When the report indicates all passed,
    /// clears the pending buffer (the next attempt may succeed without needing to be recorded).
    /// </summary>
    public static void RecordReport(PipelineRun run, int number, QualityGateReport report)
    {
        if (report.AllPassed)
        {
            run.BrainEvidence?.Pending.CiJobs.Clear();
            run.BrainEvidence?.Pending.CiFailedTests.Clear();
            if (run.BrainEvidence is { } e)
            {
                e.Pending.CommitSha = null;
                e.Pending.Classification = null;
            }
            return;
        }

        var evidence = EnsureEvidence(run);

        // Determine which gate failed first
        string gate;
        if (!report.Compilation.Passed)
            gate = "Compilation";
        else if (!report.Tests.Passed)
            gate = "Tests";
        else if (report.ExternalCi is { Passed: false })
            gate = evidence.PostPr ? "Post-PR CI" : "External CI";
        else
            gate = evidence.PostPr ? "Post-PR CI" : "External CI";

        // Classification
        string classification;
        if (gate == "Tests" && report.Tests.IsInfrastructureFailure == true)
            classification = "infrastructure";
        else if (gate is "External CI" or "Post-PR CI")
            classification = evidence.Pending.Classification ?? "unknown";
        else
            classification = "code";

        // Failed tests: from the Tests gate
        IReadOnlyList<string> failedTests = gate == "Tests"
            ? report.Tests.FailedTestNames ?? []
            : evidence.Pending.CiFailedTests.Take(BrainLayout.MaxFailedTestNames).ToList();

        var attempt = new BrainEvidenceAttempt(
            number,
            gate,
            failedTests,
            evidence.Pending.CiJobs.ToList(),
            classification,
            null,           // NoCodeChange: set by the next fix (DD5)
            [],             // ChangedPaths: set by the next fix
            evidence.Pending.CommitSha);

        // Cap: keep first + newest 9 = 10 total
        AddAttempt(evidence, attempt);

        // Clear pending
        evidence.Pending.CommitSha = null;
        evidence.Pending.CiJobs.Clear();
        evidence.Pending.CiFailedTests.Clear();
        evidence.Pending.Classification = null;
    }

    /// <summary>Adds an attempt, keeping at most MaxEvidenceAttempts (first + newest 9).</summary>
    private static void AddAttempt(BrainEvidence evidence, BrainEvidenceAttempt attempt)
    {
        // Replace if same number already recorded
        int idx = evidence.Attempts.FindIndex(a => a.Number == attempt.Number);
        if (idx >= 0)
        {
            evidence.Attempts[idx] = attempt;
            return;
        }
        evidence.Attempts.Add(attempt);
        // Keep first + newest 9
        while (evidence.Attempts.Count > BrainLayout.MaxEvidenceAttempts)
        {
            // Remove the second-oldest (index 1), keeping the first at index 0
            evidence.Attempts.RemoveAt(1);
        }
    }

    // ── CI failure classification ─────────────────────────────────────

    /// <summary>
    /// Records the CI failure classification and extracts job evidence into the pending buffer.
    /// </summary>
    public static void RecordCiFailure(
        PipelineRun run,
        PipelineRunStatus status,
        CiFailureClassifier.CiFailureCategory category)
    {
        var evidence = EnsureEvidence(run);
        evidence.Pending.Classification = category switch
        {
            CiFailureClassifier.CiFailureCategory.CodeFailure => "code",
            CiFailureClassifier.CiFailureCategory.Infrastructure => "infrastructure",
            CiFailureClassifier.CiFailureCategory.RateLimited => "rate_limited",
            _ => "unknown"
        };

        int testCount = 0;
        foreach (var job in status.Jobs)
        {
            if (!job.EndedUnsuccessfully()) continue;
            var ciJob = CiLogEvidenceExtractor.Extract(job.Name, job.FailedSteps, job.LogContent);
            evidence.Pending.CiJobs.Add(ciJob);
            if (testCount < BrainLayout.MaxFailedTestNames)
            {
                var names = CiLogEvidenceExtractor.ExtractFailedTestNames(job.LogContent);
                foreach (var name in names)
                {
                    if (testCount >= BrainLayout.MaxFailedTestNames) break;
                    evidence.Pending.CiFailedTests.Add(name);
                    testCount++;
                }
            }
        }
    }

    /// <summary>Records a CI status error classification.</summary>
    public static void RecordCiStatusError(PipelineRun run, string gate, string message)
    {
        EnsureEvidence(run).Pending.Classification = "ci_status_error";
    }

    /// <summary>Records a CI timeout classification.</summary>
    public static void RecordCiTimeout(PipelineRun run)
    {
        EnsureEvidence(run).Pending.Classification = "timeout";
    }

    // ── Fix fingerprints ──────────────────────────────────────────────

    /// <summary>
    /// Updates the last recorded attempt with the changed paths computed from working-tree fingerprints.
    /// </summary>
    public static void RecordFixFingerprints(
        PipelineRun run,
        IReadOnlyDictionary<string, string>? before,
        IReadOnlyDictionary<string, string>? after)
    {
        var evidence = run.BrainEvidence;
        if (evidence is null || evidence.Attempts.Count == 0) return;
        if (before is null || after is null) return;

        var changed = new HashSet<string>(StringComparer.Ordinal);
        changed.UnionWith(before.Where(kv => !after.TryGetValue(kv.Key, out var v) || v != kv.Value).Select(kv => kv.Key));
        changed.UnionWith(after.Where(kv => !before.ContainsKey(kv.Key)).Select(kv => kv.Key));

        var paths = changed.OrderBy(p => p, StringComparer.Ordinal)
            .Take(BrainLayout.MaxChangedPaths)
            .ToList();
        var noCodeChange = paths.Count == 0;

        var last = evidence.Attempts[^1];
        evidence.Attempts[^1] = last with { ChangedPaths = paths, NoCodeChange = noCodeChange };
    }

    /// <summary>Marks the last attempt as having no code change.</summary>
    public static void RecordNoCodeChange(PipelineRun run)
    {
        var evidence = run.BrainEvidence;
        if (evidence is null || evidence.Attempts.Count == 0) return;
        var last = evidence.Attempts[^1];
        evidence.Attempts[^1] = last with { NoCodeChange = true };
    }

    /// <summary>Stores the commit SHA for the next gate attempt.</summary>
    public static void RecordCommit(PipelineRun run, string? sha)
    {
        EnsureEvidence(run).Pending.CommitSha = sha;
    }

    /// <summary>Marks the evidence as post-PR (affects gate labeling).</summary>
    public static void MarkPostPr(PipelineRun run)
    {
        EnsureEvidence(run).PostPr = true;
    }

    // ── Code review ───────────────────────────────────────────────────

    /// <summary>
    /// Records code review findings from all reviewers on the run.
    /// </summary>
    public static void RecordReview(PipelineRun run)
    {
        var evidence = EnsureEvidence(run);
        var byReviewer = new Dictionary<string, (int Critical, int Warning)>(StringComparer.Ordinal);
        var topFindings = new List<string>();

        foreach (var (reviewer, text) in run.CodeReviewAgentFindings)
        {
            var findings = FindingsParser.Parse(text, reviewer);
            int critical = findings.Count(f => f.Severity == FindingSeverity.Critical);
            int warning = findings.Count(f => f.Severity == FindingSeverity.Warning);
            byReviewer[reviewer] = (critical, warning);
            AddCriticalFindings(findings, topFindings);
        }
        AddWarningFindings(run.CodeReviewAgentFindings, topFindings);
        evidence.Review = new BrainEvidenceReview(byReviewer, topFindings);
    }

    private static void AddCriticalFindings(
        IReadOnlyList<StructuredFinding> findings,
        List<string> topFindings)
    {
        if (topFindings.Count >= BrainLayout.MaxFindingLines) return;
        foreach (var f in findings.Where(f => f.Severity == FindingSeverity.Critical))
        {
            if (topFindings.Count >= BrainLayout.MaxFindingLines) break;
            topFindings.Add(FormatFinding("CRITICAL", f));
        }
    }

    private static void AddWarningFindings(
        System.Collections.Concurrent.ConcurrentDictionary<string, string> findingsByReviewer,
        List<string> topFindings)
    {
        foreach (var (_, text) in findingsByReviewer)
        {
            if (topFindings.Count >= BrainLayout.MaxFindingLines) break;
            foreach (var f in FindingsParser.Parse(text, "").Where(f => f.Severity == FindingSeverity.Warning))
            {
                if (topFindings.Count >= BrainLayout.MaxFindingLines) break;
                topFindings.Add(FormatFinding("WARNING", f));
            }
        }
    }

    private static string FormatFinding(string severity, StructuredFinding f)
    {
        var location = f.FilePath is not null ? $"{f.FilePath}:{f.LineNumber} — " : "";
        var full = $"[{severity}] {location}{f.Message}";
        return full.Length > BrainLayout.MaxExcerptChars ? full[..BrainLayout.MaxExcerptChars] : full;
    }

    // ── Human comments ────────────────────────────────────────────────

    /// <summary>
    /// Records human review comments from a PR, filtering out bots and pipeline-generated messages.
    /// </summary>
    public static void RecordHumanComments(
        PipelineRun run,
        IEnumerable<PrConversationComment> comments)
    {
        var evidence = EnsureEvidence(run);
        var eligible = comments
            .Where(c => !c.IsBot)
            .Where(c => !CommentMarkers.IsPipelineGeneratedComment(c.Body))
            .Where(c => !c.Body.StartsWith("🔴 **CRITICAL**", StringComparison.Ordinal))
            .Where(c => !c.Body.StartsWith("🟡 **WARNING**", StringComparison.Ordinal))
            .Where(c => !c.Body.StartsWith("💡 **SUGGESTION**", StringComparison.Ordinal))
            .OrderByDescending(c => c.CreatedAt)
            .Take(BrainLayout.MaxHumanComments)
            .Select(c =>
            {
                var excerpt = c.Body.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
                if (excerpt.Length > BrainLayout.MaxExcerptChars)
                    excerpt = excerpt[..BrainLayout.MaxExcerptChars];
                return new BrainEvidenceComment(
                    c.Author,
                    c.FilePath,
                    c.Line,
                    new DateTimeOffset(DateTime.SpecifyKind(c.CreatedAt, DateTimeKind.Utc)),
                    excerpt);
            });

        evidence.HumanComments.Clear();
        evidence.HumanComments.AddRange(eligible);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static BrainEvidence EnsureEvidence(PipelineRun run)
    {
        if (run.BrainEvidence is null)
            run.BrainEvidence = new BrainEvidence();
        return run.BrainEvidence;
    }
}
