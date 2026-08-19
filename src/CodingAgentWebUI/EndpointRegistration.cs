using CodingAgentWebUI.Components;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for configuring middleware pipeline and mapping application endpoints.
/// </summary>
internal static class EndpointRegistration
{
    /// <summary>
    /// Maps all application endpoints: health probes, API routes, static files, auth middleware,
    /// SignalR hub, work item endpoints (DB mode), and Razor components.
    /// </summary>
    public static WebApplication MapApplicationEndpoints(this WebApplication app)
    {
        // Kubernetes-style health probes — anonymous, no auth required
        app.MapHealthEndpoints();

        // Redirect root "/" to the main page (relative redirect — works behind any reverse proxy)
        app.MapGet("/", () => Results.Redirect("agent-coding"))
            .AllowAnonymous();

        // Export run history as JSON download
        // TODO: Accept CancellationToken parameter and pass to GetRunHistoryAsync(ct) so the DB query cancels on client disconnect
        app.MapGet("/api/export/runs.json", async (IPipelineRunHistoryService history, bool? feedbackOnly, int? page, int? pageSize) =>
        {
            IEnumerable<PipelineRunSummary> runs;
            var filterFeedback = feedbackOnly == true;
            if (page.HasValue || pageSize.HasValue)
            {
                var p = page ?? 1;
                var ps = pageSize ?? 50;
                // Apply feedbackOnly filter DB-side before paging so the filter
                // does not silently drop rows from paginated responses.
                var pagedResult = filterFeedback
                    ? await history.GetRunHistoryAsync(p, ps, feedbackOnly: true)
                    : await history.GetRunHistoryAsync(p, ps);
                runs = pagedResult.Items;
            }
            else
            {
                // Use DB-side filter for feedbackOnly even without explicit pagination
                // so older feedback entries are not silently missed by an API response cap.
                if (filterFeedback)
                {
                    var pagedResult = await history.GetRunHistoryAsync(1, 10_000, feedbackOnly: true);
                    runs = pagedResult.Items;
                }
                else
                {
                    runs = await history.GetRunHistoryAsync();
                }
            }

            var json = System.Text.Json.JsonSerializer.Serialize(runs.ToList(), PipelineJsonOptions.Default);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            var fileName = $"pipeline-runs-{DateTime.UtcNow:yyyy-MM-dd}.json";
            return Results.File(bytes, "application/json", fileName);
        }).AllowAnonymous();

        app.UseStaticFiles();
        app.MapStaticAssets();

        app.UseAuthentication();
        app.UseAuthorization();

        // SignalR hub endpoint removed (Spec 044 Task 8 / 15a.2): agents connect to CodingAgentWebUI.Api hub.
        // CodingAgentWebUI.Hub project reference is RETAINED: AgentChat.razor injects
        // IHubContext<AgentHub, IAgentHubClient> to send AssignChatPrompt to running agent pods,
        // and RegisterJobDispatching wires SignalRAgentCommunication against the same context.
        // Both require the Hub library type reference and AddSignalRServices() to compile and resolve.
        // Note: the monolith IHubContext cannot reach agents connected to the API hub — it only
        // sends to connections this process hosts. AgentChat.razor's send path will be updated in Spec 045.

        // Config import/export endpoints removed (Spec 045 Task 9 / Req 2.5):
        // Both endpoints are now served by CodingAgentWebUI.Api (/api/config/export, /api/config/import).

        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode()
            .DisableAntiforgery();

        return app;
    }
}
