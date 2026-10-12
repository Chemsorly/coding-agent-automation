using System.Globalization;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Ui;

/// <summary>
/// Run history for every role (spec 051 2.2a). A list without a project needs global read; a scoped
/// user's pages always pass the selected project. A run the user cannot read is "not found".
/// </summary>
public interface IUiRunClient
{
    Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(RunHistoryQuery query, CancellationToken ct = default);

    /// <summary>The run and its agent provider's name, or null when it does not exist or the user cannot read it.</summary>
    Task<UiRunDetail?> GetRunAsync(Guid runId, CancellationToken ct = default);
}

public sealed class UiRunClient(HttpClient http, IUiAccessTokenSource tokens) : UiApiClient(http, tokens), IUiRunClient
{
    public Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(RunHistoryQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var url = $"/api/ui/runs?page={query.Page}&pageSize={query.PageSize}&feedbackOnly={query.FeedbackOnly}&includeActive={query.IncludeActive}";
        if (query.FinalStep is { } step)
            url += $"&finalStep={step}";
        if (!string.IsNullOrEmpty(query.ProjectId))
            url += $"&projectId={Uri.EscapeDataString(query.ProjectId)}";
        if (query.Since is { } since)
            url += $"&since={Uri.EscapeDataString(since.ToString("O", CultureInfo.InvariantCulture))}";
        if (query.RunType is { } runType)
            url += $"&runType={runType}";
        return GetAsync(url, UiJsonContext.Default.PagedResultPipelineRunSummary, ct);
    }

    public Task<UiRunDetail?> GetRunAsync(Guid runId, CancellationToken ct = default) =>
        FindAsync($"/api/ui/runs/{runId}", UiJsonContext.Default.UiRunDetail, ct);
}

/// <summary>
/// The work-item lists of the Work and Overview pages (spec 051 2.2a), under the same project rule as
/// runs, and the actions on one work item (2.2b), which the API checks against its stored project.
/// A refusal throws <see cref="InvalidOperationException"/> with the API's reason.
/// </summary>
public interface IUiWorkItemClient
{
    Task<IReadOnlyList<PendingWorkItemDto>> GetPendingAsync(int maxResults = 50, string? projectId = null, CancellationToken ct = default);

    Task<IReadOnlyList<ActiveWorkItemDto>> GetActiveAsync(int olderThanSeconds = 0, string? projectId = null, CancellationToken ct = default);

    /// <summary>Cancels a queued item or an in-flight run (a run's ID is its work item's); a no-op once it has ended.</summary>
    Task CancelAsync(Guid workItemId, CancellationToken ct = default);

    /// <summary>Sets the dispatch weight (0–1000) of a queued item.</summary>
    Task SetPriorityAsync(Guid workItemId, int priorityWeight, CancellationToken ct = default);
}

public sealed class UiWorkItemClient(HttpClient http, IUiAccessTokenSource tokens) : UiApiClient(http, tokens), IUiWorkItemClient
{
    public Task<IReadOnlyList<PendingWorkItemDto>> GetPendingAsync(int maxResults = 50, string? projectId = null, CancellationToken ct = default) =>
        GetAsync($"/api/ui/work-items/pending?maxResults={maxResults}{ProjectScope(projectId)}", UiJsonContext.Default.IReadOnlyListPendingWorkItemDto, ct);

    public Task<IReadOnlyList<ActiveWorkItemDto>> GetActiveAsync(int olderThanSeconds = 0, string? projectId = null, CancellationToken ct = default) =>
        GetAsync($"/api/ui/work-items/active?olderThanSeconds={olderThanSeconds}{ProjectScope(projectId)}", UiJsonContext.Default.IReadOnlyListActiveWorkItemDto, ct);

    public Task CancelAsync(Guid workItemId, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, $"/api/ui/work-items/{workItemId}/cancel", ct);

    public Task SetPriorityAsync(Guid workItemId, int priorityWeight, CancellationToken ct = default) =>
        SendJsonAsync(HttpMethod.Put, $"/api/ui/work-items/{workItemId}/priority", new UiPriorityRequest(priorityWeight), UiJsonContext.Default.UiPriorityRequest, ct);

    private static string ProjectScope(string? projectId) =>
        string.IsNullOrEmpty(projectId) ? "" : $"&projectId={Uri.EscapeDataString(projectId)}";
}
