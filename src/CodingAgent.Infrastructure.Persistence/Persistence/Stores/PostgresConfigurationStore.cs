using System.Text.Json;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Serilog;

namespace CodingAgent.Infrastructure.Persistence.Stores;

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

    // Pipeline config cache. When CacheEnabled=true, populated on every load/save/update and cleared
    // by InvalidateCaches(). When CacheEnabled=false, this field is never set and every read goes
    // directly to the database; InvalidateCaches() assigns null, which is a no-op in that mode.
    // TODO: The comment "cached permanently until write invalidates" that appeared here was accurate
    // only for the CacheEnabled=true path. With CacheEnabled=false (as used by the API to prevent
    // stale config across replicas) this field is intentionally unused. If the two-mode behaviour
    // becomes confusing, consider splitting into CachingConfigurationStore / PassThroughConfigurationStore
    // (see docs/internals/decisions.md for guidance on introducing new store abstractions).
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
        // A null or non-positive TTL means "no caching" — skip _cache.Set entirely.
        // TimeSpan.Zero is not valid for MemoryCache AbsoluteExpirationRelativeToNow
        // (it throws ArgumentOutOfRangeException), so we treat <= 0 as a disabled signal
        // rather than passing it to the cache.
        _cacheTtl = cacheTtl ?? TimeSpan.FromSeconds(30);
        _cache = new MemoryCache(new MemoryCacheOptions());
    }

    private bool CacheEnabled => _cacheTtl > TimeSpan.Zero;

    // ── IPipelineConfigStore ─────────────────────────────────────────────

    public async Task<PipelineConfiguration> LoadPipelineConfigAsync(CancellationToken ct)
    {
        if (CacheEnabled && _pipelineConfigCache is not null)
            return _pipelineConfigCache;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.PipelineConfig.AsNoTracking().FirstOrDefaultAsync(ct);

        PipelineConfiguration result;
        if (entity?.Configuration is not null)
        {
            // TODO: PipelineConfiguration is still deserialized with JsonOptions (PipelineJsonOptions.Default),
            // which lacks PropertyNameCaseInsensitive=true. If PipelineConfiguration contains any
            // camelCase-stored fields that are silently nulled during deserialization (the same class of
            // bug fixed in DeserializeFromEntity for issue #2633), those fields will not be covered by
            // this path. Switching to PipelineJsonOptions.Lenient here would close that gap, but the
            // impact of changing this deserialization path should be assessed separately.
            result = JsonSerializer.Deserialize<PipelineConfiguration>(
                entity.Configuration, JsonOptions)
                ?? new PipelineConfiguration();
        }
        else
        {
            result = new PipelineConfiguration();
        }

        if (CacheEnabled) _pipelineConfigCache = result;
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
            if (CacheEnabled) _pipelineConfigCache = config;
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
                // TODO: Same as LoadPipelineConfigAsync — PipelineConfiguration is deserialized
                // with JsonOptions (PipelineJsonOptions.Default), which lacks PropertyNameCaseInsensitive=true.
                // See the comment in LoadPipelineConfigAsync for details.
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
            if (CacheEnabled) _pipelineConfigCache = updated;
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

        if (CacheEnabled) _cache.Set(cacheKey, result, _cacheTtl);
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

        if (CacheEnabled) _cache.Set(ProfilesCacheKey, result, _cacheTtl);
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

        if (CacheEnabled) _cache.Set(QualityGatesCacheKey, result, _cacheTtl);
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

        if (CacheEnabled) _cache.Set(ReviewersCacheKey, result, _cacheTtl);
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
        foreach (var config in PipelineConfigurationDefaults.DefaultReviewerConfigurations)
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
    //
    // A template's own project (PipelineJobTemplateEntity.ProjectId) is the only membership record.
    // PipelineProject.TemplateIds is filled from it on load, in TemplateOrder, and ignored on save.

    public async Task<IReadOnlyList<PipelineProject>> LoadProjectsAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(ProjectsCacheKey, out IReadOnlyList<PipelineProject>? cached)
            && cached is not null)
            return cached;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entities = await db.Projects.AsNoTracking().ToListAsync(ct);
        var membership = await LoadMembershipAsync(db, projectId: null, ct);

        var result = entities
            .Select(e => DeserializeProject(e, membership))
            .Where(p => p is not null)
            .Cast<PipelineProject>()
            .ToList()
            .AsReadOnly();

        if (CacheEnabled) _cache.Set(ProjectsCacheKey, result, _cacheTtl);
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
        if (entity is null)
            return null;

        var membership = await LoadMembershipAsync(db, guid, ct);
        return DeserializeProject(entity, membership);
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

            // Membership is not part of the project: its TemplateIds are derived on load, so none are stored.
            var settingsDoc = SerializeToJson(project with { TemplateIds = [] });

            if (entity is null)
            {
                entity = new ProjectEntity
                {
                    Id = guid,
                    Name = project.Name,
                    Enabled = project.Enabled,
                    Description = project.Description,
                    Settings = settingsDoc
                };
                db.Projects.Add(entity);
            }
            else
            {
                entity.Name = project.Name;
                entity.Enabled = project.Enabled;
                entity.Description = project.Description;
                entity.Settings = settingsDoc;
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

            // The deleted project's templates move to the Default project.
            var defaultGuid = Guid.Parse(WellKnownIds.DefaultProjectId);
            var templates = await db.PipelineJobTemplates
                .Where(t => t.ProjectId == guid)
                .ToListAsync(ct);
            foreach (var t in templates)
                t.ProjectId = defaultGuid;

            if (templates.Count > 0)
            {
                Logger.Information(
                    "Moved {Count} templates from deleted project {ProjectId} to Default project",
                    templates.Count, guid);
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
        var entities = await db.PipelineJobTemplates
            .AsNoTracking()
            .Where(t => t.ProjectId == guid)
            .ToListAsync(ct);

        return TemplateOrder.ByName(entities, e => e.Name, e => e.Id.ToString())
            .Select(e => DeserializeFromEntity<PipelineJobTemplate>(e.Configuration))
            .Where(t => t is not null)
            .Cast<PipelineJobTemplate>()
            .ToList()
            .AsReadOnly();
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

        if (CacheEnabled) _cache.Set(AllTemplatesCacheKey, result, _cacheTtl);
        return result;
    }

    public async Task<bool> HasEnabledTemplatesAsync(CancellationToken ct)
    {
        // Delegate to LoadAllTemplatesAsync so we benefit from the existing cache.
        // The template list is already loaded in most request paths; this avoids a second DB round-trip.
        var all = await LoadAllTemplatesAsync(ct);
        return all.Any(t => t.Enabled);
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
            if (!await db.Projects.AnyAsync(e => e.Id == projectGuid, ct))
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

            await db.SaveChangesAsync(ct);
            InvalidateProjectCaches();
        }
        finally
        {
            _projectLock.Release();
        }
    }

    public async Task DeleteTemplateAsync(
        string projectId, TemplateId templateId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        if (!Guid.TryParse(projectId, out _))
            return;
        if (!Guid.TryParse(templateId.Value, out var templateGuid))
            return;

        await _projectLock.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            var entity = await db.PipelineJobTemplates
                .FirstOrDefaultAsync(e => e.Id == templateGuid, ct);
            if (entity is null)
                return;

            db.PipelineJobTemplates.Remove(entity);
            await db.SaveChangesAsync(ct);
            InvalidateProjectCaches();
        }
        finally
        {
            _projectLock.Release();
        }
    }

    public async Task MoveTemplateAsync(
        ProjectId sourceProjectId, ProjectId targetProjectId, TemplateId templateId, CancellationToken ct)
    {
        if (!Guid.TryParse(sourceProjectId.Value, out _))
            return;
        if (!Guid.TryParse(targetProjectId.Value, out var targetGuid))
            return;
        if (!Guid.TryParse(templateId.Value, out var templateGuid))
            return;

        await _projectLock.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);

            // A template must always point at a project that exists, or it would drop out of every project.
            if (!await db.Projects.AnyAsync(e => e.Id == targetGuid, ct))
                return;

            var entity = await db.PipelineJobTemplates
                .FirstOrDefaultAsync(e => e.Id == templateGuid, ct);
            if (entity is null)
                return;

            entity.ProjectId = targetGuid;
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

    // TODO: PipelineJsonOptions.Lenient adds PropertyNameCaseInsensitive=true (required for
    // correct camelCase JSONB deserialization) but does NOT include TimeSpanJsonConverter.
    // The four types currently passed through this method (PipelineJobTemplate, AgentProfile,
    // QualityGateConfiguration, ReviewerConfiguration) have no TimeSpan properties, so there
    // is no active defect. However, if any of these models gain a TimeSpan property in the
    // future, STJ will throw a JsonException at runtime (not silently null the property) for
    // an unrecognised TimeSpan string format — this would surface as an unhandled deserialization
    // exception in LoadAllTemplatesAsync / LoadTemplatesForProjectAsync and take those load paths
    // down entirely, with no compile-time warning. Consider replacing Lenient with a dedicated
    // read-options instance that mirrors Lenient but also includes TimeSpanJsonConverter to
    // eliminate this future-regression risk. If TimeSpan support is needed sooner, either add
    // TimeSpanJsonConverter to a dedicated read-options instance or extend PipelineJsonOptions.Lenient.
    private static T? DeserializeFromEntity<T>(string? json) where T : class
    {
        if (json is null) return null;
        return JsonSerializer.Deserialize<T>(json, PipelineJsonOptions.Lenient);
    }

    private static ProviderConfig? DeserializeProviderConfig(ProviderConfigEntity entity)
    {
        if (entity.Configuration is null) return null;
        return JsonSerializer.Deserialize<ProviderConfig>(entity.Configuration, JsonOptions);
    }

    private static PipelineProject? DeserializeProject(ProjectEntity entity, ILookup<Guid, string> membership)
    {
        var templateIds = membership[entity.Id].ToList();

        if (entity.Settings is null)
        {
            // Minimal project from typed columns only
            return new PipelineProject
            {
                Id = entity.Id.ToString(),
                Name = entity.Name,
                Enabled = entity.Enabled,
                Description = entity.Description,
                TemplateIds = templateIds
            };
        }

        var project = JsonSerializer.Deserialize<PipelineProject>(entity.Settings, JsonOptions);
        if (project is null)
            return null;

        // The row's ID and the templates' own project are authoritative; Settings JSON saved before
        // membership moved to the templates may still carry a stale TemplateIds list.
        // NOTE: Name, Enabled, Description are not overridden here; if they diverge, consider
        // overriding all typed-column fields for consistency with the null-Settings fallback path.
        return project with
        {
            Id = entity.Id.ToString(),
            TemplateIds = templateIds
        };
    }

    /// <summary>
    /// Each project's template IDs in <see cref="TemplateOrder"/>, read from the templates' own project, which is
    /// the only membership record. With <paramref name="projectId"/>, only that project's templates are read.
    /// </summary>
    private static async Task<ILookup<Guid, string>> LoadMembershipAsync(
        PipelineDbContext db, Guid? projectId, CancellationToken ct)
    {
        var query = db.PipelineJobTemplates.AsNoTracking();
        if (projectId is { } id)
            query = query.Where(t => t.ProjectId == id);

        var rows = await query.Select(t => new { t.Id, t.ProjectId, t.Name }).ToListAsync(ct);
        return TemplateOrder.ByName(rows, r => r.Name, r => r.Id.ToString())
            .ToLookup(r => r.ProjectId, r => r.Id.ToString());
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
