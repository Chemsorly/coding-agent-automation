using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Mvc;

namespace CodingAgentWebUI.Api;

/// <summary>
/// Minimal API endpoints for config CRUD operations.
/// All endpoints require the OperatorApiKey authorization policy (Req 6.5b).
/// Secret redaction is applied on all GET endpoints returning ProviderConfig (Req 6.4a).
/// GET /api/config/export and POST /api/config/import are NOT ported — they stay in the
/// monolith (Req 6.3b). Spec 045 decides their final home.
/// </summary>
public static class ConfigEndpoints
{
    /// <summary>
    /// Maps all config endpoints onto the application endpoint route builder.
    /// </summary>
    public static void MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/config")
            .RequireAuthorization("OperatorApiKey");

        // ── Pipeline config ─────────────────────────────────────────────
        group.MapGet("/pipeline", GetPipelineConfig);
        group.MapPut("/pipeline", SavePipelineConfig);

        // ── Provider configs ────────────────────────────────────────────
        group.MapGet("/provider-configs", GetProviderConfigs);
        group.MapGet("/provider-configs/{id}", GetProviderConfigById);
        group.MapPut("/provider-configs", SaveProviderConfig);
        group.MapDelete("/provider-configs/{id}", DeleteProviderConfig);

        // ── Agent profiles ──────────────────────────────────────────────
        group.MapGet("/agent-profiles", GetAgentProfiles);
        group.MapPut("/agent-profiles", SaveAgentProfile);
        group.MapDelete("/agent-profiles/{id}", DeleteAgentProfile);

        // ── Quality gate configs ────────────────────────────────────────
        group.MapGet("/quality-gate-configs", GetQualityGateConfigs);
        group.MapPut("/quality-gate-configs", SaveQualityGateConfig);
        group.MapDelete("/quality-gate-configs/{id}", DeleteQualityGateConfig);

        // ── Reviewer configs ────────────────────────────────────────────
        group.MapGet("/reviewer-configs", GetReviewerConfigs);
        group.MapPut("/reviewer-configs", SaveReviewerConfig);
        group.MapDelete("/reviewer-configs/{id}", DeleteReviewerConfig);
        group.MapPost("/reviewer-configs/reset", ResetReviewerConfigs);

        // ── Projects (requires two store calls — Req 6.3a) ─────────────
        group.MapGet("/projects", GetProjects);
        group.MapGet("/projects/{id}", GetProjectById);
        group.MapPut("/projects", SaveProject);
        group.MapDelete("/projects/{id}", DeleteProject);

        // ── Templates ───────────────────────────────────────────────────
        group.MapGet("/projects/{projectId}/templates", GetTemplatesForProject);
        group.MapPut("/projects/{projectId}/templates", SaveTemplate);
        group.MapDelete("/projects/{projectId}/templates/{templateId}", DeleteTemplate);
        group.MapPost("/projects/{sourceProjectId}/templates/{templateId}/move", MoveTemplate);

