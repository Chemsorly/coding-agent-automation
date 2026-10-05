namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Paging and filter options for a run-history read. Shared by <c>IPipelineRunHistoryService</c>,
/// <c>IPipelineApiRunHistoryClient</c> and the <c>GET /api/pipeline-runs</c> endpoint, which binds it from
/// the query string via <c>[AsParameters]</c> — the property names are the query-string keys
/// (matched case-insensitively) and the constructor defaults apply when a key is absent.
/// </summary>
/// <param name="Page">1-based page number.</param>
/// <param name="PageSize">Number of items per page.</param>
/// <param name="FeedbackOnly">When true, returns only runs with non-null Feedback. Applied DB-side, before paging.</param>
/// <param name="IncludeActive">
/// When true, the API endpoint merges in-flight runs that have not reached history yet. Only the endpoint
/// honours it; <c>IPipelineRunHistoryService</c> implementations read persisted history and ignore it.
/// </param>
/// <param name="FinalStep">Optional outcome filter (e.g. <see cref="PipelineStep.Failed"/>); null returns all outcomes. Applied DB-side so pagination stays correct.</param>
/// <param name="ProjectId">Optional project scope; null returns all projects. Applied DB-side.</param>
/// <param name="Since">Optional start-date filter; when set, only runs with StartedAt &gt;= this value are returned. Applied DB-side. Null means no date filter ("All" window).</param>
/// <param name="RunType">Optional run type filter (e.g. <see cref="PipelineRunType.Consolidation"/>); null returns all types. Applied DB-side so pagination stays correct.</param>
public sealed record RunHistoryQuery(
    int Page = 1,
    int PageSize = 50,
    bool FeedbackOnly = false,
    bool IncludeActive = false,
    PipelineStep? FinalStep = null,
    string? ProjectId = null,
    DateTimeOffset? Since = null,
    PipelineRunType? RunType = null);
