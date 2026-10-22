using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.AspNetCore.Mvc;

namespace CodingAgent.Api.Ui;

/// <summary>
/// Spec 051 2.1b: Settings on the UI surface, admin only. The handlers are the service surface's
/// (<see cref="ConfigEndpoints"/>), so validation is identical. Projects and agent profiles go through
/// <see cref="UiSecretMask"/> (D4). Provider configs and their operations are in <see cref="UiProviderEndpoints"/>.
/// The config export stays unredacted (spec 051 Q1): it is the admin's backup and restore file.
/// </summary>
internal static class UiSettingsEndpoints
{
    internal const string FirstRunBannerDismissedKey = "first_run_banner_dismissed";

    public static RouteGroupBuilder MapUiSettingsEndpoints(this RouteGroupBuilder ui)
    {
        var group = ui.MapGroup("/settings").RequireAuthorization(UiPolicies.Admin);

        group.MapGet("/pipeline", ConfigEndpoints.GetPipelineConfig);
        group.MapPut("/pipeline", SavePipelineConfig);

        group.MapGet("/agent-routing", GetAgentRouting);
        group.MapPut("/agent-routing", SaveAgentRouting);

        group.MapGet("/agent-profiles", GetAgentProfiles);
        group.MapPut("/agent-profiles", SaveAgentProfile);
        group.MapDelete("/agent-profiles/{id}", ConfigEndpoints.DeleteAgentProfile);

        group.MapGet("/quality-gate-configs", ConfigEndpoints.GetQualityGateConfigs);
        group.MapPut("/quality-gate-configs", ConfigEndpoints.SaveQualityGateConfig);
        group.MapDelete("/quality-gate-configs/{id}", ConfigEndpoints.DeleteQualityGateConfig);

        group.MapGet("/reviewer-configs", ConfigEndpoints.GetReviewerConfigs);
        group.MapPut("/reviewer-configs", ConfigEndpoints.SaveReviewerConfig);
        group.MapDelete("/reviewer-configs/{id}", ConfigEndpoints.DeleteReviewerConfig);
        group.MapPost("/reviewer-configs/reset-to-defaults", ConfigEndpoints.ResetReviewerConfigs);

        group.MapGet("/projects", GetProjects);
        group.MapGet("/projects/{id}", GetProjectById);
        group.MapPut("/projects", SaveProject);
        group.MapDelete("/projects/{id}", ConfigEndpoints.DeleteProject);

        group.MapGet("/templates", ConfigEndpoints.GetAllTemplates);
        group.MapPost("/templates/move", ConfigEndpoints.MoveTemplateFlat);
        group.MapPut("/projects/{projectId}/templates", ConfigEndpoints.SaveTemplate);
        group.MapDelete("/projects/{projectId}/templates/{templateId}", ConfigEndpoints.DeleteTemplate);

        group.MapGet("/export", ConfigEndpoints.ExportConfigAsync);
        group.MapPost("/import", ConfigEndpoints.ImportConfigAsync).DisableAntiforgery();

        group.MapGet("/first-run", GetFirstRun);
        group.MapPost("/first-run/dismiss", DismissFirstRunBanner);

        group.MapUiProviderEndpoints();

        return group;
    }

    /// <summary>
    /// PUT /api/ui/settings/pipeline — saves the settings but keeps the stored loop fields: they are
    /// loop commands (spec 051 D11), and a settings page loaded before a Start or Stop would undo it.
    /// </summary>
    internal static async Task<IResult> SavePipelineConfig([FromBody] PipelineConfiguration config, IPipelineConfigStore store, CancellationToken ct)
    {
        var errors = PipelineSettingsValidator.Validate(config);
        if (errors.Count > 0)
            return TypedResults.BadRequest(string.Join(" ", errors));

        await store.UpdatePipelineConfigAsync(
            current => config with
            {
                ClosedLoopAutoStart = current.ClosedLoopAutoStart,
                LoopResumeRequestedAt = current.LoopResumeRequestedAt,
            },
            ct);
        return TypedResults.Ok();
    }

    /// <summary>GET /api/ui/settings/agent-routing — the global routing table, the profiles a route can name and their job templates.</summary>
    internal static async Task<IResult> GetAgentRouting(
        AgentRoutingStore routing, IAgentProfileStore profiles, JobTemplateStore templates, CancellationToken ct)
    {
        var toolsByKey = templates.ToolsByKey();
        var routingProfiles = (await profiles.LoadAgentProfilesAsync(ct))
            .OrderBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(p => new UiRoutingProfile(p.Id, p.DisplayName, ToolOf(p, toolsByKey), p.JobTemplateKey(), p.Enabled))
            .ToList();
        return TypedResults.Ok(new UiAgentRoutingPage(
            await routing.LoadAsync(ct) ?? AgentRouting.Empty, routingProfiles, JobTemplates(toolsByKey)));
    }

