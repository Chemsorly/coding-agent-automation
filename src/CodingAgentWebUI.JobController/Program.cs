using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.JobController;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.Pipeline.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;

// Bootstrap logger — captures startup log output before UseSerilog takes over
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level}] {Message:lj}{NewLine}{Exception}",
        theme: Serilog.Sinks.SystemConsole.Themes.ConsoleTheme.None)
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

// ── Fast-fail: Pipeline API URL ──────────────────────────────────────────────
var apiBaseUrl = builder.Configuration.GetValue<string>("PipelineApi:BaseUrl");
if (string.IsNullOrEmpty(apiBaseUrl))
{
    Log.Fatal("PipelineApi:BaseUrl is not configured. The Job Controller requires the Pipeline API URL. Exiting.");
    return;
}

// ── Fast-fail: Agent API key ─────────────────────────────────────────────────
var agentApiKey = builder.Configuration.GetValue<string>("AGENT_API_KEY");
if (string.IsNullOrEmpty(agentApiKey))
{
    Log.Fatal("AGENT_API_KEY is not configured. Exiting.");
    return;
}

// ── Fast-fail: in-cluster guard ──────────────────────────────────────────────
// The Job Controller MUST run inside a Kubernetes cluster.
// Bypass in Test environment only (WebApplicationFactory sets ASPNETCORE_ENVIRONMENT=Test).
if (!string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"), "Test",
        StringComparison.OrdinalIgnoreCase)
    && !File.Exists("/var/run/secrets/kubernetes.io/serviceaccount/token"))
{
    Log.Fatal("Not running inside a Kubernetes cluster. " +
              "Set ASPNETCORE_ENVIRONMENT=Test to bypass in unit/integration tests. Exiting.");
    return;
}

// ── Startup identity log ─────────────────────────────────────────────────────
var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
var serviceName = builder.Configuration.GetValue<string>("OTEL_SERVICE_NAME") ?? "coding-agent-jobcontroller";
Log.Information("Job Controller starting: ServiceName={ServiceName} Version={Version}", serviceName, version);

// ── Pipeline API client ───────────────────────────────────────────────────────
builder.Services.AddPipelineApiClient(new PipelineApiClientOptions
{
    BaseUrl = apiBaseUrl,
    AgentApiKey = agentApiKey
});

// ── Job Controller services ───────────────────────────────────────────────────
builder.Services.AddJobControllerServices(builder.Configuration);

// ── Serilog ───────────────────────────────────────────────────────────────────
// Parse LOG_LEVEL env var — inline to avoid Infrastructure dependency
var logLevel = Enum.TryParse<Serilog.Events.LogEventLevel>(
    Environment.GetEnvironmentVariable("LOG_LEVEL"), ignoreCase: true, out var parsedLevel)
    ? parsedLevel
    : Serilog.Events.LogEventLevel.Information;

builder.Host.UseSerilog((ctx, lc) => lc
    .MinimumLevel.Is(logLevel)
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithSpan()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level}] {Message:lj}{NewLine}{Exception}",
        theme: Serilog.Sinks.SystemConsole.Themes.ConsoleTheme.None)
    .WriteToOtlpIfConfigured("coding-agent-jobcontroller", ctx.HostingEnvironment.EnvironmentName));

// ── OpenTelemetry ─────────────────────────────────────────────────────────────
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: serviceName,
        serviceVersion: version))
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
         // The Job Controller owns the dispatch loop — RecordLastPollEpoch, UpdateCredentialPoolMetrics,
         // RecordDispatchLatency and LogTerminalStatus are all called from DispatchLoop/ReconciliationLoop.
         // Without this AddMeter, those measurements are taken but never exported.
         .AddMeter(WorkDistributionTelemetry.MeterName)
         // Prometheus requires Cumulative temporality; the OTLP exporter defaults to Delta for
         // histograms and counters, which Grafana Cloud silently drops.
         .AddOtlpExporter((_, readerOptions) =>
             readerOptions.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative);
    });

// ── ASPNETCORE_URLS defaults to port 8091 ────────────────────────────────────
builder.WebHost.UseUrls("http://+:8091"); // NOSONAR S1075 — port is runtime infrastructure config, not a business URL

// ── Shutdown timeout ─────────────────────────────────────────────────────────
builder.Services.Configure<HostOptions>(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(30));

var app = builder.Build();

// ── Health probes ─────────────────────────────────────────────────────────────
app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));
// /readyz returns 200 for all replicas regardless of leader state.
// Leader election gates the actual dispatch and reconciliation work inside
// DispatchService and ReconciliationService — the non-leader idles and is still
// healthy. Returning 503 for non-leaders would keep the pod stuck as 0/1 Ready
// in multi-replica deployments (strategy: RollingUpdate) and is incorrect: the
// non-leader is not degraded, just standing by.
app.MapGet("/readyz", () => Results.Ok(new { status = "ready" }));

await app.RunAsync();

// Make Program accessible for WebApplicationFactory in integration tests
public partial class Program { } // NOSONAR S1118 — required for WebApplicationFactory<Program> in integration tests
