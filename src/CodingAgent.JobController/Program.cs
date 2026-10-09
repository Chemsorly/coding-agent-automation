using CodingAgent.Api.Client;
using CodingAgent.Infrastructure.GitHub;
using CodingAgent.Infrastructure.Telemetry;
using CodingAgent.JobController;
using CodingAgent.Pipeline.LeaderElection;
using CodingAgent.Pipeline.Telemetry;
using Serilog;
using Serilog.Events;

// Bootstrap logger — captures startup log output before UseSerilog takes over
Log.Logger = HostBootstrap.CreateBootstrapLogger();

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
// TODO: This identity log is placed after the fast-fail guards, so if any guard fires the
// startup line is never emitted. The other three hosts (Api, Scheduler, Web) emit the identity
// log before their guards, enabling operators to distinguish "process started then failed config
// check" from "process crashed before logging". Move this block above the fast-fail section
// (i.e. immediately after Log.Logger = HostBootstrap.CreateBootstrapLogger()) to make
// JobController consistent with its siblings. (review-findings #2865)
var version = Environment.GetEnvironmentVariable("SERVICE_VERSION") ?? "local";
var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "coding-agent-jobcontroller";
HostBootstrap.LogStartupIdentity("Job Controller", serviceName, version);

// ── Pipeline API client ───────────────────────────────────────────────────────
builder.Services.AddPipelineApiClient(new PipelineApiClientOptions
{
    BaseUrl = apiBaseUrl,
    AgentApiKey = agentApiKey
});

// ── Job Controller services ───────────────────────────────────────────────────
builder.Services.AddJobControllerServices(builder.Configuration);

// ── Serilog ───────────────────────────────────────────────────────────────────
var logLevel = LogLevelParser.Parse(
    Environment.GetEnvironmentVariable("LOG_LEVEL"),
    LogEventLevel.Information);

builder.Host.UseSerilog((ctx, lc) => lc
    .ApplyHostDefaults(logLevel)
    .WriteToHostConsole()
    .WriteToOtlpIfConfigured("coding-agent-jobcontroller", ctx.HostingEnvironment.EnvironmentName));

// ── OpenTelemetry ─────────────────────────────────────────────────────────────
builder.Services.AddHostOpenTelemetry(
    "coding-agent-jobcontroller",
    configureMetrics: m => m
        // The Job Controller owns the reconciliation loop. ReconciliationLoop records
        // workdistribution.timeout_execution_age_seconds, workdistribution.timeout_canary_violations,
        // and workdistribution.agent_timeouts via factory-created instruments on this meter.
        // GitHub-facing metrics (github.api.requests counter, github.rate_limit.remaining gauge).
        // Not registered in the agent — agent pods must not emit these series.
        .AddMeter(GitHubTelemetry.MeterName));

// ── ASPNETCORE_URLS defaults to port 8080 ────────────────────────────────────
builder.WebHost.UseUrls("http://+:8080"); // NOSONAR S1075 — port is runtime infrastructure config, not a business URL

// ── Shutdown timeout ─────────────────────────────────────────────────────────
builder.Services.Configure<HostOptions>(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(30));

var app = builder.Build();

// ── Health probes ─────────────────────────────────────────────────────────────
app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }));
// /readyz returns 200 for all replicas regardless of leader state.
// Leader election gates the actual reconciliation work inside
// ReconciliationService — the non-leader idles and is still
// healthy. Returning 503 for non-leaders would keep the pod stuck as 0/1 Ready
// in multi-replica deployments (strategy: RollingUpdate) and is incorrect: the
// non-leader is not degraded, just standing by.
app.MapGet("/readyz", () => Results.Ok(new { status = "ready" }));

await app.RunAsync();

// Make Program accessible for WebApplicationFactory in integration tests
public partial class Program { } // NOSONAR S1118 — required for WebApplicationFactory<Program> in integration tests