    /// <summary>
    /// PUT /api/ui/settings/agent-routing — replaces the global routing table (spec 053 req 2.6): every row is checked,
    /// and an invalid table is refused with one line per invalid row.
    /// </summary>
    internal static async Task<IResult> SaveAgentRouting(
        [FromBody] AgentRouting? routing, AgentRoutingStore store, IAgentProfileStore profiles, JobTemplateStore templates,
        CancellationToken ct)
    {
        var errors = AgentRouteValidator.ValidateRouting(routing, await profiles.LoadAgentProfilesAsync(ct), templates.ToolsByKey());
        if (errors.Count > 0)
            return TypedResults.BadRequest(string.Join("\n", errors));

        await store.SaveAsync(routing!, ct);
        return TypedResults.Ok();
    }

    /// <summary>GET /api/ui/settings/agent-profiles — the profiles, MCP secrets masked, and the job templates they can run on.</summary>
    internal static async Task<IResult> GetAgentProfiles(IAgentProfileStore store, JobTemplateStore templates, CancellationToken ct) =>
        TypedResults.Ok(new UiAgentProfilesPage(
            (await store.LoadAgentProfilesAsync(ct)).Select(UiSecretMask.Redact).ToList(),
            JobTemplates(templates.ToolsByKey())));

    internal static async Task<IResult> SaveAgentProfile(
        [FromBody] AgentProfile profile, IAgentProfileStore store, JobTemplateStore templates, AgentRoutingStore routing,
        IProjectStore projects, CancellationToken ct)
    {
        var stored = (await store.LoadAgentProfilesAsync(ct)).FirstOrDefault(p => p.Id == profile.Id);
        var (restored, error) = UiSecretMask.Restore(profile, stored);
        return restored is null
            ? TypedResults.BadRequest(error)
            : await ConfigEndpoints.SaveAgentProfile(restored, store, templates, routing, projects, ct);
    }

    private static List<UiJobTemplate> JobTemplates(IReadOnlyDictionary<string, string> toolsByKey) =>
        toolsByKey
            .OrderBy(t => t.Key, StringComparer.Ordinal)
            .Select(t => new UiJobTemplate(t.Key, AgentToolCatalog.Find(t.Value)?.Id ?? ""))
            .ToList();

    private static string ToolOf(AgentProfile profile, IReadOnlyDictionary<string, string> toolsByKey) =>
        toolsByKey.TryGetValue(profile.JobTemplateKey(), out var providerType) ? AgentToolCatalog.Find(providerType)?.Id ?? "" : "";

    internal static async Task<IResult> GetProjects(IProjectStore store, CancellationToken ct) =>
        TypedResults.Ok((await store.LoadProjectsAsync(ct)).Select(UiSecretMask.Redact).ToList());

    internal static async Task<IResult> GetProjectById(string id, IProjectStore store, CancellationToken ct) =>
        await store.GetProjectByIdAsync(id, ct) is { } project
            ? TypedResults.Ok(UiSecretMask.Redact(project))
            : TypedResults.NotFound();

    internal static async Task<IResult> SaveProject([FromBody] PipelineProject project, IProjectStore store, CancellationToken ct)
    {
        var stored = await store.GetProjectByIdAsync(project.Id, ct);
        var (restored, error) = UiSecretMask.Restore(project, stored);
        return restored is null
            ? TypedResults.BadRequest(error)
            : await ConfigEndpoints.SaveProject(restored, store, ct);
    }

    /// <summary>GET /api/ui/settings/first-run — what the first-run banner needs.</summary>
    internal static async Task<IResult> GetFirstRun(IProjectStore projects, IKeyValueStore keyValues, CancellationToken ct) =>
        TypedResults.Ok(new UiFirstRunState(
            await projects.HasEnabledTemplatesAsync(ct),
            await keyValues.GetAsync(FirstRunBannerDismissedKey, ct) == "true"));

    internal static async Task<IResult> DismissFirstRunBanner(IKeyValueStore keyValues, CancellationToken ct)
    {
        await keyValues.SetAsync(FirstRunBannerDismissedKey, "true", ct);
        return TypedResults.Ok();
    }
}
