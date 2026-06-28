using CodingAgentWebUI;
using CodingAgentWebUI.Components;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Infrastructure.Telemetry;
using CodingAgentWebUI.Models;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;

// ── CLI command: export-config ──────────────────────────────────────────────
if (args.Length >= 1 && args[0] == "export-config")
{
    var outputArg = args.FirstOrDefault(a => a.StartsWith("--output="))
        ?? args.FirstOrDefault(a => a == "--output");

    string? outputDir = null;
    if (outputArg is not null && outputArg.StartsWith("--output="))
    {
        outputDir = outputArg["--output=".Length..];
    }
    else if (outputArg == "--output")
    {
        var idx = Array.IndexOf(args, "--output");
        if (idx + 1 < args.Length)
            outputDir = args[idx + 1];
    }

    if (string.IsNullOrWhiteSpace(outputDir))
    {
        Console.Error.WriteLine("Usage: dotnet run -- export-config --output /path/to/dir");
        return;
    }

    // Initialize minimal Serilog for CLI mode
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.Console(theme: Serilog.Sinks.SystemConsole.Themes.ConsoleTheme.None)
        .CreateLogger();

    var connectionString = Environment.GetEnvironmentVariable("Database__ConnectionString")
        ?? new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
            .AddEnvironmentVariables()
            .Build()
            .GetValue<string>("Database:ConnectionString");

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Log.Error("export-config requires Database:ConnectionString to be configured");
        return;
    }

    var services = new ServiceCollection();
    services.AddPooledDbContextFactory<PipelineDbContext>(o => o.UseNpgsql(connectionString));
    services.AddSingleton<ConfigExportService>();

#pragma warning disable ASP0000 // Intentional: CLI command uses isolated DI container, not the web host
    await using var sp = services.BuildServiceProvider();
#pragma warning restore ASP0000
    var exportService = sp.GetRequiredService<ConfigExportService>();

    Directory.CreateDirectory(outputDir);
    await exportService.ExportAsync(outputDir, CancellationToken.None);

    Log.Information("Export complete: {OutputDir}", outputDir);
    return;
}

var builder = WebApplication.CreateBuilder(args);

// Register services
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton(BuildInfo.Load());

// Host shutdown timeout: drain delay (15s) + ShutdownService timeout (15s) + buffer (10s) = 40s
builder.Services.Configure<HostOptions>(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(40));

// Pipeline — Configuration Store (created eagerly to load config before DI container is built)
var configStore = new JsonConfigurationStore(PipelineConstants.ConfigBaseDirectory);
var pipelineConfig = await configStore.LoadPipelineConfigAsync(CancellationToken.None);

// Domain service registrations (extracted into focused extension methods)
var dbConnectionString = builder.Configuration.GetValue<string>("Database:ConnectionString");
if (string.IsNullOrEmpty(dbConnectionString))
{
    // Legacy mode: JSON-based config store
    builder.Services.AddInfrastructureServices(configStore, pipelineConfig);
}
else
{
    // DB mode: infrastructure services without config store (handled by AddWorkDistribution)
    builder.Services.AddInfrastructureServicesWithoutConfigStore();
}
builder.Services.AddPipelineServices(Serilog.Log.Logger);
builder.Services.AddPipelineCoreServices();
builder.Services.AddOrchestrationServices(pipelineConfig,
    string.IsNullOrEmpty(dbConnectionString) ? null : (builder.Configuration.GetValue<string>("WorkDistribution:Mode") ?? "SignalR"));
builder.Services.AddConsolidationServices(pipelineConfig);
builder.Services.AddWorkDistribution(builder.Configuration);
builder.Services.AddDatabaseHealthServices(builder.Configuration);

// Page-level services (scoped — one instance per Blazor circuit)
builder.Services.AddScoped<CodingAgentWebUI.Services.AgentCodingPageService>();
builder.Services.AddScoped<CodingAgentWebUI.Services.NotificationService>();

// SignalR — hub services with MessagePack protocol
builder.Services.AddSignalR()
    .AddMessagePackProtocol();

// SignalR — hub filter for agent authorization
builder.Services.AddSingleton<IHubFilter>(sp => new AgentAuthorizationFilter(
    sp.GetRequiredService<AgentRegistryService>(),
    Serilog.Log.Logger));

