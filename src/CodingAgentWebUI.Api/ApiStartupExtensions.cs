using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgentWebUI.Api;

/// <summary>
/// Extension methods for database startup and health endpoints.
/// </summary>
internal static class ApiStartupExtensions
{
    /// <summary>
    /// Runs database migration verification before app.Run().
    /// Calls <see cref="DatabaseStartupService.HandleMigrationsAsync"/> only —
    /// NEVER <c>ImportJsonConfigIfNeededAsync</c> (legacy JSON migration).
    /// Honours <c>Database:SkipStartupInit</c> (Req 4.3) for integration tests.
    /// The API runs with <c>MigrateOnStartup=false</c> so this method VERIFIES and
    /// THROWS on pending migrations rather than applying them (Req 9.5a/9.5b).
    /// </summary>
    public static async Task RunApiMigrationsAsync(
        this WebApplication app,
        IConfiguration configuration)
    {
        if (configuration.GetValue<bool>("Database:SkipStartupInit"))
        {
            Log.Warning("Database:SkipStartupInit is true — skipping database initialization. " +
                        "Only use this in integration test environments.");
            return;
        }

        var connectionString = DatabaseConnectionResolver.Resolve(configuration);
        if (string.IsNullOrEmpty(connectionString))
            return;

        var dbFactory = app.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        var lockProvider = app.Services.GetRequiredService<IDistributedLockProvider>();
        var probe = app.Services.GetService<IDatabaseProbe>();

        var startupService = new DatabaseStartupService(
            dbFactory, lockProvider, configuration, Log.Logger, probe);

        // WaitForDatabaseConnectionAsync + HandleMigrationsAsync only.
        // Do NOT call InitializeAsync — that also calls ImportJsonConfigIfNeededAsync.
        await startupService.WaitForDatabaseConnectionAsync(CancellationToken.None);
        await startupService.HandleMigrationsAsync(CancellationToken.None);
    }

    /// <summary>
    /// Maps <c>/healthz</c> (liveness) and <c>/readyz</c> (readiness) probes.
    /// Both are anonymous.
    /// </summary>
    public static IEndpointRouteBuilder MapApiHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // Liveness: Is the process alive?
        endpoints.MapGet("/healthz", () =>
            Results.Ok(new { status = "ok" })).AllowAnonymous();

        // Readiness: Can this pod serve traffic?
        // TODO(Spec 043): Wire up a real DB probe when DatabaseHealthState is accessible from the API
        endpoints.MapGet("/readyz", () =>
            Results.Ok(new { status = "ready" })).AllowAnonymous();

        return endpoints;
    }
}
