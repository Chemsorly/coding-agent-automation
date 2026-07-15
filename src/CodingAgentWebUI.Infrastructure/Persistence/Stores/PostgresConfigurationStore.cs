using System.Text.Json;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Serilog;

namespace CodingAgentWebUI.Infrastructure.Persistence.Stores;

/// <summary>
/// EF Core-backed implementation of <see cref="IConfigurationStore"/>.
/// Uses IDbContextFactory for singleton-safe context creation.
/// PipelineConfiguration is cached permanently (invalidated on write).
/// Other configs use short-lived cache with configurable TTL (default 30s).
/// </summary>
public sealed class PostgresConfigurationStore : IConfigurationStore
{
    private static readonly ILogger Logger = Log.ForContext<PostgresConfigurationStore>();
    private static readonly JsonSerializerOptions JsonOptions = PipelineJsonOptions.Default;

    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private MemoryCache _cache;
    private readonly TimeSpan _cacheTtl;
    private readonly SemaphoreSlim _pipelineConfigLock = new(1, 1);
    private readonly SemaphoreSlim _projectLock = new(1, 1);

    // Pipeline config is cached permanently until write invalidates
    private PipelineConfiguration? _pipelineConfigCache;

    /// <inheritdoc />
    public void InvalidateCaches()
    {
        _pipelineConfigCache = null;
        // Swap to a fresh cache instance. Don't dispose the old one — concurrent readers
        // may still hold a reference. GC will collect it after all references drain.
        Interlocked.Exchange(ref _cache, new MemoryCache(new MemoryCacheOptions()));
    }

    // Cache keys
    private const string ProviderCachePrefix = "providers_";
    private const string ProfilesCacheKey = "agent_profiles";
    private const string QualityGatesCacheKey = "quality_gates";
    private const string ReviewersCacheKey = "reviewers";
    private const string ProjectsCacheKey = "projects";
    private const string AllTemplatesCacheKey = "all_templates";

    public PostgresConfigurationStore(
        IDbContextFactory<PipelineDbContext> dbFactory,
        TimeSpan? cacheTtl = null)
    {
        _dbFactory = dbFactory;
        _cacheTtl = cacheTtl ?? TimeSpan.FromSeconds(30);
        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    // ── IPipelineConfigStore ─────────────────────────────────────────────

    public async Task<PipelineConfiguration> LoadPipelineConfigAsync(CancellationToken ct)
    {
        if (_pipelineConfigCache is not null)
            return _pipelineConfigCache;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.PipelineConfig.AsNoTracking().FirstOrDefaultAsync(ct);

        PipelineConfiguration result;
        if (entity?.Configuration is not null)
        {
            result = JsonSerializer.Deserialize<PipelineConfiguration>(
                entity.Configuration, JsonOptions)
                ?? new PipelineConfiguration();
        }
        else
        {
            result = new PipelineConfiguration();
        }

        _pipelineConfigCache = result;
        return result;
    }

    public async Task SavePipelineConfigAsync(PipelineConfiguration config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        await _pipelineConfigLock.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var entity = await db.PipelineConfig.FirstOrDefaultAsync(ct);

            var jsonDoc = SerializeToJson(config);

            if (entity is null)
            {
                entity = new PipelineConfigEntity
                {
                    Id = Guid.NewGuid(),
                    Configuration = jsonDoc
                };
                db.PipelineConfig.Add(entity);
            }
            else
            {
                entity.Configuration = jsonDoc;
            }

            await db.SaveChangesAsync(ct);
            _pipelineConfigCache = config;
        }
        finally
        {
            _pipelineConfigLock.Release();
        }
    }

