using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgentWebUI.Services;

/// <summary>
/// API endpoints for config import/export as a single JSON bundle.
/// Export: GET returns a JSON file download containing all config (no runs).
/// Import: POST accepts an uploaded JSON file and upserts into DB.
/// </summary>
public static class ConfigImportExportEndpoints
{
    public static void MapConfigImportExportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/config")
            .RequireAuthorization("AgentApiKey");

        group.MapGet("/export", ExportConfigAsync);
        group.MapPost("/import", ImportConfigAsync)
            .DisableAntiforgery();
    }

    internal static async Task<IResult> ExportConfigAsync(
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var bundle = new ConfigBundle
        {
            PipelineConfig = await LoadEntityJson<PipelineConfigEntity>(db.PipelineConfig, e => e.Configuration, ct),
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

        var json = JsonSerializer.Serialize(bundle, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        return Results.File(
            System.Text.Encoding.UTF8.GetBytes(json),
            "application/json",
            "pipeline-config-export.json");
    }

    /// <summary>
    /// POST /api/config/import
    /// Accepts a JSON file upload containing the config bundle, clears existing config, and imports.
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
            bundle = await JsonSerializer.DeserializeAsync<ConfigBundle>(stream, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            }, ct);
        }
        catch (JsonException ex)
        {
            return TypedResults.BadRequest(new ImportExportResult { Success = false, Message = $"Invalid JSON: {ex.Message}" });
        }

        if (bundle is null)
            return TypedResults.BadRequest(new ImportExportResult { Success = false, Message = "Empty or invalid bundle" });

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Clear existing config (not runs/consolidation/work items)
        db.PipelineConfig.RemoveRange(db.PipelineConfig);
        db.ProviderConfigs.RemoveRange(db.ProviderConfigs);
        db.AgentProfiles.RemoveRange(db.AgentProfiles);
        db.QualityGateConfigs.RemoveRange(db.QualityGateConfigs);
        db.ReviewerConfigs.RemoveRange(db.ReviewerConfigs);
        db.Projects.RemoveRange(db.Projects);
        db.PipelineJobTemplates.RemoveRange(db.PipelineJobTemplates);
        await db.SaveChangesAsync(ct);

        // Import from bundle
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

        // Invalidate config store caches (bypassed by direct DB writes)
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

    private static async Task<string?> LoadEntityJson<T>(
        DbSet<T> dbSet, Func<T, string?> selector, CancellationToken ct) where T : class
    {
        var entity = await dbSet.AsNoTracking().FirstOrDefaultAsync(ct);
        return entity is null ? null : selector(entity);
    }
}

// ── DTOs for the config bundle ──────────────────────────────────────────────

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

public sealed class ProviderConfigDto
{
    public Guid Id { get; set; }
    public ProviderKind Kind { get; set; }
    public string DisplayName { get; set; } = "";
    public string ProviderType { get; set; } = "";
    public bool Enabled { get; set; }
    public string? Configuration { get; set; }
}

public sealed class NamedConfigDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Configuration { get; set; }
}

public sealed class ProjectDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string? Description { get; set; }
    public string? Settings { get; set; }
    public List<string>? TemplateIds { get; set; }
}

public sealed class JobTemplateDto
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = "";
    public string? Configuration { get; set; }
}

public sealed class ImportExportResult
{
    public bool Success { get; init; }
    public required string Message { get; init; }
}
