using CodingAgent.Api.Consolidation;
using CodingAgent.Api.Dispatch;
using CodingAgent.Contracts.Ui;

namespace CodingAgent.Api.Ui;

/// <summary>
/// Spec 051 2.4b: the Consolidation page. Consolidation works across projects (a brain can serve
/// several), so reading needs global read and triggering needs global operator, as the page required
/// before. The page lists runs through <c>/api/ui/runs</c> and cancels a queued one through
/// <c>/api/ui/work-items/{id}/cancel</c>.
/// </summary>
internal static class UiConsolidationEndpoints
{
    public static RouteGroupBuilder MapUiConsolidationEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/consolidation", GetOverview).RequireAuthorization(UiPolicies.GlobalRead);
        group.MapGet("/consolidation/auto-counters", GetAutoCounters).RequireAuthorization(UiPolicies.GlobalRead);
        group.MapGet("/consolidation/templates/{templateId}/refactoring-scan", GetRefactoringScanSettings).RequireAuthorization(UiPolicies.GlobalRead);
        group.MapPost("/consolidation/runs", Trigger).RequireAuthorization(UiPolicies.GlobalOperate);
        return group;
    }

    /// <summary>GET /api/ui/consolidation — the template cards and the harness suggestions.</summary>
    internal static async Task<IResult> GetOverview(ConsolidationPageService page, CancellationToken ct) =>
        TypedResults.Ok(await page.GetOverviewAsync(ct));

    /// <summary>GET /api/ui/consolidation/auto-counters — automatic-start counters for each template and for harness suggestions.</summary>
    internal static async Task<IResult> GetAutoCounters(ConsolidationPageService page, CancellationToken ct) =>
        TypedResults.Ok(await page.GetAutoCountersAsync(ct));

    /// <summary>GET /api/ui/consolidation/templates/{templateId}/refactoring-scan — the settings a scan of the template runs with.</summary>
    internal static async Task<IResult> GetRefactoringScanSettings(string templateId, ConsolidationPageService page, CancellationToken ct) =>
        TypedResults.Ok(await page.GetRefactoringScanSettingsAsync(templateId, ct));

    /// <summary>POST /api/ui/consolidation/runs — 200 carries the message to show; a refusal answers 409 with the reason.</summary>
    internal static async Task<IResult> Trigger(UiConsolidationTriggerRequest request, ConsolidationPageService page, CancellationToken ct)
    {
        try
        {
            return TypedResults.Ok(await page.TriggerAsync(request, ct));
        }
        catch (DispatchRefusedException ex)
        {
            return TypedResults.Json(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
