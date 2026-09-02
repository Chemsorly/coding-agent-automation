using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api;

/// <summary>
/// Minimal API endpoints for pipeline run history.
/// Every endpoint here requires authentication: /api/pipeline-runs and the /api/export/runs.json
/// download both carry issue identifiers and project names, so the export is operator-gated too.
/// </summary>
public static class PipelineRunEndpoints
{
    /// <summary>
    /// Maps pipeline run endpoints onto the application endpoint route builder.
    /// </summary>
    public static void MapPipelineRunEndpoints(this IEndpointRouteBuilder app)
    {
        // All /api/pipeline-runs routes are operator-gated.
        // GET routes carry issue identifiers and project names — must not be exposed to agent-tier keys.
        // POST (CreateRunSummary) is called by the orchestrator (ApiBackedPipelineRunHistoryService),
        // which authenticates with the operator key. See W0-04 remediation.
        var group = app.MapGroup("/api/pipeline-runs")
            .RequireAuthorization(ApiAuthPolicies.Operator);

        // Paginated history with optional feedbackOnly filter (DB-side filter)
        group.MapGet("/", GetRunHistory);

        // Single run by GUID
        group.MapGet("/{runId:guid}", GetRunById);

        // Create: persists a completed run summary. Called by the orchestrator.
        group.MapPost("/", CreateRunSummary);

        // Active branch names — used by SchedulerRunQueryService for the housekeeping guard.
        // NOTE: This endpoint inherits RequireAuthorization(ApiAuthPolicies.Operator) from the group.
        //   The Scheduler's HttpClient (via PipelineApiRunHistoryClient) must authenticate with an operator-tier
        //   key, not an agent-tier key. If misconfigured with an agent key, the request receives 403 and
        //   GetFromJsonAsync silently returns null → [], which is indistinguishable from "no active runs" and
        //   defeats the conservative fallback. Document this requirement in the Scheduler configuration guide
        //   and consider adding a startup check that the Scheduler can reach this endpoint.
        group.MapGet("/active-branches", GetActiveBranches);

        // Run history export — operator-authenticated file download.
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

    // ── POST /api/pipeline-runs/ ────────────────────────────────────────────

    /// <summary>
    /// POST /api/pipeline-runs/
    /// Persists a completed run summary sent by the orchestrator process.
    /// The orchestrator (CodingAgentWebUI) calls this instead of writing directly to Postgres.
    /// Idempotent: an existing run with the same RunId is updated, not duplicated.
    /// 201 Created on success.
    /// </summary>
    internal static async Task<IResult> CreateRunSummary(
        PipelineRunSummary summary,
        IPipelineRunHistoryService history,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(summary);
        await history.AddRunSummaryAsync(summary, ct);
        return Results.StatusCode(201);
    }

    // ── GET /api/pipeline-runs/active-branches ─────────────────────────────

    /// <summary>
    /// GET /api/pipeline-runs/active-branches
    /// Returns the branch names of all currently active (non-terminal) pipeline runs held
    /// in the orchestrator's in-memory run service.
    /// Used by <c>SchedulerRunQueryService</c> to populate <c>GetActiveRunBranchesAsync()</c>
    /// so the housekeeping branch-update guard works correctly in the Scheduler deployment.
    /// Returns an empty array when no runs are active.
    /// </summary>
    internal static IResult GetActiveBranches(IOrchestratorRunService runService)
    {
        var branches = runService.GetActiveRuns()
            .Where(r => r.BranchName != null)
            .Select(r => r.BranchName!)
            .ToList();

        return TypedResults.Ok((IReadOnlyList<string>)branches);
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
    ///
    /// <para>
    /// Falls back to an in-flight run from <see cref="IOrchestratorRunService"/> when the id is not
    /// in history, mirroring the <c>includeActive</c> path on the list endpoint above. Both must
    /// agree: the monitoring page lists active runs from the merged list, and the run-detail modal
    /// then fetches the clicked run through here. Without the fallback, clicking a running job 404s
    /// and the modal — which only renders once it has a non-null summary — never opens.
    /// </para>
    /// </summary>
    internal static async Task<IResult> GetRunById(
        Guid runId,
        IPipelineRunHistoryService history,
        IOrchestratorRunService runService,
        CancellationToken ct = default)
    {
        var summary = await history.GetRunAsync(runId, ct);

        summary ??= runService.GetActiveRuns()
            .FirstOrDefault(r => string.Equals(r.RunId, runId.ToString(), StringComparison.OrdinalIgnoreCase))
            ?.ToSummary();

        if (summary is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(summary);
    }

    // ── GET /api/export/runs.json — operator-authenticated download ───

    /// <summary>
    /// GET /api/export/runs.json
    /// Operator-authenticated file download of run history as JSON. The monolith served this
    /// behind its own login wall; the split API is separately reachable, so the export is gated on
    /// <see cref="ApiAuthPolicies.Operator"/> (see the mapping) rather than left anonymous.
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