    public async Task UpdatePipelineConfigAsync(
        Func<PipelineConfiguration, PipelineConfiguration> transform, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(transform);

        await _pipelineConfigLock.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var entity = await db.PipelineConfig.FirstOrDefaultAsync(ct);

            PipelineConfiguration current;
            if (entity?.Configuration is not null)
            {
                var deserialized = JsonSerializer.Deserialize<PipelineConfiguration>(
                    entity.Configuration, JsonOptions);
                if (deserialized is null)
                    throw new InvalidOperationException(
                        "Pipeline configuration row exists but contains invalid JSON.");
                current = deserialized;
            }
            else
            {
                current = new PipelineConfiguration();
            }

            var updated = transform(current);
            var jsonDoc = SerializeToJson(updated);

            if (entity is null)
            {
                entity = new PipelineConfigEntity
                {
                    Id = Guid.NewGuid(),
                    Configuration = jsonDoc
                };
                db.PipelineConfig.Add(entity);
            }
            else
            {
                entity.Configuration = jsonDoc;
            }

            await db.SaveChangesAsync(ct);
            _pipelineConfigCache = updated;
        }
        finally
        {
            _pipelineConfigLock.Release();
        }
    }

    // ── IProviderConfigStore ─────────────────────────────────────────────

    public async Task<IReadOnlyList<ProviderConfig>> LoadProviderConfigsAsync(
        ProviderKind kind, CancellationToken ct)
    {
        var cacheKey = ProviderCachePrefix + (int)kind;
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<ProviderConfig>? cached) && cached is not null)
            return cached;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entities = await db.ProviderConfigs
            .AsNoTracking()
            .Where(e => e.Kind == kind)
            .ToListAsync(ct);

        var result = entities
            .Select(DeserializeProviderConfig)
            .Where(c => c is not null)
            .Cast<ProviderConfig>()
            .ToList()
            .AsReadOnly();

        _cache.Set(cacheKey, result, _cacheTtl);
        return result;
    }

    public async Task<ProviderConfig?> GetProviderConfigByIdAsync(
        string id, ProviderKind kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!Guid.TryParse(id, out var guid))
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ProviderConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == guid && e.Kind == kind, ct);

        return entity is null ? null : DeserializeProviderConfig(entity);
    }

    public async Task SaveProviderConfigAsync(ProviderConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!Guid.TryParse(config.Id, out var guid))
            throw new ArgumentException($"Invalid provider config ID: {config.Id}");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ProviderConfigs
            .FirstOrDefaultAsync(e => e.Id == guid, ct);

        var jsonDoc = SerializeToJson(config);

        if (entity is null)
        {
            entity = new ProviderConfigEntity
            {
                Id = guid,
                Kind = config.Kind,
                DisplayName = config.DisplayName,
                ProviderType = config.ProviderType,
                Enabled = true,
                Configuration = jsonDoc
            };
            db.ProviderConfigs.Add(entity);
        }
        else
        {
            entity.Kind = config.Kind;
            entity.DisplayName = config.DisplayName;
            entity.ProviderType = config.ProviderType;
            entity.Configuration = jsonDoc;
        }

        await db.SaveChangesAsync(ct);
        InvalidateProviderCache(config.Kind);
    }

    public async Task DeleteProviderConfigAsync(string id, ProviderKind kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!Guid.TryParse(id, out var guid))
            return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ProviderConfigs
            .FirstOrDefaultAsync(e => e.Id == guid && e.Kind == kind, ct);

        if (entity is not null)
        {
            db.ProviderConfigs.Remove(entity);
            await db.SaveChangesAsync(ct);
        }

        InvalidateProviderCache(kind);
    }

    // ── IAgentProfileStore ───────────────────────────────────────────────

    public async Task<IReadOnlyList<AgentProfile>> LoadAgentProfilesAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(ProfilesCacheKey, out IReadOnlyList<AgentProfile>? cached) && cached is not null)
            return cached;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entities = await db.AgentProfiles.AsNoTracking().ToListAsync(ct);

        var result = entities
            .Select(e => DeserializeFromEntity<AgentProfile>(e.Configuration))
            .Where(p => p is not null)
            .Cast<AgentProfile>()
            .ToList()
            .AsReadOnly();

        _cache.Set(ProfilesCacheKey, result, _cacheTtl);
        return result;
    }

    public async Task SaveAgentProfileAsync(AgentProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!Guid.TryParse(profile.Id, out var guid))
            throw new ArgumentException($"Invalid profile ID: {profile.Id}");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.AgentProfiles.FirstOrDefaultAsync(e => e.Id == guid, ct);

        var jsonDoc = SerializeToJson(profile);

        if (entity is null)
        {
            entity = new AgentProfileEntity
            {
                Id = guid,
                Name = profile.DisplayName,
                Configuration = jsonDoc
            };
            db.AgentProfiles.Add(entity);
        }
        else
        {
            entity.Name = profile.DisplayName;
            entity.Configuration = jsonDoc;
        }

        await db.SaveChangesAsync(ct);
        _cache.Remove(ProfilesCacheKey);
    }

    public async Task DeleteAgentProfileAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!Guid.TryParse(id, out var guid))
            return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.AgentProfiles.FirstOrDefaultAsync(e => e.Id == guid, ct);

        if (entity is not null)
        {
            db.AgentProfiles.Remove(entity);
            await db.SaveChangesAsync(ct);
        }

        _cache.Remove(ProfilesCacheKey);
    }

    // ── IQualityGateConfigStore ──────────────────────────────────────────

    public async Task<IReadOnlyList<QualityGateConfiguration>> LoadQualityGateConfigsAsync(
        CancellationToken ct)
    {
        if (_cache.TryGetValue(QualityGatesCacheKey, out IReadOnlyList<QualityGateConfiguration>? cached)
            && cached is not null)
            return cached;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entities = await db.QualityGateConfigs.AsNoTracking().ToListAsync(ct);

        var result = entities
            .Select(e => DeserializeFromEntity<QualityGateConfiguration>(e.Configuration))
            .Where(c => c is not null)
            .Cast<QualityGateConfiguration>()
            .ToList()
            .AsReadOnly();

        _cache.Set(QualityGatesCacheKey, result, _cacheTtl);
        return result;
    }

    public async Task SaveQualityGateConfigAsync(QualityGateConfiguration config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!Guid.TryParse(config.Id, out var guid))
            throw new ArgumentException($"Invalid quality gate config ID: {config.Id}");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.QualityGateConfigs.FirstOrDefaultAsync(e => e.Id == guid, ct);

        var jsonDoc = SerializeToJson(config);

        if (entity is null)
        {
            entity = new QualityGateConfigEntity
            {
                Id = guid,
                Name = config.DisplayName,
                Configuration = jsonDoc
            };
            db.QualityGateConfigs.Add(entity);
        }
        else
        {
            entity.Name = config.DisplayName;
            entity.Configuration = jsonDoc;
        }

        await db.SaveChangesAsync(ct);
        _cache.Remove(QualityGatesCacheKey);
    }

    public async Task DeleteQualityGateConfigAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!Guid.TryParse(id, out var guid))
            return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.QualityGateConfigs.FirstOrDefaultAsync(e => e.Id == guid, ct);

        if (entity is not null)
        {
            db.QualityGateConfigs.Remove(entity);
            await db.SaveChangesAsync(ct);
        }

        _cache.Remove(QualityGatesCacheKey);
    }

    // ── IReviewerConfigStore ─────────────────────────────────────────────

    public async Task<IReadOnlyList<ReviewerConfiguration>> LoadReviewerConfigsAsync(
        CancellationToken ct)
    {
        if (_cache.TryGetValue(ReviewersCacheKey, out IReadOnlyList<ReviewerConfiguration>? cached)
            && cached is not null)
            return cached;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entities = await db.ReviewerConfigs.AsNoTracking().ToListAsync(ct);

        var result = entities
            .Select(e => DeserializeFromEntity<ReviewerConfiguration>(e.Configuration))
            .Where(c => c is not null)
            .Cast<ReviewerConfiguration>()
            .ToList()
            .AsReadOnly();

        _cache.Set(ReviewersCacheKey, result, _cacheTtl);
        return result;
    }

    public async Task SaveReviewerConfigAsync(ReviewerConfiguration config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!Guid.TryParse(config.Id, out var guid))
            throw new ArgumentException($"Invalid reviewer config ID: {config.Id}");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ReviewerConfigs.FirstOrDefaultAsync(e => e.Id == guid, ct);

        var jsonDoc = SerializeToJson(config);

        if (entity is null)
        {
            entity = new ReviewerConfigEntity
            {
                Id = guid,
                Name = config.DisplayName,
                Configuration = jsonDoc
            };
            db.ReviewerConfigs.Add(entity);
        }
        else
        {
            entity.Name = config.DisplayName;
            entity.Configuration = jsonDoc;
        }

        await db.SaveChangesAsync(ct);
        _cache.Remove(ReviewersCacheKey);
    }

    public async Task DeleteReviewerConfigAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!Guid.TryParse(id, out var guid))
            return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ReviewerConfigs.FirstOrDefaultAsync(e => e.Id == guid, ct);

        if (entity is not null)
        {
            db.ReviewerConfigs.Remove(entity);
            await db.SaveChangesAsync(ct);
        }

        _cache.Remove(ReviewersCacheKey);
    }

    public async Task ResetReviewerConfigsToDefaultAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Remove all existing reviewer configs
        var existing = await db.ReviewerConfigs.ToListAsync(ct);
        db.ReviewerConfigs.RemoveRange(existing);

        // Insert default configurations
        foreach (var config in PipelineConfiguration.DefaultReviewerConfigurations)
        {
            if (!Guid.TryParse(config.Id, out var guid))
                guid = Guid.NewGuid();

            db.ReviewerConfigs.Add(new ReviewerConfigEntity
            {
                Id = guid,
                Name = config.DisplayName,
                Configuration = SerializeToJson(config)
            });
        }

        await db.SaveChangesAsync(ct);
        _cache.Remove(ReviewersCacheKey);
    }

    // ── IProjectStore ────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PipelineProject>> LoadProjectsAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(ProjectsCacheKey, out IReadOnlyList<PipelineProject>? cached)
            && cached is not null)
            return cached;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entities = await db.Projects.AsNoTracking().ToListAsync(ct);

        var result = entities
            .Select(DeserializeProject)
            .Where(p => p is not null)
            .Cast<PipelineProject>()
            .ToList()
            .AsReadOnly();

        _cache.Set(ProjectsCacheKey, result, _cacheTtl);
        return result;
    }

    public async Task<PipelineProject?> GetProjectByIdAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!Guid.TryParse(id, out var guid))
            return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == guid, ct);

        return entity is null ? null : DeserializeProject(entity);
    }

    public async Task SaveProjectAsync(PipelineProject project, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!Guid.TryParse(project.Id, out var guid))
            throw new ArgumentException($"Invalid project ID: {project.Id}");

        await _projectLock.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var entity = await db.Projects.FirstOrDefaultAsync(e => e.Id == guid, ct);

            var settingsDoc = SerializeToJson(project);

            if (entity is null)
            {
                entity = new ProjectEntity
                {
                    Id = guid,
                    Name = project.Name,
                    Enabled = project.Enabled,
                    Description = project.Description,
                    Settings = settingsDoc,
                    TemplateIds = project.TemplateIds.ToList()
                };
                db.Projects.Add(entity);
            }
            else
            {
                entity.Name = project.Name;
                entity.Enabled = project.Enabled;
                entity.Description = project.Description;
                entity.Settings = settingsDoc;
                entity.TemplateIds = project.TemplateIds.ToList();
            }

            await db.SaveChangesAsync(ct);
            InvalidateProjectCaches();
        }
        finally
        {
            _projectLock.Release();
        }
    }

    public async Task DeleteProjectAsync(string id, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (!Guid.TryParse(id, out var guid))
            return;

        if (string.Equals(id, WellKnownIds.DefaultProjectId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Default project cannot be deleted.");

        await _projectLock.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var entity = await db.Projects.FirstOrDefaultAsync(e => e.Id == guid, ct);
            if (entity is null)
                return;

            // Move orphaned templates to Default project
            if (entity.TemplateIds.Count > 0)
            {
                var defaultGuid = Guid.Parse(WellKnownIds.DefaultProjectId);
                var defaultProject = await db.Projects
                    .FirstOrDefaultAsync(e => e.Id == defaultGuid, ct);

                if (defaultProject is not null)
                {
                    defaultProject.TemplateIds.AddRange(entity.TemplateIds);
                    // Move template entities
                    var templateGuids = entity.TemplateIds
                        .Where(t => Guid.TryParse(t, out _))
                        .Select(Guid.Parse)
                        .ToList();
                    var templates = await db.PipelineJobTemplates
                        .Where(t => templateGuids.Contains(t.Id))
                        .ToListAsync(ct);
                    foreach (var t in templates)
                        t.ProjectId = defaultGuid;

                    Logger.Information(
                        "Moved {Count} templates from deleted project to Default project",
                        entity.TemplateIds.Count);
                }
            }

            db.Projects.Remove(entity);
            await db.SaveChangesAsync(ct);
            InvalidateProjectCaches();
        }
        finally
        {
            _projectLock.Release();
        }
    }

    // ── Template CRUD ────────────────────────────────────────────────────

    public async Task<IReadOnlyList<PipelineJobTemplate>> LoadTemplatesForProjectAsync(
        string projectId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        if (!Guid.TryParse(projectId, out var guid))
            return [];

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var project = await db.Projects
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == guid, ct);

        if (project is null)
            return [];

        var templateGuids = project.TemplateIds
            .Where(t => Guid.TryParse(t, out _))
            .Select(Guid.Parse)
            .ToList();

        var entities = await db.PipelineJobTemplates
            .AsNoTracking()
            .Where(t => t.ProjectId == guid)
            .ToListAsync(ct);

        var templateMap = entities
            .Select(e => DeserializeFromEntity<PipelineJobTemplate>(e.Configuration))
            .Where(t => t is not null)
            .Cast<PipelineJobTemplate>()
            .ToDictionary(t => t.Id);

        // Order by TemplateIds position
        var ordered = new List<PipelineJobTemplate>();
        foreach (var tid in project.TemplateIds)
        {
            if (templateMap.TryGetValue(tid, out var t))
                ordered.Add(t);
        }
        return ordered.AsReadOnly();
    }

    public async Task<IReadOnlyList<PipelineJobTemplate>> LoadAllTemplatesAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(AllTemplatesCacheKey, out IReadOnlyList<PipelineJobTemplate>? cached)
            && cached is not null)
            return cached;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entities = await db.PipelineJobTemplates.AsNoTracking().ToListAsync(ct);

        var result = entities
            .Select(e => DeserializeFromEntity<PipelineJobTemplate>(e.Configuration))
            .Where(t => t is not null)
            .Cast<PipelineJobTemplate>()
            .ToList()
            .AsReadOnly();

        _cache.Set(AllTemplatesCacheKey, result, _cacheTtl);
        return result;
    }

    public async Task SaveTemplateAsync(
        string projectId, PipelineJobTemplate template, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(template);
        if (!Guid.TryParse(projectId, out var projectGuid))
            return;
        if (!Guid.TryParse(template.Id, out var templateGuid))
            throw new ArgumentException($"Invalid template ID: {template.Id}");

        await _projectLock.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // Verify project exists
            var project = await db.Projects.FirstOrDefaultAsync(e => e.Id == projectGuid, ct);
            if (project is null)
                return;

            var entity = await db.PipelineJobTemplates
                .FirstOrDefaultAsync(e => e.Id == templateGuid, ct);

            var jsonDoc = SerializeToJson(template);

            if (entity is null)
            {
                entity = new PipelineJobTemplateEntity
                {
                    Id = templateGuid,
                    ProjectId = projectGuid,
                    Name = template.Name,
                    Configuration = jsonDoc
                };
                db.PipelineJobTemplates.Add(entity);
            }
            else
            {
                entity.ProjectId = projectGuid;
                entity.Name = template.Name;
                entity.Configuration = jsonDoc;
            }

            // Add template ID to project's TemplateIds if not present
            if (!project.TemplateIds.Contains(template.Id))
            {
                project.TemplateIds.Add(template.Id);
            }

            await db.SaveChangesAsync(ct);
            InvalidateProjectCaches();
        }
        finally
        {
            _projectLock.Release();
        }
    }

    public async Task DeleteTemplateAsync(
        string projectId, string templateId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        ArgumentNullException.ThrowIfNull(templateId);
        if (!Guid.TryParse(projectId, out var projectGuid))
            return;
        if (!Guid.TryParse(templateId, out var templateGuid))
            return;

        await _projectLock.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var entity = await db.PipelineJobTemplates
                .FirstOrDefaultAsync(e => e.Id == templateGuid, ct);
            if (entity is not null)
                db.PipelineJobTemplates.Remove(entity);

            // Remove template ID from project's TemplateIds
            var project = await db.Projects.FirstOrDefaultAsync(e => e.Id == projectGuid, ct);
            if (project is not null)
                project.TemplateIds.Remove(templateId);

            await db.SaveChangesAsync(ct);
            InvalidateProjectCaches();
        }
        finally
        {
            _projectLock.Release();
        }
    }

    public async Task MoveTemplateAsync(
        string sourceProjectId, string targetProjectId, string templateId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sourceProjectId);
        ArgumentNullException.ThrowIfNull(targetProjectId);
        ArgumentNullException.ThrowIfNull(templateId);
        if (!Guid.TryParse(sourceProjectId, out var sourceGuid))
            return;
        if (!Guid.TryParse(targetProjectId, out var targetGuid))
            return;
        if (!Guid.TryParse(templateId, out var templateGuid))
            return;

        await _projectLock.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // Update the template's project reference
            var entity = await db.PipelineJobTemplates
                .FirstOrDefaultAsync(e => e.Id == templateGuid, ct);
            if (entity is not null)
                entity.ProjectId = targetGuid;

            // Remove from source project's TemplateIds
            var sourceProject = await db.Projects
                .FirstOrDefaultAsync(e => e.Id == sourceGuid, ct);
            if (sourceProject is not null)
            {
                sourceProject.TemplateIds.Remove(templateId);
                ResyncSettingsJson(sourceProject);
            }

            // Add to target project's TemplateIds
            var targetProject = await db.Projects
                .FirstOrDefaultAsync(e => e.Id == targetGuid, ct);
            if (targetProject is not null && !targetProject.TemplateIds.Contains(templateId))
            {
                targetProject.TemplateIds.Add(templateId);
                ResyncSettingsJson(targetProject);
            }

            await db.SaveChangesAsync(ct);
            InvalidateProjectCaches();
        }
        finally
        {
            _projectLock.Release();
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────

    private static string SerializeToJson<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    /// <summary>
    /// Re-serializes the Settings JSON on a ProjectEntity to match the current
    /// TemplateIds column and entity Id, preventing drift between the two data sources.
    /// </summary>
    private static void ResyncSettingsJson(ProjectEntity entity)
    {
        if (entity.Settings is null)
            return;

        var project = JsonSerializer.Deserialize<PipelineProject>(entity.Settings, JsonOptions);
        if (project is null)
            return;

        var synced = project with
        {
            Id = entity.Id.ToString(),
            TemplateIds = entity.TemplateIds
        };
        entity.Settings = SerializeToJson(synced);
    }

    private static T? DeserializeFromEntity<T>(string? json) where T : class
    {
        if (json is null) return null;
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    private static ProviderConfig? DeserializeProviderConfig(ProviderConfigEntity entity)
    {
        if (entity.Configuration is null) return null;
        return JsonSerializer.Deserialize<ProviderConfig>(entity.Configuration, JsonOptions);
    }

    private static PipelineProject? DeserializeProject(ProjectEntity entity)
    {
        if (entity.Settings is null)
        {
            // Minimal project from typed columns only
            return new PipelineProject
            {
                Id = entity.Id.ToString(),
                Name = entity.Name,
                Enabled = entity.Enabled,
                Description = entity.Description,
                TemplateIds = entity.TemplateIds
            };
        }

        var project = JsonSerializer.Deserialize<PipelineProject>(entity.Settings, JsonOptions);
        if (project is null)
            return null;

        // Override with authoritative column values — the Settings JSON may be stale
        // if MoveTemplateAsync (or other code paths) updated columns without re-serializing JSON.
        // NOTE: entity.TemplateIds is a mutable List assigned without a defensive copy (.ToList()).
        // NOTE: Name, Enabled, Description are not overridden here; if they diverge, consider
        // overriding all typed-column fields for consistency with the null-Settings fallback path.
        return project with
        {
            Id = entity.Id.ToString(),
            TemplateIds = entity.TemplateIds
        };
    }

    private void InvalidateProviderCache(ProviderKind kind)
    {
        _cache.Remove(ProviderCachePrefix + (int)kind);
    }

    private void InvalidateProjectCaches()
    {
        _cache.Remove(ProjectsCacheKey);
        _cache.Remove(AllTemplatesCacheKey);
    }
}
