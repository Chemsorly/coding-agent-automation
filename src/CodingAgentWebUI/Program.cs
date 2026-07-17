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

    var exportConfig = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    var connectionString = CodingAgentWebUI.Services.DatabaseConnectionResolver.Resolve(exportConfig);

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Log.Error("export-config requires Database:Host to be configured");
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

// Bootstrap logger: captures log output during service registration (before UseSerilog takes over at Build())
// TODO: Add integration test verifying ResolveApiKey log messages appear in output (review-findings #953)
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(theme: Serilog.Sinks.SystemConsole.Themes.ConsoleTheme.None)
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

// Register services
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(BuildInfo.Load());

// Configure JSON serialization for minimal API endpoints (enum-as-string to match agent DTOs)
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// Host shutdown timeout: drain delay (15s) + ShutdownService timeout (15s) + buffer (10s) = 40s
builder.Services.Configure<HostOptions>(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(40));

// Pipeline — Configuration Store (created eagerly to load config before DI container is built)
var configStore = new JsonConfigurationStore(PipelineConstants.ConfigBaseDirectory);
var pipelineConfig = await configStore.LoadPipelineConfigAsync(CancellationToken.None);

// Domain service registrations (extracted into focused extension methods)
var dbConnectionString = CodingAgentWebUI.Services.DatabaseConnectionResolver.Resolve(builder.Configuration);
builder.Services.AddSingleton(new CodingAgentWebUI.Services.FeatureFlags
{
    IsDatabaseMode = !string.IsNullOrEmpty(dbConnectionString)
});
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
builder.Services.AddPipelineCoreServices(isDatabaseMode: !string.IsNullOrEmpty(dbConnectionString));
builder.Services.AddOrchestrationServices(pipelineConfig,
    string.IsNullOrEmpty(dbConnectionString) ? null : (builder.Configuration.GetValue<string>("WorkDistribution:Mode") ?? "SignalR"));
builder.Services.AddConsolidationServices(pipelineConfig);
builder.Services.AddWorkDistribution(builder.Configuration);
builder.Services.AddDatabaseHealthServices(builder.Configuration);

// Infrastructure health aggregation — reads from DatabaseHealthState + IConnectionMultiplexer (both optional)
builder.Services.AddSingleton<CodingAgentWebUI.Services.InfrastructureHealthService>();

// Page-level services (scoped — one instance per Blazor circuit)
builder.Services.AddScoped<CodingAgentWebUI.Services.AgentCodingPageService>();
builder.Services.AddScoped<CodingAgentWebUI.Services.NotificationService>();

// SignalR — hub services with MessagePack protocol
builder.Services.AddSignalR(options =>
    {
        // Agents may send output chunks or large payloads; default 32KB is too restrictive.
        options.MaximumReceiveMessageSize = 128 * 1024; // 128 KB
    })
    .AddMessagePackProtocol(options =>
    {
        options.SerializerOptions = MessagePack.MessagePackSerializerOptions.Standard
            .WithResolver(MessagePack.Resolvers.CompositeResolver.Create(
                new MessagePack.Formatters.IMessagePackFormatter[] { new CodingAgentWebUI.Pipeline.Models.JobIdFormatter() },
                new MessagePack.IFormatterResolver[] { MessagePack.Resolvers.ContractlessStandardResolverAllowPrivate.Instance }));
    });

