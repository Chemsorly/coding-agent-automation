using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using Microsoft.EntityFrameworkCore;

namespace CodingAgentWebUI.Services;

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

    private const string MigrationLockKey = "caa_schema_migration";
    internal const int MaxRetryAttempts = 10;
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    public DatabaseStartupService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        IDistributedLockProvider lockProvider,
        IConfiguration configuration,
        Serilog.ILogger logger,
        IDatabaseProbe? probe = null)
    {
        _dbFactory = dbFactory;
        _lockProvider = lockProvider;
        _configuration = configuration;
        _logger = logger;
        _probe = probe;
    }

    /// <summary>
    /// Validates DB connectivity, applies/verifies migrations, and imports JSON config if DB is empty.
    /// Throws on unrecoverable failure (caller should prevent app startup).
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        await WaitForDatabaseConnectionAsync(ct);
        await HandleMigrationsAsync(ct);
        await ImportJsonConfigIfNeededAsync(ct);
    }

    /// <summary>
    /// Retries DB connection with exponential backoff: 2s → 4s → 8s → 16s → 30s (capped), max 10 attempts.
    /// </summary>
    internal async Task WaitForDatabaseConnectionAsync(CancellationToken ct)
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

                await Task.Delay(delay, ct);
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
    /// </summary>
    internal async Task HandleMigrationsAsync(CancellationToken ct)
    {
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
    /// If the database is empty (no PipelineConfig row), imports configuration from JSON files.
    /// This enables seamless transition from Legacy (JSON) mode to DB mode.
    /// </summary>
    internal async Task ImportJsonConfigIfNeededAsync(CancellationToken ct, string? configBasePath = null)
    {
        var migrationService = new ConfigMigrationService(_dbFactory, _lockProvider,
            configBasePath ?? CodingAgentWebUI.Pipeline.Models.PipelineConstants.ConfigBaseDirectory);
        var migrated = await migrationService.MigrateIfNeededAsync(ct);

        if (migrated)
        {
            _logger.Information("JSON config imported into database successfully");
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
