using CodingAgentWebUI;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Models;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.SignalR;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;

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

// ── Monolith runtime options (T11: replaces scattered env reads) ─────────────────────────────
// Binds READINESS_DRAIN_DELAY_SECONDS, PIPELINE_LOOP_STARTUP_DELAY_SECONDS from IConfiguration.
// ValidateDataAnnotations ensures startup fails fast with a named error if values are out of range.
// Note: AgentApiKey is set from builder.Configuration (read below) — options registration deferred.
builder.Services.AddOptions<MonolithRuntimeOptions>()
    .Configure<IConfiguration>((opts, cfg) =>
    {
        var drainDelay = Environment.GetEnvironmentVariable("READINESS_DRAIN_DELAY_SECONDS");
        if (!string.IsNullOrWhiteSpace(drainDelay) && int.TryParse(drainDelay, out var d))
            opts.ReadinessDrainDelaySeconds = d;

        var loopDelay = cfg.GetValue<int?>("Orchestrator:PipelineLoopStartupDelaySeconds")
            ?? cfg.GetValue<int?>("Env:PipelineLoopStartupDelaySeconds");
        if (loopDelay.HasValue)
            opts.PipelineLoopStartupDelaySeconds = loopDelay.Value;

        opts.AgentApiKey = cfg.GetValue<string>("AGENT_API_KEY")
            ?? Environment.GetEnvironmentVariable("AGENT_API_KEY")
            ?? "";
    })
    .ValidateDataAnnotations()
    .ValidateOnStart();

// Host shutdown timeout: drain delay (15s) + ShutdownService timeout (15s) = 30s used, 10s headroom remaining.
// ShutdownBudgetValidation warns if headroom drops below 5s (i.e., drain + shutdown > 35s).
builder.Services.Configure<HostOptions>(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(40));

// ── Pipeline API client ───────────────────────────────────────────────────────────────────────
// Fast-fail: API URL required — monolith has no direct DB access.
// Helm injects this as PipelineApi__BaseUrl (double-underscore → colon convention).
var apiBaseUrl = builder.Configuration.GetValue<string>("PipelineApi:BaseUrl");
if (string.IsNullOrEmpty(apiBaseUrl))
{
    Log.Fatal("PipelineApi:BaseUrl is required. Ensure CodingAgentWebUI.Api is deployed first.");
    return;
}

var agentApiKey = builder.Configuration.GetValue<string>("AGENT_API_KEY")
    ?? Environment.GetEnvironmentVariable("AGENT_API_KEY")
    ?? "";

builder.Services.AddPipelineApiClient(new PipelineApiClientOptions
{
    BaseUrl = apiBaseUrl,
    AgentApiKey = agentApiKey
});

// Scoped hub connection — one per Blazor circuit (overrides the Transient registered
// by AddPipelineApiClient above). Connects to the API hub using AGENT_API_KEY as Bearer.
// Helm injects PipelineApi__HubUrl (double-underscore → PipelineApi:HubUrl IConfiguration key).
// Falls back to baseUrl + /hubs/agent if HubUrl is not set.
// See Req 3.6 L1 for the explicit wiring requirement.
var apiHubUrl = builder.Configuration.GetValue<string>("PipelineApi:HubUrl")
    ?? $"{apiBaseUrl.TrimEnd('/')}/hubs/agent";
builder.Services.AddScoped<IAgentHubConnection>(_ => new AgentHubConnection(apiHubUrl, agentApiKey));

// null — monolith has no direct DB access; AddApplicationTelemetry does not include Npgsql tracing.
var dbConnectionString = (string?)null;

// Bootstrap config for DI registration only — real config is loaded from Pipeline API at runtime.
// NOTE: ClosedLoopAutoStart defaults to false here; AutoStartPipelineLoopAsync loads the real value from the API.
var pipelineConfig = new PipelineConfiguration();

builder.Services.AddInfrastructureServices();
builder.Services.AddPipelineServices(Serilog.Log.Logger);
builder.Services.AddPipelineCoreServices();
builder.Services.AddOrchestrationServices(pipelineConfig);
builder.Services.AddConsolidationServices(pipelineConfig);
builder.Services.AddWorkDistribution(builder.Configuration);

// Infrastructure health aggregation — reads from IConnectionMultiplexer (Redis, optional).
// DB health monitoring removed — monolith has no direct Postgres connection.
builder.Services.AddSingleton<CodingAgentWebUI.Services.InfrastructureHealthService>();

// Page-level services (scoped — one instance per Blazor circuit)
builder.Services.AddScoped<CodingAgentWebUI.Services.IIssueDrawerService, CodingAgentWebUI.Services.IssueDrawerService>();
builder.Services.AddScoped<CodingAgentWebUI.Services.IPrReviewDrawerService, CodingAgentWebUI.Services.PrReviewDrawerService>();
builder.Services.AddScoped<CodingAgentWebUI.Services.IEpicDrawerService, CodingAgentWebUI.Services.EpicDrawerService>();
builder.Services.AddScoped<CodingAgentWebUI.Services.AgentCodingPageService>();
builder.Services.AddAgentMonitoringPageServiceDependencies();
builder.Services.AddScoped<CodingAgentWebUI.Services.AgentMonitoringPageService>();
builder.Services.AddScoped<CodingAgentWebUI.Services.NotificationService>();
builder.Services.AddScoped<CodingAgentWebUI.Services.IChatPromptBuilder, CodingAgentWebUI.Services.ChatPromptBuilder>();

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
// Ordering: ValidateShutdownBudget, ValidateDiWiring, RegisterObservableGauges, then MapApplicationEndpoints.
// TODO: Add unit/integration tests for each extracted startup extension method
// (ValidateShutdownBudget, ValidateDiWiring, RegisterObservableGauges, RunConsolidationStartupAsync,
// AutoStartPipelineLoopAsync). Extraction was done to enable independent testability but no tests
// were added yet. (review-findings)

app.ValidateShutdownBudget();
app.ValidateDiWiring();
app.RegisterObservableGauges();
app.MapApplicationEndpoints();
await app.RunConsolidationStartupAsync(pipelineConfig);
await app.AutoStartPipelineLoopAsync();

app.Run();

// Make Program class accessible for WebApplicationFactory in tests
public partial class Program { }
