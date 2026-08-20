using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CodingAgentWebUI.Api;

/// <summary>
/// Minimal API endpoints for config CRUD operations.
/// All endpoints require the OperatorApiKey authorization policy.
/// Secret redaction is applied on all GET endpoints returning ProviderConfig.
/// GET /api/config/export and POST /api/config/import are guarded by OperatorApiKey (Tier 2).
/// </summary>
public static class ConfigEndpoints
{
    private static readonly JsonSerializerOptions ExportOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly JsonSerializerOptions ImportOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };


    /// <summary>
    /// Maps all config endpoints onto the application endpoint route builder.
    /// </summary>
    public static void MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/config")
            .RequireAuthorization(ApiAuthPolicies.Operator);

        // ── Config import/export — OperatorApiKey Tier 2 (agent pods cannot overwrite config)
        group.MapGet("/export", ExportConfigAsync);
        group.MapPost("/import", ImportConfigAsync)
            .DisableAntiforgery();

        // ── Model fetch — passthrough to ModelFetchJobService
        group.MapGet("/models", GetModels);

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
        group.MapPost("/reviewer-configs/reset-to-defaults", ResetReviewerConfigs);

        // ── Projects (requires two store calls — Req 6.3a) ─────────────
        group.MapGet("/projects", GetProjects);
        group.MapGet("/projects/{id}", GetProjectById);
        group.MapPut("/projects", SaveProject);
        group.MapDelete("/projects/{id}", DeleteProject);

        // ── Templates ───────────────────────────────────────────────────
        group.MapGet("/templates", GetAllTemplates);
        group.MapPost("/templates/move", MoveTemplateFlat);
        group.MapGet("/projects/has-enabled-templates", HasEnabledTemplates);
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
    /// GET /api/config/provider-configs?kind={kind}&amp;includeSecrets={bool}
    /// Returns all configs of the given kind. Settings and Secrets values are redacted unless
    /// <paramref name="includeSecrets"/> is explicitly requested.
    ///
    /// The opt-in exists because two very different callers share this endpoint. The Blazor
    /// settings pages render these configs and must never receive live credentials — they use
    /// the redacted default. The monolith's dispatch path reads the same configs through
    /// <c>ApiConfigurationStore</c> and embeds them in the job payload the agent receives;
    /// <c>RunEnvironmentSetupStep</c> writes <c>Secrets</c> straight into the run environment and
    /// the provider resolvers read tokens and base URLs out of <c>Settings</c>. Serving that path
    /// redacted ships every job with "****" in place of its credentials.
    ///
    /// The whole /api/config group already requires the operator key, so this is defence in depth
    /// for the UI rather than a trust boundary — an agent-derived key cannot reach either form.
    /// </summary>
    internal static async Task<IResult> GetProviderConfigs(
        ProviderKind kind,
        IProviderConfigStore store,
        CancellationToken ct,
        bool includeSecrets = false)
    {
        var configs = await store.LoadProviderConfigsAsync(kind, ct);
        if (includeSecrets)
            return TypedResults.Ok(configs.ToList());

        return TypedResults.Ok(configs.Select(RedactProviderConfig).ToList());
    }

    /// <summary>
    /// GET /api/config/provider-configs/{id}?kind={kind}&amp;includeSecrets={bool}
    /// Returns a single config by ID. See <see cref="GetProviderConfigs"/> for why the
    /// unredacted form is opt-in rather than the default.
    /// </summary>
    internal static async Task<IResult> GetProviderConfigById(
        string id,
        ProviderKind kind,
        IProviderConfigStore store,
        CancellationToken ct,
        bool includeSecrets = false)
    {
        var config = await store.GetProviderConfigByIdAsync(id, kind, ct);
        if (config is null)
            return TypedResults.NotFound();

        return TypedResults.Ok(includeSecrets ? config : RedactProviderConfig(config));
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
        return TypedResults.Ok(projects);
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

        return TypedResults.Ok(project);
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

    internal static async Task<IResult> HasEnabledTemplates(
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var hasAny = await db.PipelineJobTemplates.AsNoTracking().AnyAsync(ct);
        return TypedResults.Ok(hasAny);
    }

    internal static async Task<IResult> MoveTemplateFlat(
        [FromBody] MoveTemplateFlatRequest request,
        IProjectStore store,
        CancellationToken ct)
    {
        await store.MoveTemplateAsync(request.SourceProjectId, request.TargetProjectId, new TemplateId(request.TemplateId), ct);
        return TypedResults.Ok();
    }

    internal static async Task<IResult> GetAllTemplates(
        IProjectStore projectStore,
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        // Fetch all project IDs then load templates per project (reuses store deserialization logic)
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var projectIds = await db.Projects.AsNoTracking().Select(p => p.Id).ToListAsync(ct);
        var all = new List<PipelineJobTemplate>();
        foreach (var pid in projectIds)
        {
            var templates = await projectStore.LoadTemplatesForProjectAsync(pid.ToString(), ct);
            all.AddRange(templates);
        }
        return TypedResults.Ok(all);
    }

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

    // ── Model fetch ────────────────────────────────────────────────────────

    /// <summary>
    /// Delegates to <see cref="ModelFetchJobService"/> to dispatch a one-shot K8s Job
    /// that queries available models from the Kiro CLI. Results are returned via the
    /// normal agent hub protocol — no pod log reads required.
    /// Option A passthrough: Settings.razor calls this instead of injecting ModelFetchJobService directly.
    /// </summary>
    internal static async Task<IResult> GetModels(
        ModelFetchJobService fetchService,
        CancellationToken ct)
    {
        var (models, error) = await fetchService.FetchModelsAsync("kiro", ct);
        if (error is not null)
            return TypedResults.Problem(error, statusCode: 502);
        return TypedResults.Ok(models);
    }

    // ── Config import/export ───────────────────────────────────────────────

    /// <summary>
    /// GET /api/config/export
    /// Returns a JSON file download containing all config (providers, profiles, gate configs, etc.).
    /// Requires OperatorApiKey policy (042 Req 6.5, Tier 2) — agent-derived keys receive 403.
    ///
    /// Provider Settings and Secrets are exported UNREDACTED, matching the monolith endpoint this
    /// replaced. Export/import is a backup-and-restore path: <see cref="ImportConfigAsync"/> writes
    /// the bundle verbatim, so a redacted export would restore every credential as a mask string and
    /// silently break every provider. The 042 Req 6.4 redaction applies to the read endpoints
    /// (<c>GET /api/config/providers*</c>), which is where secrets must never surface — see
    /// <see cref="RedactProviderConfig"/>.
    ///
    /// The response therefore contains live credentials. It is guarded by the operator tier and the
    /// UI warns before download; treat the downloaded file as a secret.
    /// </summary>
    internal static async Task<IResult> ExportConfigAsync(
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var bundle = new ConfigBundle
        {
            PipelineConfig = await LoadFirstEntityJson(db.PipelineConfig, e => e.Configuration, ct),
            ProviderConfigs = await db.ProviderConfigs.AsNoTracking().Select(e => new ProviderConfigDto
            {
                Id = e.Id,
                Kind = e.Kind,
                DisplayName = e.DisplayName,
                ProviderType = e.ProviderType,
                Enabled = e.Enabled,
                Configuration = e.Configuration
            }).ToListAsync(ct),
            AgentProfiles = await db.AgentProfiles.AsNoTracking().Select(e => new NamedConfigDto
            {
                Id = e.Id,
                Name = e.Name,
                Configuration = e.Configuration
            }).ToListAsync(ct),
            QualityGateConfigs = await db.QualityGateConfigs.AsNoTracking().Select(e => new NamedConfigDto
            {
                Id = e.Id,
                Name = e.Name,
                Configuration = e.Configuration
            }).ToListAsync(ct),
            ReviewerConfigs = await db.ReviewerConfigs.AsNoTracking().Select(e => new NamedConfigDto
            {
                Id = e.Id,
                Name = e.Name,
                Configuration = e.Configuration
            }).ToListAsync(ct),
            Projects = await db.Projects.AsNoTracking().Select(e => new ProjectDto
            {
                Id = e.Id,
                Name = e.Name,
                Enabled = e.Enabled,
                Description = e.Description,
                Settings = e.Settings,
                TemplateIds = e.TemplateIds
            }).ToListAsync(ct),
            JobTemplates = await db.PipelineJobTemplates.AsNoTracking().Select(e => new JobTemplateDto
            {
                Id = e.Id,
                ProjectId = e.ProjectId,
                Name = e.Name,
                Configuration = e.Configuration
            }).ToListAsync(ct)
        };

        var json = JsonSerializer.Serialize(bundle, ExportOptions);
        return Results.File(
            System.Text.Encoding.UTF8.GetBytes(json),
            "application/json",
            "pipeline-config-export.json");
    }

    /// <summary>
    /// POST /api/config/import
    /// Accepts a JSON file upload (multipart/form-data, field name "file") containing the config
    /// bundle. Clears ALL existing config before inserting — operation is transactional.
    /// Requires OperatorApiKey policy (042 Req 6.5, Tier 2) — agent-derived keys receive 403.
    /// WARNING: This is destructive — run history and work items are preserved, but all config
    /// (providers, profiles, quality gate configs, reviewer configs, projects, templates) is erased.
    /// </summary>
    internal static async Task<IResult> ImportConfigAsync(
        IFormFile file,
        IDbContextFactory<PipelineDbContext> dbFactory,
        IConfigurationStore configStore,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return TypedResults.BadRequest(new ImportExportResult { Success = false, Message = "No file uploaded" });

        ConfigBundle? bundle;
        try
        {
            using var stream = file.OpenReadStream();
            bundle = await JsonSerializer.DeserializeAsync<ConfigBundle>(stream, ImportOptions, ct);
        }
        catch (JsonException ex)
        {
            return TypedResults.BadRequest(new ImportExportResult { Success = false, Message = $"Invalid JSON: {ex.Message}" });
        }

        if (bundle is null)
            return TypedResults.BadRequest(new ImportExportResult { Success = false, Message = "Empty or invalid bundle" });

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Clear existing config (not runs, consolidation data, or work items — those are preserved).
        db.PipelineConfig.RemoveRange(db.PipelineConfig);
        db.ProviderConfigs.RemoveRange(db.ProviderConfigs);
        db.AgentProfiles.RemoveRange(db.AgentProfiles);
        db.QualityGateConfigs.RemoveRange(db.QualityGateConfigs);
        db.ReviewerConfigs.RemoveRange(db.ReviewerConfigs);
        db.Projects.RemoveRange(db.Projects);
        db.PipelineJobTemplates.RemoveRange(db.PipelineJobTemplates);
        await db.SaveChangesAsync(ct);

        if (bundle.PipelineConfig is not null)
        {
            db.PipelineConfig.Add(new PipelineConfigEntity
            {
                Id = Guid.NewGuid(),
                Configuration = bundle.PipelineConfig
            });
        }

        foreach (var p in bundle.ProviderConfigs ?? [])
        {
            db.ProviderConfigs.Add(new ProviderConfigEntity
            {
                Id = p.Id,
                Kind = p.Kind,
                DisplayName = p.DisplayName,
                ProviderType = p.ProviderType,
                Enabled = p.Enabled,
                Configuration = p.Configuration
            });
        }

        foreach (var a in bundle.AgentProfiles ?? [])
        {
            db.AgentProfiles.Add(new AgentProfileEntity
            {
                Id = a.Id,
                Name = a.Name,
                Configuration = a.Configuration
            });
        }

        foreach (var q in bundle.QualityGateConfigs ?? [])
        {
            db.QualityGateConfigs.Add(new QualityGateConfigEntity
            {
                Id = q.Id,
                Name = q.Name,
                Configuration = q.Configuration
            });
        }

        foreach (var r in bundle.ReviewerConfigs ?? [])
        {
            db.ReviewerConfigs.Add(new ReviewerConfigEntity
            {
                Id = r.Id,
                Name = r.Name,
                Configuration = r.Configuration
            });
        }

        foreach (var proj in bundle.Projects ?? [])
        {
            db.Projects.Add(new ProjectEntity
            {
                Id = proj.Id,
                Name = proj.Name,
                Enabled = proj.Enabled,
                Description = proj.Description,
                Settings = proj.Settings,
                TemplateIds = proj.TemplateIds ?? []
            });
        }

        foreach (var t in bundle.JobTemplates ?? [])
        {
            db.PipelineJobTemplates.Add(new PipelineJobTemplateEntity
            {
                Id = t.Id,
                ProjectId = t.ProjectId,
                Name = t.Name,
                Configuration = t.Configuration
            });
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        // Invalidate config store caches after the raw DB write (bypasses the store layer).
        configStore.InvalidateCaches();

        return TypedResults.Ok(new ImportExportResult
        {
            Success = true,
            Message = $"Imported: {bundle.ProviderConfigs?.Count ?? 0} providers, " +
                      $"{bundle.AgentProfiles?.Count ?? 0} profiles, " +
                      $"{bundle.QualityGateConfigs?.Count ?? 0} quality gates, " +
                      $"{bundle.ReviewerConfigs?.Count ?? 0} reviewers, " +
                      $"{bundle.Projects?.Count ?? 0} projects, " +
                      $"{bundle.JobTemplates?.Count ?? 0} templates"
        });
    }

    private static async Task<string?> LoadFirstEntityJson<T>(
        Microsoft.EntityFrameworkCore.DbSet<T> dbSet,
        Func<T, string?> selector,
        CancellationToken ct) where T : class
    {
        var entity = await dbSet.AsNoTracking().FirstOrDefaultAsync(ct);
        return entity is null ? null : selector(entity);
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

// ── DTOs for config import/export bundle ──────────────────────────────────────

/// <summary>
/// The full config bundle — serialized to/from JSON for export/import.
/// Same schema as the monolith's ConfigBundle so existing exports remain importable.
/// </summary>
public sealed class ConfigBundle
{
    public string? PipelineConfig { get; set; }
    public List<ProviderConfigDto>? ProviderConfigs { get; set; }
    public List<NamedConfigDto>? AgentProfiles { get; set; }
    public List<NamedConfigDto>? QualityGateConfigs { get; set; }
    public List<NamedConfigDto>? ReviewerConfigs { get; set; }
    public List<ProjectDto>? Projects { get; set; }
    public List<JobTemplateDto>? JobTemplates { get; set; }
}

/// <summary>Provider config row — Configuration is a serialized ProviderConfig JSON blob.</summary>
public sealed record ProviderConfigDto
{
    public Guid Id { get; init; }
    public ProviderKind Kind { get; init; }
    public string DisplayName { get; init; } = "";
    public string ProviderType { get; init; } = "";
    public bool Enabled { get; init; }
    public string? Configuration { get; init; }
}

/// <summary>Generic named config row (agent profiles, quality gate configs, reviewer configs).</summary>
public sealed class NamedConfigDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Configuration { get; set; }
}

/// <summary>Project row.</summary>
public sealed class ProjectDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string? Description { get; set; }
    public string? Settings { get; set; }
    public List<string>? TemplateIds { get; set; }
}

/// <summary>Pipeline job template row.</summary>
public sealed class JobTemplateDto
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string? Configuration { get; set; }
}

/// <summary>Response body for POST /api/config/import.</summary>
public sealed class ImportExportResult
{
    public bool Success { get; init; }
    public required string Message { get; init; }
}

/// <summary>Request body for POST /api/config/templates/move.</summary>
public sealed class MoveTemplateFlatRequest
{
    public required string SourceProjectId { get; init; }
    public required string TargetProjectId { get; init; }
    public required string TemplateId { get; init; }
}