// Agent API key authentication — NOT set as default scheme to avoid interfering with Blazor UI.
// When only one scheme is registered, ASP.NET Core auto-promotes it to the default scheme,
// causing UseAuthentication() to challenge every request (health checks, Blazor, static files).
// Explicitly clear the defaults so only endpoints with RequireAuthorization("AgentApiKey") trigger it.
var agentApiKey = AgentApiKeyAuthHandler.ResolveApiKey(Serilog.Log.Logger);
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = null;
        options.DefaultChallengeScheme = null;
    })
    .AddScheme<AgentApiKeyAuthOptions, AgentApiKeyAuthHandler>(
        AgentApiKeyDefaults.AuthenticationScheme,
        options => options.ApiKey = agentApiKey);
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AgentApiKey", policy =>
        policy.AddAuthenticationSchemes(AgentApiKeyDefaults.AuthenticationScheme)
              .RequireAuthenticatedUser());
});

// Configure Serilog
var orchestratorLogLevel = Environment.GetEnvironmentVariable("LOG_LEVEL")?.ToLowerInvariant() switch
{
    "debug" or "dbg" => Serilog.Events.LogEventLevel.Debug,
    "verbose" or "trace" => Serilog.Events.LogEventLevel.Verbose,
    "warning" or "warn" => Serilog.Events.LogEventLevel.Warning,
    "error" or "err" => Serilog.Events.LogEventLevel.Error,
    _ => Serilog.Events.LogEventLevel.Information
};
builder.Host.UseSerilog((ctx, lc) => lc
    .MinimumLevel.Is(orchestratorLogLevel)
    // Suppress noisy ASP.NET Core framework logging (health checks, static files, Blazor negotiation, auth)
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithSpan()
    .WriteTo.Console(theme: Serilog.Sinks.SystemConsole.Themes.ConsoleTheme.None)
    .WriteToOtlpIfConfigured("coding-agent-orchestrator", ctx.HostingEnvironment.EnvironmentName));

// Configure OpenTelemetry (tracing + metrics)
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: "coding-agent-orchestrator",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource(PipelineTelemetry.SourceName)
            .AddSource("Microsoft.AspNetCore.SignalR.Server")
            .AddOtlpExporter();

        // DB mode: Npgsql tracing for query spans
        if (!string.IsNullOrEmpty(dbConnectionString))
            t.AddSource("Npgsql");

        // Redis backplane: trace Redis commands
        var redisConn = builder.Configuration.GetValue<string>("SignalR:Redis:ConnectionString");
        if (!string.IsNullOrEmpty(redisConn))
            t.AddSource("StackExchange.Redis");
    })
    .WithMetrics(m =>
    {
        m.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddMeter(PipelineTelemetry.SourceName)
            .AddOtlpExporter();

        // Work distribution metrics (035a)
        if (!string.IsNullOrEmpty(dbConnectionString))
            m.AddMeter(CodingAgentWebUI.Orchestration.Telemetry.WorkDistributionTelemetry.MeterName);
    });

var app = builder.Build();

// Database startup: connection retry + migration/verification (blocks until ready)
await app.InitializeDatabaseAsync();

// Register workitems_by_status observable gauge callback (DB-backed, cached).
// Uses a periodic timer instead of synchronous DB query in the metrics collection thread.
if (!string.IsNullOrEmpty(dbConnectionString))
{
    var dbFactory = app.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
    IEnumerable<System.Diagnostics.Metrics.Measurement<long>> cachedMeasurements = [];
    var updateTimer = new Timer(async _ =>
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var counts = await db.WorkItems
                .GroupBy(w => new { w.Status, w.AgentSelector })
                .Select(g => new { g.Key.Status, g.Key.AgentSelector, Count = g.LongCount() })
                .ToListAsync();
            cachedMeasurements = counts.Select(c => new System.Diagnostics.Metrics.Measurement<long>(c.Count,
                new KeyValuePair<string, object?>("status", c.Status.ToString()),
                new KeyValuePair<string, object?>("agent_selector", c.AgentSelector)));
        }
        catch { cachedMeasurements = []; }
    }, null, TimeSpan.Zero, TimeSpan.FromSeconds(10));

    CodingAgentWebUI.Orchestration.Telemetry.WorkDistributionTelemetry.RegisterWorkItemsByStatusCallback(() => cachedMeasurements);
}

// Register observable gauges for dispatch queue and agent concurrency metrics
var dispatcher = app.Services.GetRequiredService<JobDispatcherService>();
var agentRegistry = app.Services.GetRequiredService<AgentRegistryService>();
_ = PipelineTelemetry.Meter.CreateObservableGauge("dispatch.queue.depth",
    () => dispatcher.QueueLength, "{item}", "Jobs waiting for available agent");
_ = PipelineTelemetry.Meter.CreateObservableGauge("agent.jobs.active",
    () => agentRegistry.GetBusyAgentCount(), "{job}", "Currently executing agent jobs");
