using CodingAgentWebUI;
using CodingAgentWebUI.Hub;
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

// Domain service registrations (extracted into focused extension methods)
var dbConnectionString = DatabaseConnectionResolver.Resolve(builder.Configuration);
if (string.IsNullOrEmpty(dbConnectionString))
{
    Log.Fatal("Database__Host is not configured. Kubernetes deployment requires PostgreSQL. Exiting.");
    return;
}

// Bootstrap config for DI registration only — real config is loaded from Postgres at runtime.
// NOTE: ClosedLoopAutoStart defaults to false here, so the pipeline loop does not auto-start.
// Spec 045 Req 4.4 replaces this with a Postgres read.
var pipelineConfig = new PipelineConfiguration();

builder.Services.AddInfrastructureServices();
builder.Services.AddPipelineServices(Serilog.Log.Logger);
builder.Services.AddPipelineCoreServices();
builder.Services.AddOrchestrationServices(pipelineConfig);
builder.Services.AddConsolidationServices(pipelineConfig);
builder.Services.AddWorkDistribution(builder.Configuration);
builder.Services.AddDatabaseHealthServices(builder.Configuration);

// Infrastructure health aggregation — reads from DatabaseHealthState + IConnectionMultiplexer (both optional)
builder.Services.AddSingleton<CodingAgentWebUI.Services.InfrastructureHealthService>();

// Page-level services (scoped — one instance per Blazor circuit)
builder.Services.AddScoped<CodingAgentWebUI.Services.IIssueDrawerService, CodingAgentWebUI.Services.IssueDrawerService>();
builder.Services.AddScoped<CodingAgentWebUI.Services.IPrReviewDrawerService, CodingAgentWebUI.Services.PrReviewDrawerService>();
builder.Services.AddScoped<CodingAgentWebUI.Services.IEpicDrawerService, CodingAgentWebUI.Services.EpicDrawerService>();
builder.Services.AddScoped<CodingAgentWebUI.Services.AgentCodingPageService>();
builder.Services.AddAgentMonitoringPageServiceDependencies();
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
// Ordering matters: InitializeDatabaseAsync must precede MapApplicationEndpoints.
// TODO: Add unit/integration tests for each extracted startup extension method
// (ValidateShutdownBudget, ValidateDiWiring, RegisterObservableGauges, RunConsolidationStartupAsync,
// AutoStartPipelineLoopAsync). Extraction was done to enable independent testability but no tests
// were added yet. (review-findings)

app.ValidateShutdownBudget();
await app.InitializeDatabaseAsync();
// Spec 044: RehydrateActiveRunsAsync removed — IOrchestratorRunService rehydration is now performed
// in CodingAgentWebUI.Api (Task 2). The monolith's in-memory run state is no longer authoritative.
app.ValidateDiWiring();
app.RegisterObservableGauges();
app.MapApplicationEndpoints(dbConnectionString);
await app.RunConsolidationStartupAsync(pipelineConfig);
await app.AutoStartPipelineLoopAsync(pipelineConfig);

app.Run();

// Make Program class accessible for WebApplicationFactory in tests
public partial class Program { }
