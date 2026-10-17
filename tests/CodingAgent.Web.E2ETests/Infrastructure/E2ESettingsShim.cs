using CodingAgent.Contracts.Ui;
using CodingAgent.Web.E2ETests.Fakes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace CodingAgent.Web.E2ETests.Infrastructure;

/// <summary>
/// Spec 052 V16 (design 6.1): the harness keeps configuration in <see cref="InMemoryPipelineApiConfigClient"/>
/// and its <c>InMemoryConfigurationStore</c>, which the API host's stores are replaced with. Five
/// <c>/api/ui/settings</c> handlers bypass those stores and read the API's database directly, so this
/// shim answers exactly them from the harness's configuration, with the real handlers' JSON shapes:
/// <list type="bullet">
/// <item><c>GET templates</c> (project IDs from <c>db.Projects</c>);</item>
/// <item><c>GET export</c> and <c>POST import</c> (every config table);</item>
/// <item><c>GET first-run</c> and <c>POST first-run/dismiss</c> (the database key-value store).</item>
/// </list>
/// Every other route reaches the real API handlers. The shim runs before authentication, so it only
/// demands a bearer header; the token itself is checked by T2 in <c>Api.IntegrationTests</c>.
/// </summary>
internal sealed class E2ESettingsShim(InMemoryPipelineApiConfigClient config) : IStartupFilter
{
    private const string Settings = "/api/ui/settings";
    private const string FirstRunBannerDismissedKey = "first_run_banner_dismissed";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            if (await AnswerAsync(context) is { } result)
            {
                await result.ExecuteAsync(context);
                return;
            }
            await nextMiddleware();
        });
        next(app);
    };

    private async Task<IResult?> AnswerAsync(HttpContext context)
    {
        var request = context.Request;
        var path = request.Path.Value ?? "";
        if (!request.Path.StartsWithSegments(Settings, StringComparison.OrdinalIgnoreCase))
            return null;

        var route = path[Settings.Length..].ToLowerInvariant();
        var known = (request.Method, route) is ("GET", "/templates") or ("GET", "/export") or ("POST", "/import")
            or ("GET", "/first-run") or ("POST", "/first-run/dismiss");
        if (!known)
            return null;
        if (!request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal))
            return Results.Unauthorized();

        var ct = context.RequestAborted;
        switch (route)
        {
            case "/templates":
                return Results.Ok(await config.GetAllTemplatesAsync(ct));
            case "/export":
                return Results.File(await config.ExportConfigAsync(ct), "application/json", "pipeline-config-export.json");
            case "/import":
                var file = (await request.ReadFormAsync(ct)).Files["file"];
                if (file is null)
                    return Results.BadRequest("No file uploaded.");
                await using (var stream = file.OpenReadStream())
                    await config.ImportConfigAsync(stream, file.FileName, ct);
                return Results.Ok(new { success = true, message = "Imported." });
            case "/first-run":
                return Results.Ok(new UiFirstRunState(
                    await config.HasEnabledTemplatesAsync(ct),
                    await config.GetKeyValueAsync(FirstRunBannerDismissedKey, ct) == "true"));
            default: // "/first-run/dismiss"
                await config.SetKeyValueAsync(FirstRunBannerDismissedKey, "true", ct);
                return Results.Ok();
        }
    }
}
