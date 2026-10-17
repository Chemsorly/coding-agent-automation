using System.Net.Http.Json;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Ui;

/// <summary>
/// The Consolidation page (spec 051 2.4b): its template cards and harness suggestions, the settings a
/// refactoring scan runs with, and the trigger. Reading needs global read and triggering global operator.
/// The page lists runs with <see cref="IUiRunClient"/> and cancels with <see cref="IUiWorkItemClient"/>.
/// </summary>
public interface IUiConsolidationClient
{
    Task<UiConsolidationOverview> GetOverviewAsync(CancellationToken ct = default);

    /// <summary>Automatic-start counters: current count, threshold, and optional blocked reason per template / harness.</summary>
    Task<UiConsolidationAutoCounters> GetAutoCountersAsync(CancellationToken ct = default);

    Task<UiRefactoringScanSettings> GetRefactoringScanSettingsAsync(string templateId, CancellationToken ct = default);

    /// <summary>
    /// Queues a run and returns the message to show. A refused trigger (already running, a configuration
    /// error, or an unknown template) throws <see cref="InvalidOperationException"/> with the reason.
    /// </summary>
    Task<string> TriggerAsync(ConsolidationRunType type, string? templateId, bool autoDispatch, CancellationToken ct = default, RefactoringScanFocus focus = RefactoringScanFocus.All);
}

public sealed class UiConsolidationClient(HttpClient http, IUiAccessTokenSource tokens) : UiApiClient(http, tokens), IUiConsolidationClient
{
    private const string Consolidation = "/api/ui/consolidation";

    public Task<UiConsolidationOverview> GetOverviewAsync(CancellationToken ct = default) =>
        GetAsync(Consolidation, UiJsonContext.Default.UiConsolidationOverview, ct);

    public Task<UiConsolidationAutoCounters> GetAutoCountersAsync(CancellationToken ct = default) =>
        GetAsync($"{Consolidation}/auto-counters", UiJsonContext.Default.UiConsolidationAutoCounters, ct);

    public Task<UiRefactoringScanSettings> GetRefactoringScanSettingsAsync(string templateId, CancellationToken ct = default) =>
        GetAsync($"{Consolidation}/templates/{Uri.EscapeDataString(templateId)}/refactoring-scan", UiJsonContext.Default.UiRefactoringScanSettings, ct);

    public Task<string> TriggerAsync(ConsolidationRunType type, string? templateId, bool autoDispatch, CancellationToken ct = default, RefactoringScanFocus focus = RefactoringScanFocus.All) =>
        SendForResultAsync(
            HttpMethod.Post,
            $"{Consolidation}/runs",
            JsonContent.Create(new UiConsolidationTriggerRequest(type, templateId, autoDispatch, focus), UiJsonContext.Default.UiConsolidationTriggerRequest),
            UiJsonContext.Default.String,
            ct);
}
