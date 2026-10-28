namespace CodingAgent.Pipeline.Brain;

/// <summary>
/// Evidence accumulated during a run for writing into the brain inbox file.
/// Agent-only — never sent to the API or stored in Redis (spec 054, req 4, DD4).
/// </summary>
public sealed class BrainEvidence
{
    /// <summary>Failed gate attempts, at most 10 kept (first and newest 9).</summary>
    public List<BrainEvidenceAttempt> Attempts { get; } = [];

    /// <summary>Code-review findings summary, populated after the review phase.</summary>
    public BrainEvidenceReview? Review { get; set; }

    /// <summary>Human review comments, at most 10.</summary>
    public List<BrainEvidenceComment> HumanComments { get; } = [];

    /// <summary>True after the ready finalize; set by BrainEvidenceRecorder.MarkPostPr.</summary>
    public bool PostPr { get; set; }

    /// <summary>Transient state pending the next RecordReport call.</summary>
    internal BrainEvidencePending Pending { get; } = new();
}

/// <summary>Transient buffer consumed by the next RecordReport call.</summary>
internal sealed class BrainEvidencePending
{
    public string? CommitSha { get; set; }
    public List<BrainEvidenceCiJob> CiJobs { get; } = [];
    public List<string> CiFailedTests { get; } = [];

    /// <summary>code | infrastructure | rate_limited | unknown | ci_status_error | timeout</summary>
    public string? Classification { get; set; }
}

/// <summary>A single failed gate attempt.</summary>
public sealed record BrainEvidenceAttempt(
    int Number,
    string Gate,                                    // Compilation | Tests | External CI | Post-PR CI
    IReadOnlyList<string> FailedTests,             // ≤ 20
    IReadOnlyList<BrainEvidenceCiJob> CiJobs,      // failed jobs only
    string Classification,                          // code | infrastructure | ci_status_error | timeout | rate_limited | unknown
    bool? NoCodeChange,                             // null = no next attempt yet
    IReadOnlyList<string> ChangedPaths,            // ≤ 20, from the next fix
    string? CommitSha);                            // non-null for attempts that reached External CI

/// <summary>A failed CI job within an attempt, with extracted evidence lines.</summary>
public sealed record BrainEvidenceCiJob(
    string Name,
    IReadOnlyList<string> FailedSteps,
    IReadOnlyList<string> Lines);                  // ≤ 8 kept lines

/// <summary>Summary of code-review findings for a run.</summary>
public sealed record BrainEvidenceReview(
    IReadOnlyDictionary<string, (int Critical, int Warning)> ByReviewer,
    IReadOnlyList<string> TopFindings);            // ≤ 5, formatted "[SEVERITY] file:line — msg"

/// <summary>A single human review comment.</summary>
public sealed record BrainEvidenceComment(
    string Author,
    string? FilePath,
    int? Line,
    DateTimeOffset CreatedAt,
    string Excerpt);                               // first 300 chars, line breaks → spaces

/// <summary>
/// Agent-only snapshot of the brain skills injected into this run.
/// Never leaves the pod (spec 054, DD4).
/// </summary>
public sealed record BrainSkillsInjection(
    string Text,
    string Result,
    int Bytes,
    IReadOnlyList<string> FilesInjected,
    string? Commit);
