using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Scheduler;
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

// ── Startup identity log ──────────────────────────────────────────────────
var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
var serviceName = builder.Configuration.GetValue<string>("OTEL_SERVICE_NAME") ?? "coding-agent-scheduler";
Log.Information("Scheduler starting: ServiceName={ServiceName} Version={Version}", serviceName, version);

// ── Fast-fail: Pipeline API URL required ─────────────────────────────────
var pipelineApiBaseUrl = builder.Configuration.GetValue<string>("PipelineApi__BaseUrl")
    ?? builder.Configuration.GetValue<string>("PipelineApi:BaseUrl");
if (string.IsNullOrEmpty(pipelineApiBaseUrl))
{
    Log.Fatal("PipelineApi__BaseUrl is not configured. The Scheduler requires the Pipeline API. Exiting.");
    return;
}

// ── Fast-fail: agent API key required ────────────────────────────────────
var agentApiKey = builder.Configuration.GetValue<string>("AGENT_API_KEY")
    ?? builder.Configuration.GetValue<string>("AgentApiKey");
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
builder.Host.UseSerilog((ctx, services, loggerConfig) =>
{
    loggerConfig
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithSpan()
        .WriteTo.Console(theme: Serilog.Sinks.SystemConsole.Themes.ConsoleTheme.None);
}, preserveStaticLogger: true);

// ── Port 8091 ─────────────────────────────────────────────────────────────
builder.WebHost.UseUrls("http://+:8091"); // NOSONAR S1075

// ── OpenTelemetry ─────────────────────────────────────────────────────────
var otelServiceName = builder.Configuration.GetValue<string>("OTEL_SERVICE_NAME") ?? "coding-agent-scheduler";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: otelServiceName,
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"))
    .WithTracing(t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation()
        .AddMeter(CodingAgentWebUI.Pipeline.Telemetry.WorkDistributionTelemetry.MeterName)
        .AddOtlpExporter());

var app = builder.Build();

// ── Health endpoint ───────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }))
   .WithName("SchedulerHealth").AllowAnonymous();

// ── Loop control endpoints ────────────────────────────────────────────────
app.MapSchedulerLoopEndpoints();

// ── Auto-start pipeline loop if configured ────────────────────────────────
await app.AutoStartSchedulerLoopAsync();

await app.RunAsync();
