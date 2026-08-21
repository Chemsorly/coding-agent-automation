using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api;

/// <summary>
/// Minimal API endpoints for pipeline run history.
/// All endpoints under /api/pipeline-runs require AgentApiKey except the anonymous export.
/// </summary>
public static class PipelineRunEndpoints
{
    /// <summary>
    /// Maps pipeline run endpoints onto the application endpoint route builder.
    /// </summary>
    public static void MapPipelineRunEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/pipeline-runs")
            .RequireAuthorization(ApiAuthPolicies.Agent);

        // Paginated history with optional feedbackOnly filter (DB-side filter)
        group.MapGet("/", GetRunHistory);

        // Single run by GUID
        group.MapGet("/{runId:guid}", GetRunById);

        // No-op create (reserved, no caller in 042–045)
        group.MapPost("/", () => Results.StatusCode(201));

        // Run history export — requires operator authentication.
        // Returns pipeline run summaries including issue identifiers and project names;
        // these must not be exposed to unauthenticated callers.
        app.MapGet("/api/export/runs.json", ExportRunsJson)
            .RequireAuthorization(ApiAuthPolicies.Operator);
    }

    // ── GET /api/pipeline-runs ─────────────────────────────────────────────

    /// <summary>
    /// GET /api/pipeline-runs?page=1&amp;pageSize=50&amp;feedbackOnly=false&amp;includeActive=false
    /// Returns paginated run history, optionally merged with in-flight active runs from
    /// IOrchestratorRunService. includeActive=true is used by the monitoring page so it can
    /// show dispatched/running jobs that haven't reached a terminal state yet.
    /// </summary>
    internal static async Task<IResult> GetRunHistory(
        IPipelineRunHistoryService history,
        IOrchestratorRunService runService,
        int page = 1,
        int pageSize = 50,
        bool feedbackOnly = false,
        bool includeActive = false,
        CancellationToken ct = default)
    {
        var result = await history.GetRunHistoryAsync(page, pageSize, feedbackOnly, ct);

        if (!includeActive || feedbackOnly)
            return TypedResults.Ok(result);

        // Merge in-flight runs from IOrchestratorRunService that are not yet in history.
        // These are runs that CreateWorkItem materialised in memory but have not yet reached
        // a terminal step (Completed/Failed/Cancelled) — so they are absent from history.
        var activeRunIds = result.Items
            .Where(r => !s_terminalSteps.Contains(r.FinalStep))
            .Select(r => r.RunId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var inFlightSummaries = runService.GetActiveRuns()
            .Where(r => !activeRunIds.Contains(r.RunId))   // not already in history page
            .Select(r => r.ToSummary())
            .ToList();

        if (inFlightSummaries.Count == 0)
            return TypedResults.Ok(result);

        // Prepend in-flight runs (newest activity first) to the paged history result.
        var mergedItems = inFlightSummaries
            .Concat(result.Items)
            .ToList()
            .AsReadOnly();

        return TypedResults.Ok(new PagedResult<PipelineRunSummary>
        {
            Items = mergedItems,
            Page = result.Page,
            PageSize = result.PageSize,
            HasMore = result.HasMore
        });
    }

    private static readonly HashSet<PipelineStep> s_terminalSteps =
    [
        PipelineStep.Completed,
        PipelineStep.Failed,
        PipelineStep.Cancelled
    ];

    // ── GET /api/pipeline-runs/{runId} ─────────────────────────────────────

    /// <summary>
    /// GET /api/pipeline-runs/{runId}
    /// Returns a single pipeline run summary by GUID.
    /// 200 or 404.
    /// </summary>
    internal static async Task<IResult> GetRunById(
        Guid runId,
        IPipelineRunHistoryService history,
        CancellationToken ct = default)
    {
        var summary = await history.GetRunAsync(runId, ct);
        if (summary is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(summary);
    }

    // ── GET /api/export/runs.json — anonymous, faithful port of monolith ───

    /// <summary>
    /// GET /api/export/runs.json
    /// Anonymous file download of run history as JSON.
    /// Faithful port of src/CodingAgentWebUI/EndpointRegistration.cs:30.
    /// feedbackOnly filter applied in-memory AFTER paging (matches monolith behaviour).
    /// TODO(Spec 046): reconcile with GET /api/pipeline-runs which filters feedbackOnly DB-side.
    /// </summary>
    internal static async Task<IResult> ExportRunsJson(
        IPipelineRunHistoryService history,
        bool? feedbackOnly,
        int? page,
        int? pageSize)
    {
        IEnumerable<PipelineRunSummary> runs;
        if (page.HasValue || pageSize.HasValue)
        {
            var p = page ?? 1;
            var ps = pageSize ?? 50;
            var pagedResult = await history.GetRunHistoryAsync(p, ps);
            runs = pagedResult.Items;
        }
        else
        {
            runs = await history.GetRunHistoryAsync();
        }

        // feedbackOnly filter is applied IN-MEMORY after paging — faithful port of monolith behaviour.
        // This may drop rows (feedbackOnly=true with a partial page). See design.md divergence note.
        if (feedbackOnly == true)
            runs = runs.Where(r => r.Feedback is not null);

        var json = System.Text.Json.JsonSerializer.Serialize(runs.ToList(), PipelineJsonOptions.Default);
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        var fileName = $"pipeline-runs-{DateTime.UtcNow:yyyy-MM-dd}.json";
        return Results.File(bytes, "application/json", fileName);
    }
}
