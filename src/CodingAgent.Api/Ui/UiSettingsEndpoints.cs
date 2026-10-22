using CodingAgent.Contracts.Ui;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Mvc;

namespace CodingAgent.Api.Ui;

/// <summary>
/// Spec 051 2.1b: Settings on the UI surface, global admin only, except what a project admin edits on their project
/// page and repository pages (#3577): those handlers check admin on the project and keep the operator's fields
/// (<see cref="ProjectAdminEdits"/>). The handlers are the service surface's
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

        group.MapPut("/pipeline", SavePipelineConfig);

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
        group.MapDelete("/projects/{id}", ConfigEndpoints.DeleteProject);

        group.MapGet("/templates", ConfigEndpoints.GetAllTemplates);
        group.MapPost("/templates/move", ConfigEndpoints.MoveTemplateFlat);
        group.MapDelete("/projects/{projectId}/templates/{templateId}", ConfigEndpoints.DeleteTemplate);

        group.MapGet("/export", ConfigEndpoints.ExportConfigAsync);
        group.MapPost("/import", ConfigEndpoints.ImportConfigAsync).DisableAntiforgery();

        group.MapGet("/first-run", GetFirstRun);
        group.MapPost("/first-run/dismiss", DismissFirstRunBanner);

        group.MapUiProviderEndpoints();

        // #3577: the project page and the Behaviour card. The policy lets in any admin; the handler demands admin on
        // the project, and the global defaults are what the project page shows next to each override.
        var projectAdmin = ui.MapGroup("/settings").RequireAuthorization(UiPolicies.AnyAdmin);
        projectAdmin.MapGet("/pipeline", ConfigEndpoints.GetPipelineConfig);
        projectAdmin.MapGet("/projects/{id}", GetProjectById);
        projectAdmin.MapPut("/projects", SaveProject);
        projectAdmin.MapPut("/projects/{projectId}/templates", SaveTemplate);

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

    internal static async Task<IResult> GetAgentProfiles(IAgentProfileStore store, CancellationToken ct) =>
        TypedResults.Ok((await store.LoadAgentProfilesAsync(ct)).Select(UiSecretMask.Redact).ToList());

    internal static async Task<IResult> SaveAgentProfile([FromBody] AgentProfile profile, IAgentProfileStore store, JobTemplateStore templates, IProviderConfigStore providerStore, CancellationToken ct)
    {
        var stored = (await store.LoadAgentProfilesAsync(ct)).FirstOrDefault(p => p.Id == profile.Id);
        var (restored, error) = UiSecretMask.Restore(profile, stored);
        return restored is null
            ? TypedResults.BadRequest(error)
            : await ConfigEndpoints.SaveAgentProfile(restored, store, templates, providerStore, ct);
    }

    internal static async Task<IResult> GetProjects(IProjectStore store, CancellationToken ct) =>
        TypedResults.Ok((await store.LoadProjectsAsync(ct)).Select(UiSecretMask.Redact).ToList());

    /// <summary>GET /api/ui/settings/projects/{id} — the project, secrets masked, for admin on it (#3577).</summary>
    internal static async Task<IResult> GetProjectById(string id, IUserAccess access, IProjectStore store, CancellationToken ct)
    {
        access.DemandAdminister(id);
        return await store.GetProjectByIdAsync(id, ct) is { } project
            ? TypedResults.Ok(UiSecretMask.Redact(project))
            : TypedResults.NotFound();
    }

    /// <summary>
    /// PUT /api/ui/settings/projects — admin on the project (#3577). A project admin only changes a stored project, and
    /// the operator's fields keep their stored values (<see cref="ProjectAdminEdits"/>).
    /// </summary>
    internal static async Task<IResult> SaveProject([FromBody] PipelineProject project, IUserAccess access, IProjectStore store, CancellationToken ct)
    {
        access.DemandAdminister(project.Id);
        var stored = await store.GetProjectByIdAsync(project.Id, ct);
        if (!access.IsGlobalAdmin)
        {
            if (stored is null)
                return TypedResults.NotFound();
            project = ProjectAdminEdits.Apply(stored, project);
        }

        var (restored, error) = UiSecretMask.Restore(project, stored);
        return restored is null
            ? TypedResults.BadRequest(error)
            : await ConfigEndpoints.SaveProject(restored, store, ct);
    }

    /// <summary>
    /// PUT /api/ui/settings/projects/{projectId}/templates — admin on the project (#3577). A project admin saves the
    /// Behaviour of a template already in that project (<see cref="ProjectAdminEdits"/>); a new template, or another
    /// project's (saving it here would move it here), is 404.
    /// </summary>
    internal static async Task<IResult> SaveTemplate(
        string projectId, [FromBody] PipelineJobTemplate template, IUserAccess access, IProjectStore store, CancellationToken ct)
    {
        access.DemandAdminister(projectId);
        if (!access.IsGlobalAdmin)
        {
            var stored = (await store.LoadTemplatesForProjectAsync(projectId, ct)).FirstOrDefault(t => t.Id == template.Id);
            if (stored is null)
                return TypedResults.NotFound();
            template = ProjectAdminEdits.Apply(stored, template);
        }

        return await ConfigEndpoints.SaveTemplate(projectId, template, store, ct);
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