// SignalR — hub filter for agent authorization
builder.Services.AddSingleton<IHubFilter>(sp => new AgentAuthorizationFilter(
    sp.GetRequiredService<IAgentRegistryService>(),
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
var dbLogLevel = Environment.GetEnvironmentVariable("DB_LOG_LEVEL")?.ToLowerInvariant() switch
{
    "debug" or "dbg" => Serilog.Events.LogEventLevel.Debug,
    "information" or "info" => Serilog.Events.LogEventLevel.Information,
    "verbose" or "trace" => Serilog.Events.LogEventLevel.Verbose,
    "error" or "err" => Serilog.Events.LogEventLevel.Error,
    _ => Serilog.Events.LogEventLevel.Warning
};
builder.Host.UseSerilog((ctx, lc) => lc
    .MinimumLevel.Is(orchestratorLogLevel)
    // Suppress noisy ASP.NET Core framework logging (health checks, static files, Blazor negotiation, auth)
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    // EF Core SQL command logging — controlled separately via DB_LOG_LEVEL env var
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", dbLogLevel)
    // Suppress noisy Npgsql connection open/close logging (Verbose/Trace only)
    .MinimumLevel.Override("Npgsql", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
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

// ── Shutdown budget validation ──────────────────────────────────────────────
// Warn if configured drain delay + shutdown timeout exceeds host ShutdownTimeout budget.
// Default budget: drain (15s) + ShutdownService (15s) + buffer (10s) = 40s.
{
    var drainDelay = ReadinessDrainService.ResolveDrainDelay();
    const int shutdownServiceTimeout = 15; // ShutdownService default
    const int hostShutdownTimeout = 40;    // HostOptions.ShutdownTimeout value set above
    const int requiredBuffer = 5;          // Minimum buffer for host finalization
    var totalRequired = drainDelay.TotalSeconds + shutdownServiceTimeout + requiredBuffer;
    if (totalRequired > hostShutdownTimeout)
    {
        Log.Warning(
            "Shutdown budget exceeded: drain ({DrainDelay}s) + ShutdownService ({ShutdownTimeout}s) + buffer ({Buffer}s) = {Total}s > HostShutdownTimeout ({HostTimeout}s). " +
            "ShutdownService may be force-killed before completing. Reduce READINESS_DRAIN_DELAY_SECONDS or increase HostOptions.ShutdownTimeout.",
            drainDelay.TotalSeconds, shutdownServiceTimeout, requiredBuffer, totalRequired, hostShutdownTimeout);
    }
}

// Database startup: connection retry + migration/verification (blocks until ready)
await app.InitializeDatabaseAsync();

// Rehydrate active pipeline runs from WorkItems (DB mode only).
// Must run BEFORE app.Run() which starts IHostedService instances (HeartbeatMonitor, DrainService).
// This ensures GetActiveRuns() returns active runs immediately on restart — no observability gap.
if (!string.IsNullOrEmpty(dbConnectionString))
{
    var rehydrationDbFactory = app.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
    await using var rehydrationDb = await rehydrationDbFactory.CreateDbContextAsync();

    var activeWorkItems = await rehydrationDb.WorkItems
        .AsNoTracking()
        .Where(w => (w.Status == WorkItemStatus.Dispatched || w.Status == WorkItemStatus.Running)
                 && w.TaskType != WorkItemTaskType.Consolidation)
        .ToListAsync();

    if (activeWorkItems.Count > 0)
    {
        var runService = app.Services.GetRequiredService<OrchestratorRunService>();
        var rehydratedCount = 0;

        foreach (var item in activeWorkItems)
        {
            if (string.IsNullOrEmpty(item.Payload)) continue;

            try
            {
                var request = System.Text.Json.JsonSerializer.Deserialize<JobDistributionRequest>(
                    item.Payload, PipelineJsonOptions.Default);
                if (request is null || string.IsNullOrEmpty(request.RunId)) continue;

                // AgentId intentionally null: HeartbeatMonitor Phase 3 skips runs without AgentId,
                // preventing false-positive orphan detection before agents reconnect.
                // Agents set AgentId on reconnect via AgentHub.RegisterAgent.
                // TODO: CurrentStep is approximated — Running items may have been in reviewing/building/retrying
                // phase. The agent will update CurrentStep on reconnect, but until then the UI may show an
                // inaccurate step for rehydrated runs.
                var initialStep = item.Status == WorkItemStatus.Running
                    ? PipelineStep.GeneratingCode
                    : PipelineStep.Created;

                var run = PipelineRunFactory.FromDistributionRequest(
                    request, agentId: null, initialStep,
                    startedAt: item.DispatchedAt ?? item.CreatedAt);
                runService.AddRun(run);
                rehydratedCount++;
            }
            catch (System.Text.Json.JsonException ex)
            {
                Log.Warning(ex, "Failed to deserialize WorkItem {WorkItemId} payload during rehydration — skipping", item.Id);
            }
        }

        if (rehydratedCount > 0)
            Log.Information("Rehydrated {Count} active pipeline runs from WorkItems table", rehydratedCount);
    }
}

// ── DI wiring assertion ─────────────────────────────────────────────────────
// Fail fast if DB mode resolved the wrong IPipelineRunHistoryService implementation.
// Guards against accidental re-introduction of competing registrations.
if (!string.IsNullOrEmpty(dbConnectionString))
{
    var historyService = app.Services.GetRequiredService<IPipelineRunHistoryService>();
    if (historyService is not PostgresPipelineRunHistoryService)
        throw new InvalidOperationException(
            $"DB mode requires PostgresPipelineRunHistoryService but resolved {historyService.GetType().Name}. " +
            "Check DI registration order in Program.cs.");
}

// workitems_by_status observable gauge is now handled by WorkItemMetricsBackgroundService
// (registered in WorkDistributionRegistration.AddWorkDistribution for DB mode).

// Register observable gauges for dispatch queue and agent concurrency metrics
var dispatcher = app.Services.GetRequiredService<JobDispatcherService>();
var agentRegistry = app.Services.GetRequiredService<IAgentRegistryService>();
var pendingWorkQuery = app.Services.GetRequiredService<IPendingWorkQuery>();
_ = PipelineTelemetry.Meter.CreateObservableGauge("dispatch.queue.depth",
    () => pendingWorkQuery.PendingCount, "{item}", "Jobs waiting for available agent");
_ = PipelineTelemetry.Meter.CreateObservableGauge("agent.jobs.active",
    () => agentRegistry.GetBusyAgentCount(), "{job}", "Currently executing agent jobs");
_ = PipelineTelemetry.Meter.CreateObservableGauge("agent.connections.total",
    () => agentRegistry.GetAllAgents().Count, "{connection}", "Total registered agents");

// Graceful shutdown is handled by ShutdownService (IHostedLifecycleService)
// — async, with 15s timeout, non-blocking (Req 12)



// Kubernetes-style health probes — anonymous, no auth required
app.MapHealthEndpoints();

// Redirect root "/" to the main page (relative redirect — works behind any reverse proxy)
app.MapGet("/", () => Results.Redirect("agent-coding"))
    .AllowAnonymous();

// Export run history as JSON download
// TODO: Accept CancellationToken parameter and pass to GetRunHistoryAsync(ct) so the DB query cancels on client disconnect
app.MapGet("/api/export/runs.json", async (IPipelineRunHistoryService history, bool? feedbackOnly) =>
{
    var runs = (IEnumerable<PipelineRunSummary>)await history.GetRunHistoryAsync();
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

// Rehydrate queued consolidation runs via IWorkDistributor (unified dispatch path)
var queuedRuns = await consolidationService.RehydrateQueuedRunsAsync(CancellationToken.None);
if (queuedRuns.Count > 0)
{
    var workDistributor = app.Services.GetRequiredService<IWorkDistributor>();
    var profileStore = app.Services.GetRequiredService<IAgentProfileStore>();
    var profileResolver = new ProfileResolver();
    var rehydrationProfiles = await profileStore.LoadAgentProfilesAsync(CancellationToken.None);
    foreach (var run in queuedRuns)
    {
        // Resolve full profile MatchLabels from QueuedRequiredLabels to produce correct AgentSelector
        var requiredLabels = run.QueuedRequiredLabels ?? [];
        var profile = profileResolver.ResolveByRequiredLabels(rehydrationProfiles, requiredLabels.ToList());
        var selectorLabels = profile?.MatchLabels ?? requiredLabels;

        var request = new JobDistributionRequest
        {
            IssueIdentifier = run.RunId,
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = "",
            InitiatedBy = ConsolidationConstants.InitiatedBy,
            TaskType = WorkItemTaskType.Consolidation,
            AgentSelector = string.Join(",", selectorLabels.OrderBy(l => l, StringComparer.Ordinal)),
            TimeoutSeconds = (int)pipelineConfig.AgentTimeout.TotalSeconds,
            ConsolidationRunType = run.Type,
            ConsolidationTemplateId = run.TemplateId,
            ConsolidationWorkspacePath = Path.Combine(pipelineConfig.WorkspaceBaseDirectory, "consolidation", run.RunId),
            RunId = run.RunId
        };
        await workDistributor.DistributeAsync(request, CancellationToken.None);
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
