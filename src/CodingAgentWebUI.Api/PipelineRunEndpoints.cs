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
            .RequireAuthorization("AgentApiKey");

        // Paginated history with optional feedbackOnly filter (DB-side filter)
        group.MapGet("/", GetRunHistory);

        // Single run by GUID
        group.MapGet("/{runId:guid}", GetRunById);

        // No-op create (reserved, no caller in 042–045)
        group.MapPost("/", () => Results.StatusCode(201));

        // Anonymous file download — mirrors monolith's EndpointRegistration.cs:30
        // Note: registered outside the group so it is anonymous and at the /api/export path
        app.MapGet("/api/export/runs.json", ExportRunsJson).AllowAnonymous();
    }

    // ── GET /api/pipeline-runs ─────────────────────────────────────────────

    /// <summary>
    /// GET /api/pipeline-runs?page=1&amp;pageSize=50&amp;feedbackOnly=false
    /// Returns paginated run history. feedbackOnly filter is applied at DB level.
    /// </summary>
    internal static async Task<IResult> GetRunHistory(
        IPipelineRunHistoryService history,
        int page = 1,
        int pageSize = 50,
        bool feedbackOnly = false,
        CancellationToken ct = default)
    {
        var result = await history.GetRunHistoryAsync(page, pageSize, feedbackOnly, ct);
        return TypedResults.Ok(result);
    }

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
    /// Note: Spec 045 reconciles the in-memory vs DB-side feedbackOnly divergence.
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
