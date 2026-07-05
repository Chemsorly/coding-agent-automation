using CodingAgentWebUI;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

// ── CLI command: export-config (early exit) ─────────────────────────────────
if (await ExportConfigCommand.ExecuteAsync(args)) return;

var builder = WebApplication.CreateBuilder(args);

// Service registrations
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton(CodingAgentWebUI.Models.BuildInfo.Load());
builder.Services.Configure<HostOptions>(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(40));

// Pipeline — Configuration Store (created eagerly to load config before DI container is built)
var configStore = new JsonConfigurationStore(PipelineConstants.ConfigBaseDirectory);
var pipelineConfig = await configStore.LoadPipelineConfigAsync(CancellationToken.None);

// Domain service registrations
var dbConnectionString = DatabaseConnectionResolver.Resolve(builder.Configuration);
builder.Services.AddSingleton(new FeatureFlags { IsDatabaseMode = !string.IsNullOrEmpty(dbConnectionString) });
if (string.IsNullOrEmpty(dbConnectionString))
    builder.Services.AddInfrastructureServices(configStore, pipelineConfig);
else
    builder.Services.AddInfrastructureServicesWithoutConfigStore();
builder.Services.AddPipelineServices(Log.Logger);
builder.Services.AddPipelineCoreServices(isDatabaseMode: !string.IsNullOrEmpty(dbConnectionString));
builder.Services.AddOrchestrationServices(pipelineConfig,
    string.IsNullOrEmpty(dbConnectionString) ? null : (builder.Configuration.GetValue<string>("WorkDistribution:Mode") ?? "SignalR"));
builder.Services.AddConsolidationServices(pipelineConfig);
builder.Services.AddWorkDistribution(builder.Configuration);
builder.Services.AddDatabaseHealthServices(builder.Configuration);
builder.Services.AddSingleton<InfrastructureHealthService>();
builder.Services.AddScoped<AgentCodingPageService>();
builder.Services.AddScoped<NotificationService>();

// Extracted service registrations
builder.Services.AddSignalRServices();
builder.Services.AddAgentAuthentication(Log.Logger);
builder.Host.ConfigureSerilog();
builder.Services.AddObservability(dbConnectionString, builder.Configuration);

var app = builder.Build();

// Shutdown budget validation
ShutdownBudgetValidator.ValidateShutdownBudget();

// Database startup: connection retry + migration/verification (blocks until ready)
await app.InitializeDatabaseAsync();

// DI wiring assertion — fail fast if DB mode resolved the wrong implementation
if (!string.IsNullOrEmpty(dbConnectionString))
{
    var historyService = app.Services.GetRequiredService<IPipelineRunHistoryService>();
    if (historyService is not PostgresPipelineRunHistoryService)
        throw new InvalidOperationException(
            $"DB mode requires PostgresPipelineRunHistoryService but resolved {historyService.GetType().Name}. " +
            "Check DI registration order in Program.cs.");
}

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
    app.Lifetime.ApplicationStopping.Register(() => updateTimer.Dispose());
    CodingAgentWebUI.Orchestration.Telemetry.WorkDistributionTelemetry.RegisterWorkItemsByStatusCallback(() => cachedMeasurements);
}

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

// Endpoint + middleware configuration
app.MapApplicationEndpoints(dbConnectionString);

// Post-build startup tasks (consolidation cleanup + rehydration + loop auto-start)
await app.RunPostBuildStartupAsync(pipelineConfig);

app.Run();

// Make Program class accessible for WebApplicationFactory in tests
public partial class Program { }
