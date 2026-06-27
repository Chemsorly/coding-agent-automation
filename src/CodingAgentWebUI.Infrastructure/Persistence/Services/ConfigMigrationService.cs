using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgentWebUI.Infrastructure.Persistence.Services;

/// <summary>
/// Migrates existing JSON file-based configuration to PostgreSQL on first startup.
/// Idempotent: skips if PipelineConfig table already has a row.
/// Acquires advisory lock to prevent concurrent migration from racing replicas.
/// </summary>
public sealed class ConfigMigrationService
{
    private static readonly ILogger Logger = Log.ForContext<ConfigMigrationService>();
    private static readonly JsonSerializerOptions JsonOptions = PipelineJsonOptions.Default;

    private const string MigrationLockKey = "caa_schema_migration";

    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly IDistributedLockProvider _lockProvider;
    private readonly string _configBasePath;

    public ConfigMigrationService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        IDistributedLockProvider lockProvider,
        string configBasePath = PipelineConstants.ConfigBaseDirectory)
    {
        _dbFactory = dbFactory;
        _lockProvider = lockProvider;
        _configBasePath = configBasePath;
    }

    /// <summary>
    /// Runs the JSON-to-DB migration if the database is empty.
    /// Returns true if migration was performed, false if skipped (already migrated or no files).
    /// Throws on parse failure (after rollback).
    /// </summary>
    public async Task<bool> MigrateIfNeededAsync(CancellationToken ct)
    {
        await using var lockHandle = await _lockProvider.AcquireAsync(MigrationLockKey, ct);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Idempotent check: if PipelineConfig row exists, migration is complete
        var hasConfig = await db.PipelineConfig.AnyAsync(ct);
        if (hasConfig)
        {
            Logger.Information("ConfigMigration: PipelineConfig row exists, skipping migration");
            return false;
        }

        // If config directory doesn't exist or is empty, skip migration
        if (!Directory.Exists(_configBasePath))
        {
            Logger.Information("ConfigMigration: Config directory {Path} does not exist, skipping migration", _configBasePath);
            return false;
        }

        var pipelineConfigPath = Path.Combine(_configBasePath, "pipeline-config.json");
        if (!File.Exists(pipelineConfigPath) && !HasAnyJsonFiles(_configBasePath))
        {
            Logger.Information("ConfigMigration: Config directory is empty, skipping migration");
            return false;
        }

        Logger.Information("ConfigMigration: Empty database detected, starting migration from {Path}", _configBasePath);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        try
        {
            var counts = new MigrationCounts();

            // 1. Pipeline config
            await MigratePipelineConfigAsync(db, counts, ct);

            // 2. Provider configs
            await MigrateProviderConfigsAsync(db, counts, ct);

            // 3. Agent profiles
            await MigrateAgentProfilesAsync(db, counts, ct);

            // 4. Quality gate configs
            await MigrateQualityGateConfigsAsync(db, counts, ct);

            // 5. Reviewer configs
            await MigrateReviewerConfigsAsync(db, counts, ct);

            // 6. Projects + templates
            await MigrateProjectsAsync(db, counts, ct);

            // 7. Consolidation runs
            await MigrateConsolidationRunsAsync(db, counts, ct);

            // 8. Pipeline runs
            await MigratePipelineRunsAsync(db, counts, ct);

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            Logger.Information(
                "ConfigMigration: Completed successfully. " +
                "PipelineConfig: {PipelineConfig}, ProviderConfigs: {Providers}, " +
                "AgentProfiles: {Profiles}, QualityGates: {QualityGates}, " +
                "Reviewers: {Reviewers}, Projects: {Projects}, " +
                "Templates: {Templates}, ConsolidationRuns: {ConsolidationRuns}, " +
                "PipelineRuns: {PipelineRuns}",
                counts.PipelineConfig, counts.ProviderConfigs,
                counts.AgentProfiles, counts.QualityGates,
                counts.Reviewers, counts.Projects,
                counts.Templates, counts.ConsolidationRuns,
                counts.PipelineRuns);

            return true;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(ct);
            Logger.Error(ex, "ConfigMigration: Failed — transaction rolled back");
            throw;
        }
    }

    private async Task MigratePipelineConfigAsync(PipelineDbContext db, MigrationCounts counts, CancellationToken ct)
    {
        var path = Path.Combine(_configBasePath, "pipeline-config.json");
        PipelineConfiguration config;

        if (File.Exists(path))
        {
            var json = await File.ReadAllTextAsync(path, ct);
            config = DeserializeOrThrow<PipelineConfiguration>(json, path);
        }
        else
        {
            config = new PipelineConfiguration();
        }

        db.PipelineConfig.Add(new PipelineConfigEntity
        {
            Id = Guid.NewGuid(),
            Configuration = SerializeToDocument(config)
        });

        counts.PipelineConfig = 1;
    }

    private async Task MigrateProviderConfigsAsync(PipelineDbContext db, MigrationCounts counts, CancellationToken ct)
    {
        var providersDir = Path.Combine(_configBasePath, "providers");
        if (!Directory.Exists(providersDir))
            return;

        var kindMapping = new Dictionary<string, ProviderKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["issue"] = ProviderKind.Issue,
            ["repository"] = ProviderKind.Repository,
            ["agent"] = ProviderKind.Agent,
            ["brain"] = ProviderKind.Brain,
            ["pipeline"] = ProviderKind.Pipeline
        };

        foreach (var subDir in Directory.GetDirectories(providersDir))
        {
            var dirName = Path.GetFileName(subDir);
            if (!kindMapping.TryGetValue(dirName, out var kind))
            {
                Logger.Warning("ConfigMigration: Unknown provider subdirectory '{DirName}', skipping", dirName);
                continue;
            }

            foreach (var file in Directory.GetFiles(subDir, "*.json"))
            {
                var json = await File.ReadAllTextAsync(file, ct);
                var config = DeserializeOrThrow<ProviderConfig>(json, file);

                if (!Guid.TryParse(config.Id, out var guid))
                    guid = Guid.NewGuid();

                db.ProviderConfigs.Add(new ProviderConfigEntity
                {
                    Id = guid,
                    Kind = kind,
                    DisplayName = config.DisplayName,
                    ProviderType = config.ProviderType,
                    Enabled = true,
                    Configuration = SerializeToDocument(config)
                });

                counts.ProviderConfigs++;
            }
        }
    }

    private async Task MigrateAgentProfilesAsync(PipelineDbContext db, MigrationCounts counts, CancellationToken ct)
    {
        var dir = Path.Combine(_configBasePath, "profiles");
        if (!Directory.Exists(dir))
            return;

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var json = await File.ReadAllTextAsync(file, ct);
            var profile = DeserializeOrThrow<AgentProfile>(json, file);

            if (!Guid.TryParse(profile.Id, out var guid))
                guid = Guid.NewGuid();

            db.AgentProfiles.Add(new AgentProfileEntity
            {
                Id = guid,
                Name = profile.DisplayName,
                Configuration = SerializeToDocument(profile)
            });

            counts.AgentProfiles++;
        }
    }

    private async Task MigrateQualityGateConfigsAsync(PipelineDbContext db, MigrationCounts counts, CancellationToken ct)
    {
        var dir = Path.Combine(_configBasePath, "quality-gates");
        if (!Directory.Exists(dir))
            return;

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var json = await File.ReadAllTextAsync(file, ct);
            var config = DeserializeOrThrow<QualityGateConfiguration>(json, file);

            if (!Guid.TryParse(config.Id, out var guid))
                guid = Guid.NewGuid();

            db.QualityGateConfigs.Add(new QualityGateConfigEntity
            {
                Id = guid,
                Name = config.DisplayName,
                Configuration = SerializeToDocument(config)
            });

            counts.QualityGates++;
        }
    }

    private async Task MigrateReviewerConfigsAsync(PipelineDbContext db, MigrationCounts counts, CancellationToken ct)
    {
        var dir = Path.Combine(_configBasePath, "reviewers");
        if (!Directory.Exists(dir))
            return;

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var json = await File.ReadAllTextAsync(file, ct);
            var config = DeserializeOrThrow<ReviewerConfiguration>(json, file);

            if (!Guid.TryParse(config.Id, out var guid))
                guid = Guid.NewGuid();

            db.ReviewerConfigs.Add(new ReviewerConfigEntity
            {
                Id = guid,
                Name = config.DisplayName,
                Configuration = SerializeToDocument(config)
            });

            counts.Reviewers++;
        }
    }

    private async Task MigrateProjectsAsync(PipelineDbContext db, MigrationCounts counts, CancellationToken ct)
    {
        var projectsDir = Path.Combine(_configBasePath, "projects");
        if (!Directory.Exists(projectsDir))
            return;

        // Root-level project JSON files (e.g., projects/{id}.json)
        foreach (var file in Directory.GetFiles(projectsDir, "*.json"))
        {
            var json = await File.ReadAllTextAsync(file, ct);
            var project = DeserializeOrThrow<PipelineProject>(json, file);

            if (!Guid.TryParse(project.Id, out var projectGuid))
                projectGuid = Guid.NewGuid();

            db.Projects.Add(new ProjectEntity
            {
                Id = projectGuid,
                Name = project.Name,
                Enabled = project.Enabled,
                Description = project.Description,
                Settings = SerializeToDocument(project),
                TemplateIds = project.TemplateIds.ToList()
            });

            counts.Projects++;

            // Templates under projects/{id}/templates/*.json
            var templatesDir = Path.Combine(projectsDir, project.Id, "templates");
            if (Directory.Exists(templatesDir))
            {
                foreach (var templateFile in Directory.GetFiles(templatesDir, "*.json"))
                {
                    var templateJson = await File.ReadAllTextAsync(templateFile, ct);
                    var template = DeserializeOrThrow<PipelineJobTemplate>(templateJson, templateFile);

                    if (!Guid.TryParse(template.Id, out var templateGuid))
                        templateGuid = Guid.NewGuid();

                    db.PipelineJobTemplates.Add(new PipelineJobTemplateEntity
                    {
                        Id = templateGuid,
                        ProjectId = projectGuid,
                        Name = template.Name,
                        Configuration = SerializeToDocument(template)
                    });

                    counts.Templates++;
                }
            }
        }
    }

    private async Task MigrateConsolidationRunsAsync(PipelineDbContext db, MigrationCounts counts, CancellationToken ct)
    {
        var dir = Path.Combine(_configBasePath, "consolidation-runs");
        if (!Directory.Exists(dir))
            return;

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var json = await File.ReadAllTextAsync(file, ct);
            var run = DeserializeOrThrow<ConsolidationRun>(json, file);

            if (!Guid.TryParse(run.RunId, out var guid))
                guid = Guid.NewGuid();

            db.ConsolidationRuns.Add(new ConsolidationRunEntity
            {
                Id = guid,
                Data = SerializeToDocument(run)
            });

            counts.ConsolidationRuns++;
        }
    }

    private async Task MigratePipelineRunsAsync(PipelineDbContext db, MigrationCounts counts, CancellationToken ct)
    {
        var dir = Path.Combine(_configBasePath, "runs");
        if (!Directory.Exists(dir))
            return;

        foreach (var file in Directory.GetFiles(dir, "*.json"))
        {
            var json = await File.ReadAllTextAsync(file, ct);
            var summary = DeserializeOrThrow<PipelineRunSummary>(json, file);

            if (!Guid.TryParse(summary.RunId, out var runGuid))
                runGuid = Guid.NewGuid();

#pragma warning disable CS0618 // Fallback to legacy StartedAt for older persisted summaries
            var startedAtOffset = summary.StartedAtOffset != default
                ? summary.StartedAtOffset
                : new DateTimeOffset(summary.StartedAt, TimeSpan.Zero);

            var completedAtOffset = summary.CompletedAtOffset
                ?? (summary.CompletedAt.HasValue
                    ? new DateTimeOffset(summary.CompletedAt.Value, TimeSpan.Zero)
                    : (DateTimeOffset?)null);
#pragma warning restore CS0618

            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = runGuid,
                WorkItemId = null, // Legacy/migrated runs have no work item
                IssueIdentifier = summary.IssueIdentifier,
                IssueTitle = summary.IssueTitle,
                FinalStep = summary.FinalStep,
                StartedAt = startedAtOffset,
                CompletedAt = completedAtOffset,
                RetryCount = summary.RetryCount,
                PullRequestUrl = summary.PullRequestUrl,
                ModelName = summary.ModelName,
                AgentId = summary.AgentId,
                ProjectName = summary.ProjectName,
                RunType = summary.RunType
            });

            counts.PipelineRuns++;
        }
    }

    private static T DeserializeOrThrow<T>(string json, string filePath)
    {
        try
        {
            var result = JsonSerializer.Deserialize<T>(json, JsonOptions);
            if (result is null)
                throw new JsonException($"Deserialized to null from file: {filePath}");
            return result;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"ConfigMigration: Failed to parse JSON file '{filePath}': {ex.Message}", ex);
        }
    }

    private static JsonDocument SerializeToDocument<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        return JsonDocument.Parse(json);
    }

    private static bool HasAnyJsonFiles(string directory)
    {
        return Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories).Any();
    }

    private sealed class MigrationCounts
    {
        public int PipelineConfig;
        public int ProviderConfigs;
        public int AgentProfiles;
        public int QualityGates;
        public int Reviewers;
        public int Projects;
        public int Templates;
        public int ConsolidationRuns;
        public int PipelineRuns;
    }
}
