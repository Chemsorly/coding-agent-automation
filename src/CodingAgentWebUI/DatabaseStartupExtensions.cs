using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for database startup initialization, health checks, and resilience wiring.
/// Registers DatabaseStartupService, DatabaseReadinessMonitor, and DatabaseHealthState.
/// </summary>
public static class DatabaseStartupExtensions
{
    /// <summary>
    /// Registers database health monitoring services when a DB connection string is configured.
    /// Must be called during service registration (before Build).
    /// </summary>
    public static IServiceCollection AddDatabaseHealthServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = Services.DatabaseConnectionResolver.Resolve(configuration);
        if (string.IsNullOrEmpty(connectionString))
            return services; // Legacy mode — no DB health monitoring needed

        var isProduction = !string.Equals(
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"),
            "Development",
            StringComparison.OrdinalIgnoreCase);

        // Normalize connection string (Timeout=15, SslMode=Require for production)
        var normalizedConnectionString = DatabaseReadinessMonitor.NormalizeConnectionString(
            connectionString, isProduction);

        services.AddSingleton<DatabaseHealthState>();
        services.AddSingleton(sp => new DatabaseReadinessMonitor(
            sp.GetRequiredService<DatabaseHealthState>(),
            normalizedConnectionString,
            Log.Logger));
        services.AddHostedService(sp => sp.GetRequiredService<DatabaseReadinessMonitor>());

        return services;
    }

    /// <summary>
    /// Runs database startup initialization: connection retry, migration/verification.
    /// Must be called after Build() but before Run() — blocks until DB is ready.
    /// Only executes in DB mode (connection string configured).
    /// Skips initialization when <c>Database:SkipStartupInit</c> is true (for integration tests).
    /// </summary>
    public static async Task InitializeDatabaseAsync(this WebApplication app)
    {
        var connectionString = Services.DatabaseConnectionResolver.Resolve(app.Configuration);
        if (string.IsNullOrEmpty(connectionString))
            return; // Legacy mode — nothing to initialize

        if (app.Configuration.GetValue<bool>("Database:SkipStartupInit"))
        {
            Log.Warning("Database:SkipStartupInit is true — skipping database initialization. " +
                        "This should only be used in integration test environments");
            return; // Test mode — skip DB initialization
        }

        var dbFactory = app.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        var lockProvider = app.Services.GetRequiredService<IDistributedLockProvider>();
        var configuration = app.Services.GetRequiredService<IConfiguration>();
        var probe = app.Services.GetService<IDatabaseProbe>();

        var startupService = new DatabaseStartupService(
            dbFactory, lockProvider, configuration, Log.Logger, probe);

        await startupService.InitializeAsync(CancellationToken.None);
    }
}
