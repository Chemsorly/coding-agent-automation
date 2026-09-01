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
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level}] {Message:lj}{NewLine}{Exception}",
        theme: Serilog.Sinks.SystemConsole.Themes.ConsoleTheme.None)
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

// ── ASPNETCORE_URLS defaults to port 8080 ────────────────────────────────────
builder.WebHost.UseUrls("http://+:8080"); // NOSONAR S1075 — port is runtime infrastructure config, not a business URL

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
         // AgentHub lives in the API process (moved from monolith in Spec 041).
         // Without this source, RegisterAgent / JobAccepted / JobCompleted hub invocations
         // produce no spans — agent lifecycle events are invisible in traces.
         .AddSource("Microsoft.AspNetCore.SignalR.Server")
         .AddOtlpExporter();
    })
    .WithMetrics(m =>
    {
        m.AddAspNetCoreInstrumentation()
         .AddHttpClientInstrumentation()
         // WorkDistributionTelemetry.MeterName is exported here because the API owns some
         // work-distribution instruments: WorkItemEndpoints records terminal statuses and
         // dispatch latency via LogTerminalStatus/RecordDispatchLatency, and
         // WorkItemMetricsBackgroundService feeds the workitems-by-status gauges.
         // NOTE: The epoch/credential-pool gauges (DispatcherLastPollEpoch, CredentialPoolAvailable,
         // CredentialPoolClaimed) are written ONLY by the Job Controller's DispatchService — the
         // API's DispatchStateBuilder does NOT call RecordLastPollEpoch or UpdateCredentialPoolMetrics.
         // This ensures the DispatcherStalled / CredentialPoolExhausted Helm alert rules evaluate
         // a single authoritative series from the Job Controller, not a conflicting API series.
         .AddMeter(WorkDistributionTelemetry.MeterName)
         // The API hosts AgentRegistryService and registers agent.jobs.active /
         // agent.connections.total ObservableGauges on PipelineTelemetry.Meter via
         // RegisterApiObservableGauges(). Without this AddMeter those gauges are created
         // on the meter but the meter is not subscribed — measurements are silently dropped.
         .AddMeter(PipelineTelemetry.SourceName)
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

// Log every 4xx/5xx response as a structured Serilog event. This runs under the Serilog
// category (not Microsoft.AspNetCore), so it is NOT suppressed by the Warning override in
// ApiSerilogRegistration. Captures hub negotiate failures (e.g. 404 on /hubs/agent/negotiate)
// that would otherwise be invisible because Microsoft.AspNetCore is overridden to Warning.
app.UseSerilogRequestLogging(opts =>
{
    opts.GetLevel = (ctx, _, ex) =>
        ex is not null || ctx.Response.StatusCode >= 400
            ? Serilog.Events.LogEventLevel.Warning
            : Serilog.Events.LogEventLevel.Debug;
    opts.EnrichDiagnosticContext = (diag, ctx) =>
    {
        diag.Set("RequestHost", ctx.Request.Host.Value);
        diag.Set("RequestScheme", ctx.Request.Scheme);
        if (ctx.Response.StatusCode >= 400)
            diag.Set("ResponseStatusCode", ctx.Response.StatusCode);
    };
});

app.UseAuthentication();
app.UseAuthorization();

// SignalR hub — agents connect here.
app.MapHub<AgentHub>(HubRoutes.Agent).RequireAuthorization(ApiAuthPolicies.Agent);

app.MapWorkItemEndpoints();
app.MapPipelineRunEndpoints();
app.MapConfigEndpoints();
app.MapConsolidationRunEndpoints();
app.MapConsolidationWorkItemEndpoints();
app.MapHarnessSuggestionEndpoints();
app.MapAgentEndpoints();
app.MapChatEndpoints();
app.MapApiSchedulerEndpoints();

// ── Startup DI validation ─────────────────────────────────────────────────────
// AssignmentEnricher is injected as optional [FromServices] in GetAssignment.
// If it is missing, new-schema work items silently receive a degraded identity-only 200
// response with no provider configs. Resolve eagerly to fail fast on misconfiguration.
_ = app.Services.GetRequiredService<AssignmentEnricher>();

await app.RunAsync();

// Make Program accessible for WebApplicationFactory in integration tests
public partial class Program { } // NOSONAR S1118 — required for WebApplicationFactory<Program> in integration tests

