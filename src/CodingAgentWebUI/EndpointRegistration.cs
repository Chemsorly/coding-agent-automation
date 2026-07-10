using CodingAgentWebUI.Components;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;

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
    public static WebApplication MapApplicationEndpoints(this WebApplication app, string? dbConnectionString)
    {
        // Kubernetes-style health probes — anonymous, no auth required
        app.MapHealthEndpoints();

        // Redirect root "/" to the main page (relative redirect — works behind any reverse proxy)
        app.MapGet("/", () => Results.Redirect("agent-coding"))
            .AllowAnonymous();

        // Export run history as JSON download
        // TODO: Accept CancellationToken parameter and pass to GetRunHistoryAsync(ct) so the DB query cancels on client disconnect
        app.MapGet("/api/export/runs.json", async (IPipelineRunHistoryService history, bool? feedbackOnly) =>
        {
            var runs = (IEnumerable<PipelineRunSummary>)await history.GetRunHistoryAsync();
            if (feedbackOnly == true)
                runs = runs.Where(r => r.Feedback is not null);

            var json = System.Text.Json.JsonSerializer.Serialize(runs.ToList(), PipelineJsonOptions.Default);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            var fileName = $"pipeline-runs-{DateTime.UtcNow:yyyy-MM-dd}.json";
            return Results.File(bytes, "application/json", fileName);
        }).AllowAnonymous();

        app.UseStaticFiles();
        app.MapStaticAssets();

        app.UseAuthentication();
        app.UseAuthorization();

        // SignalR hub endpoint for agent connections
        app.MapHub<AgentHub>(HubRoutes.Agent).RequireAuthorization("AgentApiKey");

        // Work Item HTTP API — agents fetch assignments and report status (DB modes only)
        if (!string.IsNullOrEmpty(dbConnectionString))
        {
            app.MapWorkItemEndpoints();
            app.MapConfigImportExportEndpoints();
        }

        app.MapRazorComponents<App>()
            .AddInteractiveServerRenderMode()
            .DisableAntiforgery();

        return app;
    }
}
