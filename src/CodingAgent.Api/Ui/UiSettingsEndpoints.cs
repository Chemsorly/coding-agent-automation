using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
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
        group.MapPut("/pipeline", ConfigEndpoints.SavePipelineConfig);

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

        group.MapGet("/export", ConfigEndpoints.ExportConfigAsync);
        group.MapPost("/import", ConfigEndpoints.ImportConfigAsync).DisableAntiforgery();

        group.MapGet("/first-run", GetFirstRun);
        group.MapPost("/first-run/dismiss", DismissFirstRunBanner);

        group.MapUiProviderEndpoints();

        return group;
    }

    internal static async Task<IResult> GetAgentProfiles(IAgentProfileStore store, CancellationToken ct) =>
        TypedResults.Ok((await store.LoadAgentProfilesAsync(ct)).Select(UiSecretMask.Redact).ToList());

    internal static async Task<IResult> SaveAgentProfile([FromBody] AgentProfile profile, IAgentProfileStore store, CancellationToken ct)
    {
        var stored = (await store.LoadAgentProfilesAsync(ct)).FirstOrDefault(p => p.Id == profile.Id);
        var (restored, error) = UiSecretMask.Restore(profile, stored);
        return restored is null
            ? TypedResults.BadRequest(error)
            : await ConfigEndpoints.SaveAgentProfile(restored, store, ct);
    }

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
