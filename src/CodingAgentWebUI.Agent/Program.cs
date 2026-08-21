using System.Net.Http.Headers;
using System.Text;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Agent.OpenCode;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Telemetry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
using KiroCliLib.Configuration;
using KiroCliLib.Core;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

// ── Resolve startup configuration ──
var startupConfig = await AgentStartupConfig.ResolveAsync(args);

// ── Configure Serilog ──
Log.Logger = AgentSerilogConfiguration.CreateAgentLogger(startupConfig.AgentId);

try
{
    Log.Information("Agent Worker starting (AgentId={AgentId}, OrchestratorUrl={OrchestratorUrl}, Mode={Mode})",
        startupConfig.AgentId, startupConfig.OrchestratorUrl, startupConfig.IsWorkItemMode ? "WorkItem" : "Chat");

    var builder = WebApplication.CreateBuilder(args);

    // Use Serilog
    builder.Host.UseSerilog();
    builder.Services.AddSingleton(Log.Logger);

    // Configure OpenTelemetry (tracing + metrics)
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(
            serviceName: Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "coding-agent-worker",
            serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"))
        .WithTracing(t => t
            .AddHttpClientInstrumentation()
            .AddSource(PipelineTelemetry.SourceName)
            .AddOtlpExporter())
        .WithMetrics(m => m
            .AddHttpClientInstrumentation()
            .AddMeter(PipelineTelemetry.SourceName)
            // OTLP exporter defaults to Delta temporality, but Prometheus (and Grafana Cloud's OTLP
            // receiver) require Cumulative. Without this, histograms and counters are silently dropped
            // by the Grafana collector. The two-argument overload is only available inside WithMetrics().
            .AddOtlpExporter((_, readerOptions) =>
                readerOptions.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative));

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
        return new KiroCliOrchestrator(cfg, Log.Logger);
    });

    // ── Pipeline configuration (will be overridden per-job, but needed for factory construction) ──
    var defaultPipelineConfig = new PipelineConfiguration();
    builder.Services.AddSingleton(defaultPipelineConfig);

    // ── Null-safe history service (agent doesn't maintain run history) ──
    builder.Services.AddSingleton<IPipelineRunHistoryService, NullPipelineRunHistoryService>();

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

    // ── Agent identity (single source of truth for AGENT_ID) ──
    builder.Services.Add(ServiceDescriptor.Singleton(typeof(AgentId), startupConfig.AgentId));

    // ── Hub connection manager ──
    builder.Services.AddSingleton(sp =>
        new HubConnectionManagerFactory(startupConfig.OrchestratorUrl, startupConfig.AgentId, startupConfig.AgentApiKey, Log.Logger));
    builder.Services.AddSingleton(sp =>
        sp.GetRequiredService<HubConnectionManagerFactory>().Create());

    // ── Pipeline executor ──
    builder.Services.AddSingleton<IOpenIssueContextWriter>(sp => new OpenIssueContextWriter(Log.Logger));
    builder.Services.AddSingleton<IPipelineReporterFactory>(sp => new PipelineReporterFactory(Log.Logger));
    builder.Services.AddSingleton<IPipelineExecutor>(sp => new LocalPipelineExecutor(
        new LocalPipelineExecutorDependencies(
            sp.GetRequiredService<IKiroCliOrchestrator>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<PipelineConfiguration>(),
            sp.GetRequiredService<IQualityGateValidator>(),
            Log.Logger,
            sp.GetRequiredService<IBrainUpdateService>(),
            OpenIssueContextWriter: sp.GetRequiredService<IOpenIssueContextWriter>(),
            AgentIdentity: sp.GetRequiredService<AgentId>(),
            ReporterFactory: sp.GetRequiredService<IPipelineReporterFactory>())));

    // ── Consolidation executor ──
    builder.Services.AddSingleton<IConsolidationExecutor>(sp => new LocalConsolidationExecutor(
        sp.GetRequiredService<IKiroCliOrchestrator>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        Log.Logger));

    // ── Agent worker service (mode-conditional) ──
    if (startupConfig.IsWorkItemMode)
        builder.Services.AddK8sModeServices(startupConfig, Log.Logger);
    else
        builder.Services.AddSignalRModeServices(Log.Logger);

    var app = builder.Build();

    // ── Health endpoints (Kubernetes probes) ──
    app.MapHealthEndpoints();

    // Mark startup complete once the host is listening
    app.Lifetime.ApplicationStarted.Register(HealthEndpoints.MarkStarted);

    // ── SIGTERM handler for work-item mode ──
    if (startupConfig.IsWorkItemMode)
    {
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            Log.Information("SIGTERM received, cancelling pipeline for work item {WorkItemId}", startupConfig.WorkItemId);
            var workItemService = app.Services.GetService<WorkItemAgentService>();
            workItemService?.CancelPipeline();
        });
    }

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Agent Worker terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}
