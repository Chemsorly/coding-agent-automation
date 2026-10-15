using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Ui;

/// <summary>
/// Spec 051 2.1a: the configuration every role's pages read. Each handler returns a slim DTO, never
/// a stored configuration object, so nothing Settings-only (prompts, secrets, MCP servers) leaves the
/// API. Settings itself (admin) moves in 2.1b.
/// </summary>
internal static class UiConfigEndpoints
{
    public static RouteGroupBuilder MapUiConfigEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/projects", GetProjects);
        group.MapGet("/projects/{projectId}/templates", GetTemplates);
        group.MapGet("/pipeline-settings", GetPipelineSettings);
        group.MapGet("/pipelines", GetPipelinesPage);
        return group;
    }

    /// <summary>GET /api/ui/projects — the projects the user can read, by name (all of them for a global reader).</summary>
    internal static async Task<IResult> GetProjects(IUserAccess access, IProjectStore projects, CancellationToken ct)
    {
        var readable = (await projects.LoadProjectsAsync(ct))
            .Where(p => access.CanRead(p.Id))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(p => new UiProject(p.Id, p.Name, p.Enabled))
            .ToList();
        return TypedResults.Ok(readable);
    }

    /// <summary>GET /api/ui/projects/{projectId}/templates — 404 for a project the user cannot read or that does not exist.</summary>
    internal static async Task<IResult> GetTemplates(string projectId, IUserAccess access, IProjectStore projects, CancellationToken ct)
    {
        access.DemandRead(projectId);
        if (await projects.GetProjectByIdAsync(projectId, ct) is null)
            return TypedResults.NotFound();

        var templates = (await projects.LoadTemplatesForProjectAsync(projectId, ct))
            .Select(t => new UiTemplate(t.Id, t.Name, t.Enabled))
            .ToList();
        return TypedResults.Ok(templates);
    }

    /// <summary>
    /// GET /api/ui/pipelines — the Pipelines page (spec 051 2.7): the readable projects' templates and the
    /// configuration the template table shows. A template without a project is global, so only a global reader
    /// sees it. A project-scoped user gets only the providers their templates use and none of the global
    /// quality gates, reviewers and agent profiles.
    /// </summary>
    internal static async Task<IResult> GetPipelinesPage(
        IUserAccess access, IProjectStore projects, IProviderConfigStore providers, IConfigurationStore config, CancellationToken ct)
    {
        var global = access.CanRead(null);
        var readable = (await projects.LoadProjectsAsync(ct)).Where(p => access.CanRead(p.Id)).ToList();
        var owned = readable.SelectMany(p => p.TemplateIds).ToHashSet(StringComparer.Ordinal);
        var templates = (await projects.LoadAllTemplatesAsync(ct))
            .Where(t => owned.Contains(t.Id) || global)
            .ToList();

        var used = templates
            .SelectMany(t => new[] { t.IssueProviderId, t.RepoProviderId, t.BrainProviderId, t.PipelineProviderId })
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        async Task<IReadOnlyList<ProviderConfig>> ProvidersAsync(ProviderKind kind) =>
            (await providers.LoadProviderConfigsAsync(kind, ct))
                .Where(p => global || used.Contains(p.Id))
                .Select(UiSecretMask.Redact)
                .ToList();

        var settings = await config.LoadPipelineConfigAsync(ct);
        return TypedResults.Ok(new UiPipelinesPage(
            readable.Select(p => new PipelineProject { Id = p.Id, Name = p.Name, Enabled = p.Enabled, TemplateIds = p.TemplateIds }).ToList(),
            templates,
            await ProvidersAsync(ProviderKind.Issue),
            await ProvidersAsync(ProviderKind.Repository),
            await ProvidersAsync(ProviderKind.Pipeline),
            global ? await config.LoadQualityGateConfigsAsync(ct) : [],
            global ? await config.LoadReviewerConfigsAsync(ct) : [],
            global ? (await config.LoadAgentProfilesAsync(ct)).Select(UiSecretMask.Redact).ToList() : [],
            settings.DefaultRequiredAgentLabels,
            settings.MaxRetries));
    }

    /// <summary>GET /api/ui/pipeline-settings — the global settings pages outside Settings show.</summary>
    internal static async Task<IResult> GetPipelineSettings(IPipelineConfigStore config, CancellationToken ct)
    {
        var settings = await config.LoadPipelineConfigAsync(ct);
        return TypedResults.Ok(new UiPipelineSettings(settings.MaxRetries, settings.TriageRetentionDays));
    }
}
