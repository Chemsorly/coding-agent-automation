using System.Text.Json.Serialization;

namespace CodingAgent.Pipeline.Models;

/// <summary>What a triage attempt concluded.</summary>
public enum TriageVerdict
{
    /// <summary>The agent found the root cause and proposes drafts.</summary>
    [JsonStringEnumMemberName("cause_found")]
    CauseFound,

    /// <summary>The agent could not tie the problem to one cause and asks questions.</summary>
    [JsonStringEnumMemberName("inconclusive")]
    Inconclusive,

    /// <summary>The system works as designed, or the cause is outside the project (configuration, user error).</summary>
    [JsonStringEnumMemberName("not_a_bug")]
    NotABug,

    /// <summary>An existing issue or triage already covers the problem.</summary>
    [JsonStringEnumMemberName("duplicate")]
    Duplicate
}

/// <summary>How sure the agent is of its root cause.</summary>
public enum TriageConfidence
{
    [JsonStringEnumMemberName("high")]
    High,

    [JsonStringEnumMemberName("medium")]
    Medium,

    [JsonStringEnumMemberName("low")]
    Low
}

/// <summary>What a proposed fix issue does about the root cause.</summary>
public enum TriageDraftKind
{
    /// <summary>Removes the root cause.</summary>
    [JsonStringEnumMemberName("root_fix")]
    RootFix,

    /// <summary>Limits the damage until the root fix ships.</summary>
    [JsonStringEnumMemberName("mitigation")]
    Mitigation,

    /// <summary>Catches the problem earlier next time (a test, a check, an alert).</summary>
    [JsonStringEnumMemberName("prevention")]
    Prevention
}

/// <summary>Where an explanation the agent considered stands.</summary>
public enum TriageHypothesisState
{
    [JsonStringEnumMemberName("confirmed")]
    Confirmed,

    [JsonStringEnumMemberName("ruled_out")]
    RuledOut,

    [JsonStringEnumMemberName("open")]
    Open
}

/// <summary>The role of one link in the chain from the symptom to the root cause.</summary>
public enum TriageChainKind
{
    [JsonStringEnumMemberName("symptom")]
    Symptom,

    [JsonStringEnumMemberName("step")]
    Step,

    [JsonStringEnumMemberName("trigger")]
    Trigger,

    [JsonStringEnumMemberName("root_cause")]
    RootCause
}

/// <summary>
/// What one triage attempt produced. The agent writes it to
/// <see cref="AgentWorkspacePaths.TriageResultFilePath"/>; the pipeline renders the tracker comment and the
/// web page from it.
/// </summary>
public sealed record TriageResult
{
    public required TriageVerdict Verdict { get; init; }
    public TriageConfidence? Confidence { get; init; }

    /// <summary>The root cause (or the state of the investigation) in a few sentences.</summary>
    public required string Summary { get; init; }

    /// <summary>Who or what is affected, and how much.</summary>
    public string? Impact { get; init; }

    public bool Reproduced { get; init; }

    /// <summary>How the problem was reproduced, for example the failing test.</summary>
    public string? Reproduction { get; init; }

    /// <summary>For <see cref="TriageVerdict.Duplicate"/>: the issue or triage that already covers it.</summary>
    public string? DuplicateOf { get; init; }

    public IReadOnlyList<TriageChainStep> CausalChain { get; init; } = [];
    public IReadOnlyList<TriageHypothesis> Hypotheses { get; init; } = [];
    public IReadOnlyList<TriageEvidence> Evidence { get; init; } = [];

    /// <summary>Every check the agent made, in order, so a person can judge whether it looked in the right places.</summary>
    public IReadOnlyList<TriageCheck> Investigated { get; init; } = [];

    public IReadOnlyList<TriageGap> NotChecked { get; init; } = [];

    /// <summary>For <see cref="TriageVerdict.Inconclusive"/>: what the agent needs from a person.</summary>
    public IReadOnlyList<TriageQuestion> Questions { get; init; } = [];

    public IReadOnlyList<TriageDraft> Drafts { get; init; } = [];
}

/// <summary>One link of the causal chain, with the evidence that supports it.</summary>
public sealed record TriageChainStep
{
    public required string Text { get; init; }
    public TriageChainKind Kind { get; init; } = TriageChainKind.Step;
    public IReadOnlyList<string> EvidenceIds { get; init; } = [];
}

/// <summary>An explanation the agent considered.</summary>
public sealed record TriageHypothesis
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public TriageHypothesisState State { get; init; } = TriageHypothesisState.Open;
}

/// <summary>A fact and where it came from.</summary>
public sealed record TriageEvidence
{
    public required string Id { get; init; }
    public required string Claim { get; init; }

    /// <summary>The tool or place, for example "grafana · Loki" or "code · checkout-api".</summary>
    public required string Source { get; init; }

    /// <summary>The query, command or file:line that produced it.</summary>
    public string? Query { get; init; }

    public string? Link { get; init; }
}

/// <summary>One check the agent made.</summary>
public sealed record TriageCheck
{
    /// <summary>What was checked.</summary>
    public required string Check { get; init; }

    /// <summary>The tool and target, for example "grafana · Prometheus" or "code · payments-worker".</summary>
    public required string Where { get; init; }

    /// <summary>Which hypothesis the check tested (an id such as "H1"), or what it was for ("duplicates", "symptom").</summary>
    public string? For { get; init; }

    public required string Result { get; init; }
    public IReadOnlyList<string> EvidenceIds { get; init; } = [];
}

/// <summary>Something the agent could not check, and why.</summary>
public sealed record TriageGap
{
    public required string What { get; init; }
    public required string Why { get; init; }
}

/// <summary>A question the agent needs a person to answer.</summary>
public sealed record TriageQuestion
{
    public required string Question { get; init; }
    public string? Why { get; init; }
}

/// <summary>A proposed fix issue, complete enough to be created as it is.</summary>
public sealed record TriageDraft
{
    /// <summary>Stable id within the attempt ("d1", "d2", …).</summary>
    public required string Id { get; init; }

    public TriageDraftKind Kind { get; init; } = TriageDraftKind.RootFix;

    /// <summary>The repository (template name) whose tracker gets the issue.</summary>
    public required string TargetRepository { get; init; }

    public required string Title { get; init; }
    public required string Body { get; init; }

    /// <summary>The agent's size estimate, for example "S · 2 files".</summary>
    public string? Size { get; init; }

    /// <summary>Set by the validator when it changed the draft (for example an unknown repository).</summary>
    public string? Warning { get; init; }
}
