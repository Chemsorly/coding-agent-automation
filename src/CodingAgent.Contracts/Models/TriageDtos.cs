namespace CodingAgent.Pipeline.Models;

/// <summary>
/// The facts the triage list needs about one triage, kept next to the full record so a list never loads
/// whole records.
/// </summary>
public sealed record TriageListFacts
{
    public TriageVerdict? Verdict { get; init; }
    public TriageConfidence? Confidence { get; init; }
    public int DraftCount { get; init; }
    public int CreatedIssueCount { get; init; }
    public int AttemptCount { get; init; }
    public bool LastAttemptHasResult { get; init; }
    public TriageAttemptOutcome? LastAttemptOutcome { get; init; }
    public string? LastWorkItemId { get; init; }
    public string? IssueUrl { get; init; }
    public string? RequestedBy { get; init; }

    public static TriageListFacts From(TriageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var last = record.Attempts.Count > 0 ? record.Attempts[^1] : null;
        var latest = record.LatestResult;
        return new TriageListFacts
        {
            Verdict = latest?.Verdict,
            Confidence = latest?.Confidence,
            DraftCount = record.Drafts.Count,
            CreatedIssueCount = record.CreatedIssues.Count,
            AttemptCount = record.Attempts.Count,
            LastAttemptHasResult = last?.Result is not null,
            LastAttemptOutcome = last?.Outcome,
            LastWorkItemId = last?.WorkItemId,
            IssueUrl = record.IssueUrl,
            RequestedBy = record.RequestedBy
        };
    }
}

/// <summary>One row of the triage list.</summary>
public sealed record TriageListItem
{
    public required Guid Id { get; init; }
    public required string ProjectId { get; init; }
    public required TriageSource Source { get; init; }
    public required string Title { get; init; }
    public string? IssueProviderConfigId { get; init; }
    public string? IssueIdentifier { get; init; }
    public required TriageStatus Status { get; init; }
    public required TriageListFacts Facts { get; init; }

    /// <summary>Tokens of the latest attempt's run, when its run record still exists.</summary>
    public long? LatestTokens { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>Filters of the triage list.</summary>
public sealed record TriageListQuery
{
    /// <summary>One project, or null for all projects (global users only).</summary>
    public string? ProjectId { get; init; }

    public TriageListTab Tab { get; init; } = TriageListTab.All;
    public TriageSource? Source { get; init; }

    /// <summary>Case-insensitive text the title must contain.</summary>
    public string? Search { get; init; }

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 25;
}

/// <summary>
/// An attempt's result as the agent hub reports it: the triage's key comes from the WorkItem record, never
/// from the agent.
/// </summary>
public sealed record TriageResultReport
{
    /// <summary>The attempt's WorkItem (and run) id.</summary>
    public required string WorkItemId { get; init; }

    public required string ProjectId { get; init; }
    public required TriageSource Source { get; init; }

    /// <summary>The issue's tracker, or the operator-triage sentinel.</summary>
    public required string KeyProviderConfigId { get; init; }

    /// <summary>The issue's identifier, or <c>triage:{id}</c>.</summary>
    public required string KeyIdentifier { get; init; }

    /// <summary>The issue's title and URL, for a tracker issue's first result.</summary>
    public string? IssueTitle { get; init; }
    public string? IssueUrl { get; init; }

    public required DateTimeOffset StartedAt { get; init; }
    public required TriageResult Result { get; init; }
}

/// <summary>A page of the triage list.</summary>
public sealed record TriageListPage
{
    public IReadOnlyList<TriageListItem> Items { get; init; } = [];
    public int Total { get; init; }
}

/// <summary>A triage with what the page needs besides the record.</summary>
public sealed record TriageDetail
{
    public required TriageRecord Record { get; init; }
    public required TriageStatus Status { get; init; }

    /// <summary>The running attempt's WorkItem (and run) id, or null.</summary>
    public string? ActiveWorkItemId { get; init; }

    /// <summary>Run facts per attempt WorkItem id, for the attempts whose run record still exists.</summary>
    public IReadOnlyDictionary<string, TriageAttemptRunInfo> AttemptRuns { get; init; } =
        new Dictionary<string, TriageAttemptRunInfo>();
}

/// <summary>What the run record says about one attempt.</summary>
public sealed record TriageAttemptRunInfo
{
    public long? TotalTokens { get; init; }
    public PipelineStep? FinalStep { get; init; }
    public string? FailureReason { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>Body of <c>POST /api/triages</c>.</summary>
public sealed record CreateTriageRequest
{
    public required string ProjectId { get; init; }
    public required TriageRequest Request { get; init; }
    public required string RequestedBy { get; init; }
}

/// <summary>Body of <c>POST /api/triages/{id}/rerun</c>.</summary>
public sealed record RerunTriageRequest
{
    public string? Feedback { get; init; }
    public IReadOnlyList<TriageAnswer> Answers { get; init; } = [];
    public required string Author { get; init; }
}

/// <summary>Body of <c>PUT /api/triages/{id}/drafts/{draftId}</c>.</summary>
public sealed record UpdateTriageDraftRequest
{
    public required TriageDraftKind Kind { get; init; }
    public required string TargetRepository { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    public required string EditedBy { get; init; }
}

/// <summary>Body of <c>POST /api/triages/{id}/create-issues</c>.</summary>
public sealed record CreateTriageIssuesRequest
{
    public required IReadOnlyList<string> DraftIds { get; init; }

    /// <summary>Also label the issues <c>agent:next</c>, so the loop implements them.</summary>
    public bool Queue { get; init; }

    public required string CreatedBy { get; init; }
}

/// <summary>Response of <c>POST /api/triages/{id}/create-issues</c>.</summary>
public sealed record CreateTriageIssuesResult
{
    public IReadOnlyList<TriageCreatedIssue> Created { get; init; } = [];
    public IReadOnlyList<TriageDraftError> Errors { get; init; } = [];
}

/// <summary>A draft that could not be created, and why.</summary>
public sealed record TriageDraftError
{
    public required string DraftId { get; init; }
    public required string Message { get; init; }
}

/// <summary>Body of <c>POST /api/triages/{id}/dismiss</c>.</summary>
public sealed record DismissTriageRequest
{
    public string? Reason { get; init; }
    public required string By { get; init; }
}
