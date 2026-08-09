using CodingAgentWebUI;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Infrastructure.Telemetry;
using CodingAgentWebUI.Models;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;

// ── CLI command: export-config ──────────────────────────────────────────────
if (await ExportConfigCommand.TryExecuteAsync(args)) return;

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

// Host shutdown timeout: drain delay (15s) + ShutdownService timeout (15s) = 30s used, 10s headroom remaining.
// ShutdownBudgetValidation warns if headroom drops below 5s (i.e., drain + shutdown > 35s).
builder.Services.Configure<HostOptions>(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(40));

// Pipeline — Configuration Store (created eagerly to load config before DI container is built)
var configStore = new JsonConfigurationStore(PipelineConstants.ConfigBaseDirectory);
var pipelineConfig = await configStore.LoadPipelineConfigAsync(CancellationToken.None);

// Domain service registrations (extracted into focused extension methods)
var dbConnectionString = CodingAgentWebUI.Services.DatabaseConnectionResolver.Resolve(builder.Configuration);
var workDistributionMode = builder.Configuration.GetValue<string>("WorkDistribution:Mode") ?? "SignalR";
var isKubernetesMode = string.Equals(workDistributionMode, "Kubernetes", StringComparison.OrdinalIgnoreCase);
builder.Services.AddSingleton(new CodingAgentWebUI.Services.FeatureFlags
{
    IsDatabaseMode = !string.IsNullOrEmpty(dbConnectionString),
    IsKubernetesMode = isKubernetesMode
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

// JobTemplateStore — registered unconditionally so Settings.razor injection never fails.
// In k8s mode, the real store is registered inside AddWorkDistribution → RegisterKubernetesMode.
// In all other modes, an empty sentinel is registered here.
// The empty store is never used for dispatch (DispatchService only runs in k8s mode).
if (!isKubernetesMode)
{
    builder.Services.AddSingleton<CodingAgentWebUI.Orchestration.Dispatch.JobTemplateStore>(
        CodingAgentWebUI.Orchestration.Dispatch.JobTemplateStore.CreateEmpty());
    // ModelFetchJobService is resolved via IServiceProvider.GetService<> (not @inject) in Settings.razor
    // so no registration needed here — GetService<> returns null when the type is unregistered.
}

// Infrastructure health aggregation — reads from DatabaseHealthState + IConnectionMultiplexer (both optional)
builder.Services.AddSingleton<CodingAgentWebUI.Services.InfrastructureHealthService>();

// Page-level services (scoped — one instance per Blazor circuit)
builder.Services.AddScoped<CodingAgentWebUI.Services.AgentCodingPageService>();
builder.Services.AddScoped<CodingAgentWebUI.Services.AgentMonitoringPageService>();
builder.Services.AddScoped<CodingAgentWebUI.Services.NotificationService>();

// SignalR — hub services with MessagePack protocol and agent authorization filter
builder.Services.AddSignalRServices();

// Agent API key authentication and authorization
builder.Services.AddAgentAuthentication(Serilog.Log.Logger);

// Configure Serilog
builder.Host.ConfigureSerilog();

// Configure OpenTelemetry (tracing + metrics)
var redisConnectionString = builder.Configuration.GetValue<string>("SignalR:Redis:ConnectionString");
builder.Services.AddApplicationTelemetry(dbConnectionString, redisConnectionString);

var app = builder.Build();

// ── Post-Build startup sequence ─────────────────────────────────────────────
// Each concern is extracted into its own WebApplication extension method.
// Ordering matters: InitializeDatabaseAsync must precede RehydrateActiveRunsAsync (needs DB),
// and MapApplicationEndpoints must precede RunConsolidationStartupAsync (needs middleware).
// TODO: Add unit/integration tests for each extracted startup extension method
// (ValidateShutdownBudget, ValidateDiWiring, RegisterObservableGauges, RunConsolidationStartupAsync,
// AutoStartPipelineLoopAsync). Extraction was done to enable independent testability but no tests
// were added yet. (review-findings)

app.ValidateShutdownBudget();
await app.InitializeDatabaseAsync();
await app.RehydrateActiveRunsAsync();
app.ValidateDiWiring();
app.RegisterObservableGauges();
app.MapApplicationEndpoints(dbConnectionString);
await app.RunConsolidationStartupAsync(pipelineConfig);
await app.AutoStartPipelineLoopAsync(pipelineConfig);

app.Run();

// Make Program class accessible for WebApplicationFactory in tests
public partial class Program { }
