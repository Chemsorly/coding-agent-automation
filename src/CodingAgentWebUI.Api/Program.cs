using CodingAgentWebUI.Api;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.Pipeline;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;

// Bootstrap logger: captures log output before UseSerilog takes over
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(theme: Serilog.Sinks.SystemConsole.Themes.ConsoleTheme.None)
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

// ── Startup identity log ─────────────────────────────────────────────────────
var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
var serviceName = builder.Configuration.GetValue<string>("OTEL_SERVICE_NAME") ?? "coding-agent-api";
Log.Information("Pipeline API starting: ServiceName={ServiceName} Version={Version}", serviceName, version);

// ── Fast-fail: PostgreSQL required ──────────────────────────────────────────
var dbConnectionString = DatabaseConnectionResolver.Resolve(builder.Configuration);
if (string.IsNullOrEmpty(dbConnectionString))
{
    Log.Fatal("Database__Host is not configured. The Pipeline API requires PostgreSQL. Exiting.");
    return;
}

// ── Fast-fail: agent API key required ───────────────────────────────────────
var agentApiKey = builder.Configuration.GetValue<string>("AGENT_API_KEY")
    ?? builder.Configuration.GetValue<string>("AgentApiKey");
if (string.IsNullOrEmpty(agentApiKey))
{
    Log.Fatal("AGENT_API_KEY is not configured. Exiting.");
    return;
}

// ── Configure JSON serialization (enum-as-string to match agent DTOs) ───────
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// ── Host shutdown timeout ────────────────────────────────────────────────────
builder.Services.Configure<HostOptions>(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(40));

// ── Service registrations ────────────────────────────────────────────────────
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddApiInfrastructure(dbConnectionString);
builder.Services.AddApiOrchestration();
builder.Services.AddAgentHubServices();  // shared, from CodingAgentWebUI.Hub

// ── SignalR (with optional Redis backplane) ──────────────────────────────────
builder.Services.AddApiSignalR(builder.Configuration);

// ── Agent API key authentication + authorization ─────────────────────────────
builder.Services.AddApiAuthentication(agentApiKey, Log.Logger);

// ── Serilog ──────────────────────────────────────────────────────────────────
builder.Host.ConfigureApiSerilog();

// ── ASPNETCORE_URLS defaults to port 8090 (avoids monolith's 8080) ──────────
builder.WebHost.UseUrls("http://+:8090");

// ── OpenTelemetry ─────────────────────────────────────────────────────────────
var otelServiceName = builder.Configuration.GetValue<string>("OTEL_SERVICE_NAME") ?? "coding-agent-api";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: otelServiceName,
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation()
         .AddHttpClientInstrumentation()
         .AddOtlpExporter();
    })
    .WithMetrics(m =>
    {
        m.AddAspNetCoreInstrumentation()
         .AddHttpClientInstrumentation()
         // The API owns the work-distribution instruments; this AddMeter ensures they are exported.
         // WorkItemMetricsBackgroundService feeds the workitems-by-status gauges here, and
         // WorkItemEndpoints/DispatchStateBuilder record terminal statuses and dispatch
         // latency. Without this AddMeter the measurements are taken but never exported, and
         // the WorkItemsPendingTooLong / WorkItemFailureRateHigh / CredentialPoolExhausted
         // alerts have no series to evaluate.
         .AddMeter(WorkDistributionTelemetry.MeterName)
         // Prometheus requires Cumulative temporality; the OTLP exporter defaults to Delta for
         // histograms and counters, which Grafana Cloud silently drops. Matches the monolith.
         .AddOtlpExporter((_, readerOptions) =>
             readerOptions.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative);
    });

var app = builder.Build();

// Migrations MUST be awaited before app.Run().
// Hosted services start concurrently with app.Run() — the hub must not accept
// RegisterAgent calls against an unmigrated schema.
await app.RunApiMigrationsAsync(builder.Configuration);

app.MapApiHealthEndpoints();
app.RegisterApiObservableGauges();
app.UseAuthentication();
app.UseAuthorization();

// SignalR hub — agents connect here.
app.MapHub<AgentHub>(HubRoutes.Agent).RequireAuthorization(ApiAuthPolicies.Agent);

app.MapWorkItemEndpoints();
app.MapPipelineRunEndpoints();
app.MapConfigEndpoints();
app.MapConsolidationRunEndpoints();
app.MapHarnessSuggestionEndpoints();
app.MapAgentEndpoints();

await app.RunAsync();

// Make Program accessible for WebApplicationFactory in integration tests
public partial class Program { }