        // ── Key-value ───────────────────────────────────────────────────
        group.MapGet("/key-value/{key}", GetKeyValue);
        group.MapPut("/key-value/{key}", SetKeyValue);
        group.MapDelete("/key-value/{key}", DeleteKeyValue);
    }

    // ── Pipeline config ────────────────────────────────────────────────────

    internal static async Task<IResult> GetPipelineConfig(
        IPipelineConfigStore store, CancellationToken ct)
    {
        var config = await store.LoadPipelineConfigAsync(ct);
        return TypedResults.Ok(config);
    }

    internal static async Task<IResult> SavePipelineConfig(
        [FromBody] PipelineConfiguration config,
        IPipelineConfigStore store,
        CancellationToken ct)
    {
        await store.SavePipelineConfigAsync(config, ct);
        return TypedResults.Ok();
    }

    // ── Provider configs ───────────────────────────────────────────────────

    /// <summary>
    /// GET /api/config/provider-configs?kind={kind}
    /// Returns all configs of the given kind with Settings values redacted.
    /// </summary>
    internal static async Task<IResult> GetProviderConfigs(
        ProviderKind kind,
        IProviderConfigStore store,
        CancellationToken ct)
    {
        var configs = await store.LoadProviderConfigsAsync(kind, ct);
        var redacted = configs.Select(RedactProviderConfig).ToList();
        return TypedResults.Ok(redacted);
    }

    /// <summary>
    /// GET /api/config/provider-configs/{id}?kind={kind}
    /// Returns a single config by ID with Settings values redacted.
    /// </summary>
    internal static async Task<IResult> GetProviderConfigById(
        string id,
        ProviderKind kind,
        IProviderConfigStore store,
        CancellationToken ct)
    {
        var config = await store.GetProviderConfigByIdAsync(id, kind, ct);
        if (config is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(RedactProviderConfig(config));
    }

    /// <summary>
    /// PUT /api/config/provider-configs
    /// Accepts the full config with real secret values (write-only credential pattern).
    /// </summary>
    internal static async Task<IResult> SaveProviderConfig(
        [FromBody] ProviderConfig config,
        IProviderConfigStore store,
        CancellationToken ct)
    {
        await store.SaveProviderConfigAsync(config, ct);
        return TypedResults.Ok();
    }

    internal static async Task<IResult> DeleteProviderConfig(
        string id,
        ProviderKind kind,
        IProviderConfigStore store,
        CancellationToken ct)
    {
        await store.DeleteProviderConfigAsync(id, kind, ct);
        return TypedResults.Ok();
    }

    // ── Agent profiles ─────────────────────────────────────────────────────

    internal static async Task<IResult> GetAgentProfiles(
        IAgentProfileStore store, CancellationToken ct)
    {
        var profiles = await store.LoadAgentProfilesAsync(ct);
        return TypedResults.Ok(profiles);
    }

    internal static async Task<IResult> SaveAgentProfile(
        [FromBody] AgentProfile profile,
        IAgentProfileStore store,
        CancellationToken ct)
    {
        await store.SaveAgentProfileAsync(profile, ct);
        return TypedResults.Ok();
    }

    internal static async Task<IResult> DeleteAgentProfile(
        string id,
        IAgentProfileStore store,
        CancellationToken ct)
    {
        await store.DeleteAgentProfileAsync(id, ct);
        return TypedResults.Ok();
    }

    // ── Quality gate configs ───────────────────────────────────────────────

    internal static async Task<IResult> GetQualityGateConfigs(
        IQualityGateConfigStore store, CancellationToken ct)
    {
        var configs = await store.LoadQualityGateConfigsAsync(ct);
        return TypedResults.Ok(configs);
    }

    internal static async Task<IResult> SaveQualityGateConfig(
        [FromBody] QualityGateConfiguration config,
        IQualityGateConfigStore store,
        CancellationToken ct)
    {
        await store.SaveQualityGateConfigAsync(config, ct);
        return TypedResults.Ok();
    }

    internal static async Task<IResult> DeleteQualityGateConfig(
        string id,
        IQualityGateConfigStore store,
        CancellationToken ct)
    {
        await store.DeleteQualityGateConfigAsync(id, ct);
        return TypedResults.Ok();
    }

    // ── Reviewer configs ───────────────────────────────────────────────────

    internal static async Task<IResult> GetReviewerConfigs(
        IReviewerConfigStore store, CancellationToken ct)
    {
        var configs = await store.LoadReviewerConfigsAsync(ct);
        return TypedResults.Ok(configs);
    }

    internal static async Task<IResult> SaveReviewerConfig(
        [FromBody] ReviewerConfiguration config,
        IReviewerConfigStore store,
        CancellationToken ct)
    {
        await store.SaveReviewerConfigAsync(config, ct);
        return TypedResults.Ok();
    }

    internal static async Task<IResult> DeleteReviewerConfig(
        string id,
        IReviewerConfigStore store,
        CancellationToken ct)
    {
        await store.DeleteReviewerConfigAsync(id, ct);
        return TypedResults.Ok();
    }

    internal static async Task<IResult> ResetReviewerConfigs(
        IReviewerConfigStore store, CancellationToken ct)
    {
        await store.ResetReviewerConfigsToDefaultAsync(ct);
        return TypedResults.Ok();
    }

    // ── Projects ───────────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/config/projects
    /// Returns all projects with their templates joined in (Req 6.3a).
    /// Two store calls: LoadProjectsAsync + LoadAllTemplatesAsync, joined by project.TemplateIds.
    /// Returning projects with empty template lists is a bug, not a simplification.
    /// </summary>
    internal static async Task<IResult> GetProjects(
        IProjectStore store, CancellationToken ct)
    {
        var projects = await store.LoadProjectsAsync(ct);
        var allTemplates = await store.LoadAllTemplatesAsync(ct);

        // Index templates by their ID for O(1) lookup when joining by project.TemplateIds
        var templatesById = allTemplates.ToDictionary(t => t.Id, t => t);

        var result = projects.Select(p => new ProjectWithTemplates
        {
            Project = p,
            Templates = p.TemplateIds
                .Select(id => templatesById.TryGetValue(id, out var t) ? t : null)
                .Where(t => t is not null)
                .Select(t => t!)
                .ToList()
        }).ToList();

        return TypedResults.Ok(result);
    }

    /// <summary>
    /// GET /api/config/projects/{id}
    /// Returns a single project with its templates.
    /// </summary>
    internal static async Task<IResult> GetProjectById(
        string id,
        IProjectStore store,
        CancellationToken ct)
    {
        var project = await store.GetProjectByIdAsync(id, ct);
        if (project is null)
            return TypedResults.NotFound();

        var templates = await store.LoadTemplatesForProjectAsync(id, ct);

        return TypedResults.Ok(new ProjectWithTemplates
        {
            Project = project,
            Templates = templates.ToList()
        });
    }

    internal static async Task<IResult> SaveProject(
        [FromBody] PipelineProject project,
        IProjectStore store,
        CancellationToken ct)
    {
        await store.SaveProjectAsync(project, ct);
        return TypedResults.Ok();
    }

    internal static async Task<IResult> DeleteProject(
        string id,
        IProjectStore store,
        CancellationToken ct)
    {
        await store.DeleteProjectAsync(id, ct);
        return TypedResults.Ok();
    }

    // ── Templates ──────────────────────────────────────────────────────────

    internal static async Task<IResult> GetTemplatesForProject(
        string projectId,
        IProjectStore store,
        CancellationToken ct)
    {
        var templates = await store.LoadTemplatesForProjectAsync(projectId, ct);
        return TypedResults.Ok(templates);
    }

    internal static async Task<IResult> SaveTemplate(
        string projectId,
        [FromBody] PipelineJobTemplate template,
        IProjectStore store,
        CancellationToken ct)
    {
        await store.SaveTemplateAsync(projectId, template, ct);
        return TypedResults.Ok();
    }

    internal static async Task<IResult> DeleteTemplate(
        string projectId,
        string templateId,
        IProjectStore store,
        CancellationToken ct)
    {
        await store.DeleteTemplateAsync(projectId, new TemplateId(templateId), ct);
        return TypedResults.Ok();
    }

    internal static async Task<IResult> MoveTemplate(
        string sourceProjectId,
        string templateId,
        string targetProjectId,
        IProjectStore store,
        CancellationToken ct)
    {
        await store.MoveTemplateAsync(sourceProjectId, targetProjectId, new TemplateId(templateId), ct);
        return TypedResults.Ok();
    }

    // ── Key-value ──────────────────────────────────────────────────────────

    internal static async Task<IResult> GetKeyValue(
        string key,
        IKeyValueStore store,
        CancellationToken ct)
    {
        var value = await store.GetAsync(key, ct);
        if (value is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(new { key, value });
    }

    internal static async Task<IResult> SetKeyValue(
        string key,
        [FromBody] KeyValueSetRequest request,
        IKeyValueStore store,
        CancellationToken ct)
    {
        await store.SetAsync(key, request.Value, ct);
        return TypedResults.Ok();
    }

    internal static async Task<IResult> DeleteKeyValue(
        string key,
        IKeyValueStore store,
        CancellationToken ct)
    {
        await store.DeleteAsync(key, ct);
        return TypedResults.Ok();
    }

    // ── Secret redaction helper (Req 6.4a) ────────────────────────────────

    /// <summary>
    /// Returns a copy of the ProviderConfig with all Settings values replaced by "****".
    /// Key names are preserved; values are always masked.
    /// PUT accepts real values; GET never returns them.
    /// </summary>
    private static ProviderConfig RedactProviderConfig(ProviderConfig config)
    {
        var redactedSettings = config.Settings is { Count: > 0 }
            ? config.Settings.ToDictionary(kv => kv.Key, _ => "****")
            : config.Settings;

        var redactedSecrets = config.Secrets is { Count: > 0 }
            ? config.Secrets.ToDictionary(kv => kv.Key, _ => "****")
            : config.Secrets;

        // ProviderConfig is a class (not a record), so manually construct the redacted copy.
        return new ProviderConfig
        {
            BlacklistedPaths = config.BlacklistedPaths,
            DisplayName = config.DisplayName,
            Id = config.Id,
            Kind = config.Kind,
            ProviderType = config.ProviderType,
            RepositoryRole = config.RepositoryRole,
            RequiredLabels = config.RequiredLabels,
            Secrets = redactedSecrets,
            Settings = redactedSettings,
            SetupSteps = config.SetupSteps,
            SteeringContent = config.SteeringContent
        };
    }
}

/// <summary>
/// Response shape for GET /api/config/projects — project plus its templates.
/// Required by Req 6.3a: returning projects without templates is a bug.
/// </summary>
public sealed class ProjectWithTemplates
{
    public required PipelineProject Project { get; init; }
    public required List<PipelineJobTemplate> Templates { get; init; }
}

/// <summary>Request body for PUT /api/config/key-value/{key}.</summary>
public sealed class KeyValueSetRequest
{
    public required string Value { get; init; }
}