_ = PipelineTelemetry.Meter.CreateObservableGauge("agent.connections.total",
    () => agentRegistry.GetAllAgents().Count, "{connection}", "Total registered agents");

// Graceful shutdown is handled by ShutdownService (IHostedLifecycleService)
// — async, with 15s timeout, non-blocking (Req 12)

// Kubernetes-style health probes — anonymous, no auth required
app.MapGet("/healthz", async (CancellationToken ct) =>
{
    var dbMonitor = app.Services.GetService<CodingAgentWebUI.Services.DatabaseReadinessMonitor>();
    if (dbMonitor is not null)
    {
        var dbHealthy = await dbMonitor.CheckHealthAsync(ct);
        if (!dbHealthy)
            return Results.Json(new { status = "unhealthy", reason = "database_unreachable", timestamp = DateTime.UtcNow }, statusCode: 503);
    }
    return Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
})
    .AllowAnonymous();
app.MapGet("/readyz", (HttpContext httpContext) =>
{
    var readiness = httpContext.RequestServices.GetRequiredService<CodingAgentWebUI.Services.ReadinessState>();
    var dbHealth = httpContext.RequestServices.GetService<CodingAgentWebUI.Services.DatabaseHealthState>();
    if (!readiness.IsReady)
        return Results.Json(new { status = "draining", timestamp = DateTime.UtcNow }, statusCode: 503);
    if (dbHealth is not null && !dbHealth.IsDatabaseHealthy)
        return Results.Json(new { status = "unhealthy", reason = "database_unreachable", timestamp = DateTime.UtcNow }, statusCode: 503);
    return Results.Ok(new { status = "ready", timestamp = DateTime.UtcNow });
})
    .AllowAnonymous();

// Redirect root "/" to the main page
app.MapGet("/", () => Results.Redirect("/agent-coding"))
    .AllowAnonymous();

// Export run history as JSON download
app.MapGet("/api/export/runs.json", (IPipelineRunHistoryService history, bool? feedbackOnly) =>
{
    var runs = (IEnumerable<PipelineRunSummary>)history.GetRunHistory();
    if (feedbackOnly == true)
        runs = runs.Where(r => r.Feedback is not null);

    var json = System.Text.Json.JsonSerializer.Serialize(runs.ToList(), PipelineJsonOptions.Default);
    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
    var fileName = $"pipeline-runs-{DateTime.UtcNow:yyyy-MM-dd}.json";
    return Results.File(bytes, "application/json", fileName);
}).AllowAnonymous();

app.UseStaticFiles();
app.MapStaticAssets();

app.UseAuthentication();
app.UseAuthorization();

// SignalR hub endpoint for agent connections
app.MapHub<AgentHub>(HubRoutes.Agent).RequireAuthorization("AgentApiKey");

// Work Item HTTP API — agents fetch assignments and report status (DB modes only)
if (!string.IsNullOrEmpty(dbConnectionString))
{
    app.MapWorkItemEndpoints();
    app.MapConfigImportExportEndpoints();
}

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .DisableAntiforgery();

// Clean up orphaned consolidation runs from previous sessions
var consolidationService = app.Services.GetRequiredService<IConsolidationService>();
await consolidationService.CleanupOrphanedRunsAsync(CancellationToken.None);

// Rehydrate queued consolidation runs (re-enqueue them for dispatch)
var queuedRuns = await consolidationService.RehydrateQueuedRunsAsync(CancellationToken.None);
if (queuedRuns.Count > 0)
{
    var consolidationQueue = app.Services.GetRequiredService<ConsolidationQueueService>();
    foreach (var run in queuedRuns)
    {
        var job = new PendingConsolidationJob
        {
            RunId = run.RunId,
            Type = run.Type,
            TemplateId = run.TemplateId,
            WorkspacePath = Path.Combine(pipelineConfig.WorkspaceBaseDirectory, "consolidation", run.RunId),
            RequiredLabels = run.QueuedRequiredLabels ?? [],
            EnqueuedAt = run.StartedAtUtc
        };
        consolidationQueue.EnqueueJob(job);
    }
}

// Auto-start pipeline loop if configured
if (pipelineConfig.ClosedLoopAutoStart)
{
    var loopService = app.Services.GetRequiredService<PipelineLoopService>();
    var loopStarted = await loopService.StartLoopAsync();
    if (loopStarted)
        Log.Information("Pipeline loop auto-started (ClosedLoopAutoStart=true)");
    else
        Log.Warning("Pipeline loop auto-start requested but StartLoopAsync returned false (no valid templates?)");
}

app.Run();

// Make Program class accessible for WebApplicationFactory in tests
public partial class Program { }
