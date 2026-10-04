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

    private const string MigrationLockKey = "caa_schema_migration";
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
            _logger.Information("Database:MigrateOnStartup is true — acquiring migration lock");

            await using var lockHandle = await _lockProvider.AcquireAsync(MigrationLockKey, ct);

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();

            if (pending.Count == 0)
            {
                _logger.Information("Database schema is current — no pending migrations");
                return;
            }

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
                var message = $"Database has {pending.Count} pending migration(s): {string.Join(", ", pending)}. " +
                              "Apply migrations via Helm pre-upgrade hook or set Database:MigrateOnStartup=true.";
                _logger.Error(message);
                throw new InvalidOperationException(message);
            }

            _logger.Information("Database schema verification passed — no pending migrations");
        }
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
}

/// <summary>
/// Abstraction for database connectivity probing — allows testability without real DB.
/// </summary>
public interface IDatabaseProbe
{
    Task ProbeAsync(CancellationToken ct);
}
