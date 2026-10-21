using System.Text.Json;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CodingAgent.Infrastructure;

/// <summary>
/// Handles database initialization at startup:
/// - Connection retry with exponential backoff (2s → 30s, max 10 attempts)
/// - Auto-migration with distributed lock when Database:MigrateOnStartup is true
/// - Schema verification in production mode (fail if pending migrations)
/// </summary>
public sealed class DatabaseStartupService
{
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly IDistributedLockProvider _lockProvider;
    private readonly IConfiguration _configuration;
    private readonly Serilog.ILogger _logger;
    private readonly IDatabaseProbe? _probe;
    private readonly TimeProvider _timeProvider;

    internal const string MigrationLockKey = "caa_schema_migration";

    /// <summary>
    /// ID of the baseline migration that replaced the original migrations in October 2026. It reuses the ID of
    /// the newest original migration, so a database that applied every original migration has nothing pending.
    /// </summary>
    internal const string BaselineMigrationId = "20260930213632_DropConsolidationRuns";

    internal const int MaxRetryAttempts = 10;
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    public DatabaseStartupService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        IDistributedLockProvider lockProvider,
        IConfiguration configuration,
        Serilog.ILogger logger,
        IDatabaseProbe? probe = null,
        TimeProvider? timeProvider = null)
    {
        _dbFactory = dbFactory;
        _lockProvider = lockProvider;
        _configuration = configuration;
        _logger = logger;
        _probe = probe;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Retries DB connection with exponential backoff: 2s → 4s → 8s → 16s → 30s (capped), max 10 attempts.
    /// </summary>
    public async Task WaitForDatabaseConnectionAsync(CancellationToken ct)
    {
        var delay = InitialDelay;

        for (var attempt = 1; attempt <= MaxRetryAttempts; attempt++)
        {
            try
            {
                if (_probe is not null)
                {
                    await _probe.ProbeAsync(ct);
                }
                else
                {
                    await using var db = await _dbFactory.CreateDbContextAsync(ct);
                    await db.Database.ExecuteSqlRawAsync("SELECT 1", ct);
                }

                _logger.Information("Database connection established on attempt {Attempt}", attempt);
                return;
            }
            catch (Exception ex) when (attempt < MaxRetryAttempts && ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "Database connection attempt {Attempt}/{Max} failed. Retrying in {Delay}s",
                    attempt, MaxRetryAttempts, delay.TotalSeconds);

                await Task.Delay(delay, _timeProvider, ct);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxDelay.TotalSeconds));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Database connection failed after {Max} attempts. Startup aborted", MaxRetryAttempts);
                throw new InvalidOperationException(
                    $"Failed to connect to database after {MaxRetryAttempts} attempts. Last error: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// When MigrateOnStartup is true (default for dev): acquires lock, applies migrations.
    /// When false (production): verifies no pending migrations, fails if any exist.
    /// Non-relational providers (e.g. InMemory used in tests) are skipped — they have no migrations.
    /// </summary>
    public async Task HandleMigrationsAsync(CancellationToken ct)
    {
        // Non-relational providers (InMemory, etc.) do not support migration history queries.
        // Skip the migration check entirely — schema is managed by EnsureCreated in those environments.
        await using (var dbCheck = await _dbFactory.CreateDbContextAsync(ct))
        {
            if (!dbCheck.Database.IsRelational())
                return;
        }

        var migrateOnStartup = _configuration.GetValue("Database:MigrateOnStartup", true);

        if (migrateOnStartup)
        {
            _logger.Information("Database:MigrateOnStartup is true — acquiring migration lock"); // NOSONAR S6664 — one log per migration step

            await using var lockHandle = await _lockProvider.AcquireAsync(MigrationLockKey, ct);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();

            if (pending.Count == 0)
            {
                _logger.Information("Database schema is current — no pending migrations");
                return;
            }

            await EnsureNotBehindBaselineAsync(db, pending, ct);

            _logger.Information("Applying {Count} pending migration(s): {Migrations}",
                pending.Count, string.Join(", ", pending));

            await db.Database.MigrateAsync(ct);

            _logger.Information("Database migrations applied successfully");
        }
        else
        {
            // Production mode: verify schema is current
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();

            if (pending.Count > 0)
            {
                await EnsureNotBehindBaselineAsync(db, pending, ct);

                var message = $"Database has {pending.Count} pending migration(s): {string.Join(", ", pending)}. " +
                              "Apply migrations via Helm pre-upgrade hook or set Database:MigrateOnStartup=true.";
                _logger.Error(message);
                throw new InvalidOperationException(message);
            }

            _logger.Information("Database schema verification passed — no pending migrations");
        }
    }

    /// <summary>
    /// The baseline creates the whole schema, so it can only run on an empty database. A database that applied
    /// some but not all of the original migrations needs a build from before the squash to finish them first.
    /// </summary>
    private async Task EnsureNotBehindBaselineAsync(PipelineDbContext db, List<string> pending, CancellationToken ct)
    {
        if (!pending.Contains(BaselineMigrationId))
            return;

        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToList();
        if (applied.Count == 0)
            return;

        var message = $"The database stopped at migration {applied[^1]}, before {BaselineMigrationId}. " +
                      "The original migrations were squashed into one baseline, which only applies to an empty database. " +
                      "Deploy a build from before the squash first so it applies the remaining migrations, then upgrade.";
        _logger.Error(message);
        throw new InvalidOperationException(message);
    }

    /// <summary>
    /// Runs the full post-migration seed-and-repair sequence:
    /// <list type="number">
    ///   <item>Ensures the Default project row exists (<see cref="SeedDefaultProjectIfNeededAsync"/>).</item>
    ///   <item>Seeds the default reviewer configurations when the table is empty (<see cref="SeedDefaultReviewerConfigsIfNeededAsync"/>).</item>
    ///   <item>Reparents orphaned templates to the Default project (<see cref="ClaimOrphanedTemplatesAsync"/>).</item>
    /// </list>
    /// Idempotent — safe to run on every startup. Step 1 must precede step 3 because
    /// <see cref="ClaimOrphanedTemplatesAsync"/> skips repair when the Default project is absent.
    /// </summary>
    public async Task RunStartupSeedingAsync(CancellationToken ct)
    {
        // Each step holds MigrationLockKey around both its existence check and its write, so concurrent
        // replicas serialize per step: a second replica's check runs only after the first replica's write
        // has committed. The sequence as a whole is not atomic, but every interleaving is safe, because each
        // step re-reads under the lock and is idempotent. StartupSeedingConcurrencyTests races two replicas
        // against real Postgres; without the lock, the Default project insert fails with a PK violation.
        // MigrationLockKey is non-reentrant (Postgres: pg_try_advisory_lock on a new connection per acquire;
        // InProcess: SemaphoreSlim(1,1)), so nesting an outer AcquireAsync around these calls deadlocks
        // (InProcess) or times out after 60 s (Postgres).
        await SeedDefaultProjectIfNeededAsync(ct);
        await SeedDefaultReviewerConfigsIfNeededAsync(ct);
        await ClaimOrphanedTemplatesAsync(ct);
        await MoveRepositorySettingsToTemplatesAsync(ct);
    }

    /// <summary>
    /// Inserts the Default project row (<see cref="WellKnownIds.DefaultProjectId"/>) if it does not
    /// already exist. Idempotent — no-op on every startup after the first.
    /// Acquires the schema-migration advisory lock to prevent duplicate inserts in multi-replica deployments.
    /// </summary>
    internal async Task SeedDefaultProjectIfNeededAsync(CancellationToken ct)
    {
        await using var lockHandle = await _lockProvider.AcquireAsync(MigrationLockKey, ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var defaultGuid = Guid.Parse(WellKnownIds.DefaultProjectId);
        var exists = await db.Projects.AnyAsync(p => p.Id == defaultGuid, ct);
        if (exists)
            return;

        db.Projects.Add(new ProjectEntity
        {
            Id = defaultGuid,
            Name = "Default",
            Enabled = true
        });

        await db.SaveChangesAsync(ct);

        _logger.Information("Default project row was absent — created Default project (ID {Id})", defaultGuid);
    }

    /// <summary>
    /// Seeds <see cref="PipelineConfigurationDefaults.DefaultReviewerConfigurations"/> into the
    /// <c>ReviewerConfigs</c> table if it is empty. Idempotent — skips if any reviewer config exists.
    /// Acquires the schema-migration advisory lock to prevent duplicate seeding in multi-replica deployments.
    /// </summary>
    internal async Task SeedDefaultReviewerConfigsIfNeededAsync(CancellationToken ct)
    {
        await using var lockHandle = await _lockProvider.AcquireAsync(MigrationLockKey, ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var hasReviewers = await db.ReviewerConfigs.AnyAsync(ct);
        if (hasReviewers)
        {
            return;
        }

        var defaults = PipelineConfigurationDefaults.DefaultReviewerConfigurations;
        // TODO: If DefaultReviewerConfigurations is empty, SaveChangesAsync is still called and
        // the log line fires with "seeding 0 default reviewer configuration(s)", silently leaving
        // the table empty while implying seeding occurred. Add a guard: if (defaults.Count == 0) return;
        foreach (var config in defaults)
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

        _logger.Information(
            "ReviewerConfigs table was empty — seeding {Count} default reviewer configuration(s)",
            defaults.Count);
    }

    private static string SerializeToJson<T>(T value) =>
        JsonSerializer.Serialize(value, PipelineJsonOptions.Default);

    /// <summary>
    /// Finds templates whose project no longer exists and moves them to the Default project, so every
    /// template belongs to a project. A template's own project is the only membership record.
    /// Idempotent — safe to run at every startup.
    /// Acquires the schema-migration advisory lock to prevent concurrent replicas from both loading
    /// the same orphaned templates and racing on SaveChangesAsync (which would throw
    /// DbUpdateConcurrencyException on the RowVersion concurrency token and abort one replica's startup).
    /// </summary>
    internal async Task ClaimOrphanedTemplatesAsync(CancellationToken ct)
    {
        await using var lockHandle = await _lockProvider.AcquireAsync(MigrationLockKey, ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var allTemplates = await db.PipelineJobTemplates.ToListAsync(ct);
        if (allTemplates.Count == 0)
            return;

        var projectIds = (await db.Projects.Select(p => p.Id).ToListAsync(ct)).ToHashSet();
        var defaultGuid = Guid.Parse(WellKnownIds.DefaultProjectId);

        if (!projectIds.Contains(defaultGuid))
        {
            _logger.Warning("ClaimOrphanedTemplatesAsync: Default project not found — skipping orphan repair");
            return;
        }

        var repairedCount = 0;
        foreach (var template in allTemplates.Where(t => !projectIds.Contains(t.ProjectId)))
        {
            template.ProjectId = defaultGuid;

            _logger.Information(
                "ClaimOrphanedTemplatesAsync: reparented orphaned template {TemplateId} to Default project",
                template.Id);

            repairedCount++;
        }

        if (repairedCount > 0)
        {
            await db.SaveChangesAsync(ct);
            _logger.Information(
                "ClaimOrphanedTemplatesAsync: repaired {Count} orphaned template(s)",
                repairedCount);
        }
    }

    /// <summary>
    /// Moves repository settings (RequiredLabels, Secrets, SetupSteps, SteeringContent, BlacklistedPaths)
    /// from repository <see cref="ProviderConfig"/> records onto the templates that reference them.
    /// This is a one-time data migration for the issue #3575 change where those fields moved from the
    /// repository connection to the pipeline job template. Idempotent: skips fields already set on the
    /// template; only clears from the provider once all templates have been updated.
    /// A provider referenced by no template keeps its values and emits a warning.
    /// Acquires the schema-migration advisory lock to prevent concurrent replicas from racing.
    /// </summary>
    internal async Task MoveRepositorySettingsToTemplatesAsync(CancellationToken ct)
    {
        await using var lockHandle = await _lockProvider.AcquireAsync(MigrationLockKey, ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await ApplyRepositorySettingsMigrationAsync(db, _logger, ct);
    }

    /// <summary>
    /// Core migration body: moves the five repository-settings fields
    /// (RequiredLabels, Secrets, SetupSteps, SteeringContent, BlacklistedPaths)
    /// from <see cref="ProviderConfig"/> records onto the
    /// <see cref="PipelineJobTemplate"/> records that reference them.
    /// Shared between <see cref="MoveRepositorySettingsToTemplatesAsync"/> (startup)
    /// and <see cref="CodingAgent.Api.ConfigEndpoints"/> (post-import).
    /// Idempotent: fields already set on a template are not overwritten; the provider
    /// is cleared only after every referencing template has been updated successfully.
    /// A provider referenced by no template keeps its values and emits a warning.
    /// </summary>
    public static async Task ApplyRepositorySettingsMigrationAsync(
        PipelineDbContext db,
        Serilog.ILogger logger,
        CancellationToken ct)
    {
        // TODO: [WARNING] db and logger have no ArgumentNullException.ThrowIfNull guards; a null
        // db causes NullReferenceException on the first db.ProviderConfigs access and a null logger
        // causes NullReferenceException on the first logger.Warning(...) call inside the loop.
        // Add ThrowIfNull guards for both parameters (DotNetSpecialist finding).
        var allProviderEntities = await db.ProviderConfigs
            .Where(p => p.Kind == ProviderKind.Repository)
            .ToListAsync(ct);

        if (allProviderEntities.Count == 0)
            return;

        var allTemplateEntities = await db.PipelineJobTemplates.ToListAsync(ct);

        var anyChanged = false;

        foreach (var providerEntity in allProviderEntities)
        {
            if (string.IsNullOrEmpty(providerEntity.Configuration))
                continue;

            var provider = JsonSerializer.Deserialize<ProviderConfig>(
                providerEntity.Configuration, PipelineJsonOptions.Lenient);
            if (provider is null)
                continue;

            var hasSettings = provider.RequiredLabels is { Count: > 0 }
                || provider.Secrets is { Count: > 0 }
                || provider.SetupSteps is { Count: > 0 }
                || !string.IsNullOrEmpty(provider.SteeringContent)
                || provider.BlacklistedPaths is { Count: > 0 };

            if (!hasSettings)
                continue;

            // Match templates by the provider's Id field in the JSON (the logical identifier
            // used in the template's RepoProviderId field), not by the entity's GUID key.
            var referencingEntities = allTemplateEntities
                .Where(t =>
                {
                    if (string.IsNullOrEmpty(t.Configuration)) return false;
                    var tmpl = JsonSerializer.Deserialize<PipelineJobTemplate>(
                        t.Configuration, PipelineJsonOptions.Lenient);
                    return tmpl?.RepoProviderId == provider.Id;
                })
                .ToList();

            if (referencingEntities.Count == 0)
            {
                logger.Warning(
                    "ApplyRepositorySettingsMigration: provider '{DisplayName}' ({Id}) has repository settings " +
                    "but no template references it; values kept",
                    provider.DisplayName, provider.Id);
                continue;
            }

            // Copy fields onto each referencing template whose own value is null/empty.
            // Track whether every template was successfully updated before clearing the provider.
            var allUpdated = true;
            foreach (var templateEntity in referencingEntities)
            {
                var template = JsonSerializer.Deserialize<PipelineJobTemplate>(
                    templateEntity.Configuration!, PipelineJsonOptions.Lenient);
                if (template is null)
                {
                    allUpdated = false;
                    continue;
                }

                var needsUpdate =
                    (provider.RequiredLabels is { Count: > 0 } && template.RequiredLabels is not { Count: > 0 })
                    || (provider.Secrets is { Count: > 0 } && template.Secrets is not { Count: > 0 })
                    || (provider.SetupSteps is { Count: > 0 } && template.SetupSteps is not { Count: > 0 })
                    || (!string.IsNullOrEmpty(provider.SteeringContent) && string.IsNullOrEmpty(template.SteeringContent))
                    || (provider.BlacklistedPaths is { Count: > 0 } && template.BlacklistedPaths is not { Count: > 0 });

                if (!needsUpdate)
                    continue;

                var updated = template with
                {
                    RequiredLabels = template.RequiredLabels is { Count: > 0 } ? template.RequiredLabels : provider.RequiredLabels,
                    Secrets = template.Secrets is { Count: > 0 } ? template.Secrets : provider.Secrets,
                    SetupSteps = template.SetupSteps is { Count: > 0 } ? template.SetupSteps : provider.SetupSteps,
                    SteeringContent = !string.IsNullOrEmpty(template.SteeringContent) ? template.SteeringContent : provider.SteeringContent,
                    BlacklistedPaths = template.BlacklistedPaths is { Count: > 0 } ? template.BlacklistedPaths : provider.BlacklistedPaths,
                };

                templateEntity.Configuration = SerializeToJson(updated);
                anyChanged = true;

                logger.Information(
                    "ApplyRepositorySettingsMigration: copied repository settings from provider '{ProviderDisplayName}' ({ProviderId}) to template '{TemplateName}' ({TemplateId})",
                    provider.DisplayName, provider.Id, template.Name, template.Id);
            }

            if (!allUpdated)
            {
                logger.Warning(
                    "ApplyRepositorySettingsMigration: provider '{DisplayName}' ({Id}) has repository settings but some referencing templates could not be deserialized; provider values kept",
                    provider.DisplayName, provider.Id);
                continue;
            }

            // Clear the five fields from the provider only after all templates have been updated.
            var cleared = new ProviderConfig
            {
                BlacklistedPaths = null,
                DisplayName = provider.DisplayName,
                Id = provider.Id,
                Kind = provider.Kind,
                ProviderType = provider.ProviderType,
                RepositoryRole = provider.RepositoryRole,
                RequiredLabels = null,
                Secrets = null,
                Settings = provider.Settings,
                SetupSteps = null,
                SteeringContent = null,
            };
            providerEntity.Configuration = SerializeToJson(cleared);
            anyChanged = true;

            logger.Information(
                "ApplyRepositorySettingsMigration: cleared repository settings from provider '{DisplayName}' ({Id})",
                provider.DisplayName, provider.Id);
        }

        if (anyChanged)
            await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Abstraction for database connectivity probing — allows testability without real DB.
/// </summary>
public interface IDatabaseProbe
{
    Task ProbeAsync(CancellationToken ct);
}
