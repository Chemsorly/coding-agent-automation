using CodingAgent.Api.Client;
using CodingAgent.Infrastructure.GitHub;
using CodingAgent.Infrastructure.Telemetry;
using CodingAgent.Pipeline.Services;
using CodingAgent.Scheduler;
using Serilog;
using Serilog.Events;

// Bootstrap logger: captures log output before UseSerilog takes over
Log.Logger = HostBootstrap.CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

// ── Startup identity log ──────────────────────────────────────────────────
var version = Environment.GetEnvironmentVariable("SERVICE_VERSION") ?? "local";
var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "coding-agent-scheduler";
HostBootstrap.LogStartupIdentity("Scheduler", serviceName, version);

// ── Fast-fail: Pipeline API URL required ─────────────────────────────────
var pipelineApiBaseUrl = builder.Configuration.GetValue<string>("PipelineApi__BaseUrl")
    ?? builder.Configuration.GetValue<string>("PipelineApi:BaseUrl");
if (string.IsNullOrEmpty(pipelineApiBaseUrl))
{
    Log.Fatal("PipelineApi__BaseUrl is not configured. The Scheduler requires the Pipeline API. Exiting.");
    return;
}

// ── Fast-fail: agent API key required ────────────────────────────────────
var agentApiKey = builder.Configuration.GetValue<string>("AGENT_API_KEY");
if (string.IsNullOrEmpty(agentApiKey))
{
    Log.Fatal("AGENT_API_KEY is not configured. Exiting.");
    return;
}

// ── Configure JSON serialization ─────────────────────────────────────────
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// ── Host shutdown timeout ─────────────────────────────────────────────────
builder.Services.Configure<HostOptions>(opts =>
{
    opts.ShutdownTimeout = TimeSpan.FromSeconds(60);
    opts.ServicesStartConcurrently = false; // ordered startup
});

// Validate DI on build — catches missing registrations before the service starts
builder.Host.UseDefaultServiceProvider(opts =>
{
    opts.ValidateOnBuild = true;
    opts.ValidateScopes = true;
});

// ── Service registrations ─────────────────────────────────────────────────
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSchedulerServices(pipelineApiBaseUrl, agentApiKey, builder.Configuration);

// ── Serilog ───────────────────────────────────────────────────────────────
var schedulerLogLevel = LogLevelParser.Parse(
    Environment.GetEnvironmentVariable("LOG_LEVEL"),
    LogEventLevel.Information);

builder.Host.UseSerilog((ctx, lc) => lc
    .ApplyHostDefaults(schedulerLogLevel)
    .WriteToHostConsole()
    .WriteToOtlpIfConfigured("coding-agent-scheduler", ctx.HostingEnvironment.EnvironmentName));

// ── Port 8080 ─────────────────────────────────────────────────────────────
builder.WebHost.UseUrls("http://+:8080"); // NOSONAR S1075

// ── OpenTelemetry ─────────────────────────────────────────────────────────
builder.Services.AddHostOpenTelemetry(
    "coding-agent-scheduler",
    configureMetrics: m => m
        // GitHub-facing metrics (github.api.requests counter, github.rate_limit.remaining gauge).
        // Not registered in the agent — agent pods must not emit these series.
        .AddMeter(GitHubTelemetry.MeterName));

var app = builder.Build();

// Seed the github.api.requests and pipeline.pull_requests.closed series with 0 so Prometheus
// increase() sees the first real increment after a deploy.
MetricPreInitialization.Run(app.Services, () => GitHubTelemetry.PreInitialize());

// ── Health probes ─────────────────────────────────────────────────────────
// /healthz — startup/liveness, /readyz — readiness, /health — Dockerfile HEALTHCHECK compat.
// Endpoint lambdas live in SchedulerHealthEndpoints so tests can call the same method
// instead of re-declaring inline copies (which would test routing, not production code).
app.MapSchedulerHealthEndpoints();

// ── Loop control endpoints ────────────────────────────────────────────────
app.MapSchedulerLoopEndpoints();

// ── Auto-start pipeline loop if configured ────────────────────────────────
await app.AutoStartSchedulerLoopAsync();

await app.RunAsync();
