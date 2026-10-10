using System.Text.Json.Serialization;

namespace CodingAgent.Pipeline.Models;

/// <summary>Where a triage came from.</summary>
public enum TriageSource
{
    /// <summary>Started by an operator in the web UI; not linked to an issue.</summary>
    [JsonStringEnumMemberName("operator")]
    Operator,

    /// <summary>Started by the <c>agent:triage</c> label on a tracker issue.</summary>
    [JsonStringEnumMemberName("issue")]
    Issue
}

/// <summary>
/// The stored state of a triage, set by results and by people. Whether an attempt is running is not
/// stored; <see cref="TriageStatusResolver"/> combines both into the status the UI shows.
/// </summary>
public enum TriageState
{
    /// <summary>No attempt has reported a result yet.</summary>
    [JsonStringEnumMemberName("new")]
    New,

    /// <summary>Cause found: the drafts wait for a person.</summary>
    [JsonStringEnumMemberName("needs_review")]
    NeedsReview,

    /// <summary>Inconclusive: the agent's questions wait for a person.</summary>
    [JsonStringEnumMemberName("needs_input")]
    NeedsInput,

    [JsonStringEnumMemberName("not_a_bug")]
    NotABug,

    [JsonStringEnumMemberName("duplicate")]
    Duplicate,

    /// <summary>A person created issues from the drafts.</summary>
    [JsonStringEnumMemberName("issues_created")]
    IssuesCreated,

    /// <summary>A person dismissed the triage.</summary>
    [JsonStringEnumMemberName("dismissed")]
    Dismissed
}

/// <summary>How an attempt's run ended, when it ended without a result.</summary>
public enum TriageAttemptOutcome
{
    [JsonStringEnumMemberName("completed")]
    Completed,

    [JsonStringEnumMemberName("failed")]
    Failed,

    [JsonStringEnumMemberName("cancelled")]
    Cancelled
}

/// <summary>
/// One problem report and everything done about it. Stored as one row of the <c>Triages</c> table; the
/// row's JSON column holds this record.
/// </summary>
public sealed record TriageRecord
{
    public required Guid Id { get; init; }
    public required string ProjectId { get; init; }
    public required TriageSource Source { get; init; }

    /// <summary>Tracker of the issue (<see cref="TriageSource.Issue"/> only).</summary>
    public string? IssueProviderConfigId { get; init; }

    /// <summary>The issue's identifier (<see cref="TriageSource.Issue"/> only).</summary>
    public string? IssueIdentifier { get; init; }

    public string? IssueUrl { get; init; }

    public required string Title { get; init; }

    /// <summary>The operator's report (<see cref="TriageSource.Operator"/> only).</summary>
    public TriageRequest? Request { get; init; }

    /// <summary>Who started an operator triage.</summary>
    public string? RequestedBy { get; init; }

    /// <summary>The attempts, oldest first.</summary>
    public IReadOnlyList<TriageAttempt> Attempts { get; init; } = [];

    /// <summary>The drafts of the latest result, as people edited them.</summary>
    public IReadOnlyList<TriageEditableDraft> Drafts { get; init; } = [];

    /// <summary>The WorkItem id of the attempt whose result produced <see cref="Drafts"/>.</summary>
    public string? DraftsFromWorkItemId { get; init; }

    public IReadOnlyList<TriageCreatedIssue> CreatedIssues { get; init; } = [];

    public TriageState State { get; init; } = TriageState.New;

    public string? DismissReason { get; init; }
    public string? DismissedBy { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }

    /// <summary>The latest attempt that reported a result, or null.</summary>
    [JsonIgnore]
    public TriageAttempt? LatestResultAttempt => Attempts.LastOrDefault(a => a.Result is not null);

    /// <summary>The latest result, or null.</summary>
    [JsonIgnore]
    public TriageResult? LatestResult => LatestResultAttempt?.Result;
}

/// <summary>An operator's problem report.</summary>
public sealed record TriageRequest
{
    public required string Title { get; init; }
    public required string WhatHappened { get; init; }
    public required string Expected { get; init; }

    /// <summary>When the problem was first seen.</summary>
    public DateTimeOffset? From { get; init; }

    /// <summary>Until when it was seen; null = still happening.</summary>
    public DateTimeOffset? Until { get; init; }

    public string? Environment { get; init; }

    /// <summary>Service, endpoint, page or host.</summary>
    public string? Where { get; init; }

    /// <summary>The template whose repository the agent should look at first, and which runs the job.</summary>
    public string? StartInTemplateId { get; init; }

    /// <summary>The image tag or commit that was running.</summary>
    public string? Version { get; init; }

    /// <summary>Trace or request IDs, dashboards, a failed run, a support ticket.</summary>
    public string? Links { get; init; }

    public string? AlreadyTried { get; init; }
}

/// <summary>One triage run.</summary>
public sealed record TriageAttempt
{
    /// <summary>The run's WorkItem id, which is also its run id.</summary>
    public required string WorkItemId { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>The feedback or answers a person gave before this attempt.</summary>
    public TriageFeedback? Feedback { get; init; }

    /// <summary>The attempt's result; null until the agent reports it.</summary>
    public TriageResult? Result { get; init; }

    /// <summary>
    /// How the run ended when it ended without a result, copied from the WorkItem before it expires.
    /// Null while it runs or when it reported a result.
    /// </summary>
    public TriageAttemptOutcome? Outcome { get; init; }

    public string? FailureReason { get; init; }
}

/// <summary>Feedback or answers given to the agent before a re-run.</summary>
public sealed record TriageFeedback
{
    public string? Text { get; init; }
    public IReadOnlyList<TriageAnswer> Answers { get; init; } = [];
    public string? Author { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>A person's answer to one of the agent's questions.</summary>
public sealed record TriageAnswer
{
    public required string Question { get; init; }
    public required string Answer { get; init; }
}

/// <summary>A draft as the agent proposed it and as a person edited it.</summary>
public sealed record TriageEditableDraft
{
    public required TriageDraft Current { get; init; }
    public required TriageDraft Original { get; init; }
    public string? EditedBy { get; init; }
    public DateTimeOffset? EditedAt { get; init; }

    [JsonIgnore]
    public bool Edited => EditedBy is not null;
}

/// <summary>An issue created from a draft.</summary>
public sealed record TriageCreatedIssue
{
    public required string DraftId { get; init; }

    /// <summary>The repository (template name) the issue was created for.</summary>
    public required string Repository { get; init; }

    public required string IssueProviderConfigId { get; init; }
    public required string Identifier { get; init; }
    public string? Url { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public string? CreatedBy { get; init; }
}
