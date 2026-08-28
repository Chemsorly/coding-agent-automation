using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Locking;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Serilog;
using StackExchange.Redis;

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
        // Also checks Redis connectivity when backplane is configured — a process that cannot
        // reach Redis will silently drop hub messages in multi-replica mode.
        endpoints.MapGet("/healthz", async ([FromServices] IConnectionMultiplexer? redis) =>
        {
            if (redis is not null)
            {
                try { await redis.GetDatabase().PingAsync(); }
                catch
                {
                    return Results.Json(
                        new { status = "unhealthy", reason = "redis_ping_failed" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }
            return Results.Ok(new { status = "ok" });
        }).AllowAnonymous();

        // Readiness: Can this pod serve traffic?
        // Returns 503 when:
        //   • DatabaseHealthState reports DB unreachable, OR
        //   • An IConnectionMultiplexer is registered (Redis backplane configured) and it is
        //     not currently connected. A disconnected backplane causes silent message loss in
        //     multi-replica deployments — hub broadcasts succeed locally but are never fanned out.
        endpoints.MapGet("/readyz", (
                [FromServices] DatabaseHealthState dbHealth,
                [FromServices] IConnectionMultiplexer? redis) =>
            {
                if (!dbHealth.IsDatabaseHealthy)
                    return Results.Json(
                        new { status = "unhealthy", reason = "database_unreachable" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);

                if (redis is not null && !redis.IsConnected)
                    return Results.Json(
                        new { status = "unhealthy", reason = "redis_backplane_disconnected" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);

                return Results.Ok(new { status = "ready" });
            })
            .AllowAnonymous();

        return endpoints;
    }

    /// <summary>
    /// Registers observable gauges for agent metrics against the <see cref="PipelineTelemetry.Meter"/>.
    /// Agents register on the API hub, so this is the correct process to own these gauges.
    /// </summary>
    public static WebApplication RegisterApiObservableGauges(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var agentRegistry = app.Services.GetRequiredService<IAgentRegistryService>();

        _ = PipelineTelemetry.Meter.CreateObservableGauge("agent.jobs.active",
            () => agentRegistry.GetBusyAgentCount(), "{job}", "Currently executing agent jobs");
        _ = PipelineTelemetry.Meter.CreateObservableGauge("agent.connections.total",
            () => agentRegistry.GetTotalAgentCount(), "{connection}", "Total registered agents");

        return app;
    }
}
