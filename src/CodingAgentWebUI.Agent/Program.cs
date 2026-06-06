using System.Net.Http.Headers;
using System.Text;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Agent.OpenCode;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Telemetry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
using KiroCliLib.Configuration;
using KiroCliLib.Core;
using KiroCliLib.Models;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;

// ── Read required environment variables early ──
var orchestratorUrl = Environment.GetEnvironmentVariable(AgentDefaults.EnvOrchestratorUrl)
    ?? throw new InvalidOperationException("ORCHESTRATOR_URL environment variable is required");
var agentApiKey = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentApiKey)
    ?? throw new InvalidOperationException("AGENT_API_KEY environment variable is required");
var agentId = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentId)
    ?? Environment.MachineName;

// Validate AGENT_TYPE is set (AgentWorkerService reads it, but fail fast here)
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentType)))
    throw new InvalidOperationException("AGENT_TYPE environment variable is required");

// ── Configure Serilog ──
var logLevel = Environment.GetEnvironmentVariable(AgentDefaults.EnvLogLevel)?.ToLowerInvariant() switch
{
    "debug" or "dbg" => Serilog.Events.LogEventLevel.Debug,
    "verbose" or "trace" => Serilog.Events.LogEventLevel.Verbose,
    "warning" or "warn" => Serilog.Events.LogEventLevel.Warning,
    "error" or "err" => Serilog.Events.LogEventLevel.Error,
    _ => Serilog.Events.LogEventLevel.Information
};
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(logLevel)
    // Suppress noisy ASP.NET Core request logging (health checks every 10s)
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    // Suppress noisy HttpClient logging (OpenCode health monitor polls every 5s)
    .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
    // Suppress HttpClientFactory handler lifecycle logging (cleanup cycle every 10s)
    .MinimumLevel.Override("Microsoft.Extensions.Http", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithSpan()
    .Enrich.WithProperty("AgentId", agentId)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{AgentId}] {Message:lj}{NewLine}{Exception}")
    .WriteToOtlpIfConfigured("coding-agent-worker")
    .CreateLogger();

try
{
    Log.Information("Agent Worker starting (AgentId={AgentId}, OrchestratorUrl={OrchestratorUrl})",
        agentId, orchestratorUrl);

    var builder = WebApplication.CreateBuilder(args);

    // Use Serilog
    builder.Host.UseSerilog();

    // Configure OpenTelemetry (tracing + metrics)
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(
            serviceName: "coding-agent-worker",
            serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"))
        .WithTracing(t => t
            .AddHttpClientInstrumentation()
            .AddSource(PipelineTelemetry.SourceName)
            .AddOtlpExporter())
        .WithMetrics(m => m
            .AddHttpClientInstrumentation()
            .AddMeter(PipelineTelemetry.SourceName)
            .AddView("pipeline.jobs.duration", new ExplicitBucketHistogramConfiguration
            {
                Boundaries = new double[] { 30, 60, 120, 300, 600, 900, 1800, 3600 }
            })
            .AddView("pipeline.step.duration", new ExplicitBucketHistogramConfiguration
            {
                Boundaries = new double[] { 5, 15, 30, 60, 120, 300, 600, 900, 1800 }
            })
            .AddView("quality_gate.duration", new ExplicitBucketHistogramConfiguration
            {
                Boundaries = new double[] { 5, 15, 30, 60, 120, 300, 600, 900 }
            })
            .AddView("quality_gate.external_ci.duration", new ExplicitBucketHistogramConfiguration
            {
                Boundaries = new double[] { 30, 60, 120, 300, 600, 900, 1800, 3600 }
            })
            .AddOtlpExporter());

    // ── KiroCliLib ──
    var kiroConfig = new Configuration
    {
        KiroCliPath = AgentDefaults.KiroCliPath,
        UseWsl = false, // Agent runs natively in Linux container
        WorkspaceDirectory = "/app/workspaces"
    };
    builder.Services.AddSingleton(kiroConfig);
    builder.Services.AddSingleton<IKiroCliOrchestrator>(sp =>
    {
        var cfg = sp.GetRequiredService<Configuration>();
        var callbackHandler = new CallbackHandler(Log.Logger);
        return new KiroCliOrchestrator(cfg, callbackHandler, Log.Logger);
    });

    // ── Pipeline configuration (will be overridden per-job, but needed for factory construction) ──
    var defaultPipelineConfig = new PipelineConfiguration();
    builder.Services.AddSingleton(defaultPipelineConfig);

    // ── Shared pipeline services (IQualityGateValidator, IBrainUpdateService, IAgentPhaseExecutor, IQualityGateExecutor) ──
    builder.Services.AddPipelineServices(Log.Logger);

    // ── OpenCode named HttpClient (always registered — safe when OPENCODE_SERVER_PASSWORD is absent) ──
    var agentProviderType = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentProviderType) ?? "";
    builder.Services.AddHttpClient(AgentDefaults.OpenCodeHttpClientName, (sp, client) =>
    {
        var baseUrl = Environment.GetEnvironmentVariable(AgentDefaults.EnvOpenCodeBaseUrl) ?? AgentDefaults.OpenCodeBaseUrl;
        client.BaseAddress = new Uri(baseUrl);
        // OpenCode message API blocks until the agent finishes — can take minutes for complex tasks
        client.Timeout = TimeSpan.FromMinutes(60);

        var password = Environment.GetEnvironmentVariable(AgentDefaults.EnvOpenCodeServerPassword);
        if (!string.IsNullOrEmpty(password))
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{password}")));
        }
    });

    // ── OpenCode health monitor (only when provider type is OpenCode) ──
    if (agentProviderType.Equals(AgentDefaults.OpenCodeHttpClientName, StringComparison.OrdinalIgnoreCase))
    {
        builder.Services.AddHostedService<OpenCodeHealthMonitor>(sp =>
            new OpenCodeHealthMonitor(sp.GetRequiredService<IHttpClientFactory>(), Log.Logger));
    }

    // ── Hub connection manager ──
    builder.Services.AddSingleton(sp =>
        new HubConnectionManager(orchestratorUrl, agentId, agentApiKey, Log.Logger));

    // ── Pipeline executor ──
    builder.Services.AddSingleton<IOpenIssueContextWriter>(sp => new OpenIssueContextWriter(Log.Logger));
    builder.Services.AddSingleton(sp => new LocalPipelineExecutor(
        sp.GetRequiredService<IKiroCliOrchestrator>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<PipelineConfiguration>(),
        sp.GetRequiredService<IQualityGateValidator>(),
        Log.Logger,
        agentId,
        sp.GetRequiredService<IBrainUpdateService>(),
        openIssueContextWriter: sp.GetRequiredService<IOpenIssueContextWriter>()));

    // ── Consolidation executor ──
    builder.Services.AddSingleton(sp => new LocalConsolidationExecutor(
        sp.GetRequiredService<IKiroCliOrchestrator>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        Log.Logger));

    // ── Agent worker service (BackgroundService) ──
    builder.Services.AddSingleton(sp => new AgentWorkerService(
        sp.GetRequiredService<HubConnectionManager>(),
        sp.GetRequiredService<LocalPipelineExecutor>(),
        sp.GetRequiredService<LocalConsolidationExecutor>(),
        sp.GetRequiredService<IKiroCliOrchestrator>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        Log.Logger));
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentWorkerService>());

    var app = builder.Build();

    // ── Health endpoints (Kubernetes probes) ──
    app.MapHealthEndpoints();

    // Mark startup complete once the host is listening
    app.Lifetime.ApplicationStarted.Register(HealthEndpoints.MarkStarted);

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Agent Worker terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
