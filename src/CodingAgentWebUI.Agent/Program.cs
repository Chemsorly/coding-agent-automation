using System.Net.Http.Headers;
using System.Text;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Agent.OpenCode;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Infrastructure.Telemetry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
using KiroCliLib.Configuration;
using KiroCliLib.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Http.Resilience;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Polly;
using Serilog;
using Serilog.Enrichers.Span;

// ── Determine startup mode from CLI args ──
var workItemId = args
    .FirstOrDefault(a => a.StartsWith(AgentDefaults.CliWorkItemIdPrefix, StringComparison.OrdinalIgnoreCase))
    ?.Substring(AgentDefaults.CliWorkItemIdPrefix.Length);
var isK8sMode = workItemId is not null;

// ── Read API key: from file (K8s mode) or env var (SignalR mode) ──
string agentApiKey;
var apiKeyFilePath = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentApiKeyFile);
if (!string.IsNullOrEmpty(apiKeyFilePath))
{
    // K8s mode: read from mounted Secret file
    agentApiKey = (await File.ReadAllTextAsync(apiKeyFilePath)).Trim();
}
else
{
    agentApiKey = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentApiKey)
        ?? throw new InvalidOperationException(
            $"Neither {AgentDefaults.EnvAgentApiKeyFile} nor {AgentDefaults.EnvAgentApiKey} is set. " +
            "Provide --work-item-id={{id}} with AGENT_API_KEY_FILE, or AGENT_API_KEY for SignalR mode.");
}

// ── Read required environment variables early ──
var orchestratorUrl = Environment.GetEnvironmentVariable(AgentDefaults.EnvOrchestratorUrl)
    ?? throw new InvalidOperationException("ORCHESTRATOR_URL environment variable is required");
var agentId = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentId)
    ?? Environment.MachineName;

// ── Validate startup mode (fail within 10s if neither mode can be determined) ──
if (!isK8sMode && string.IsNullOrEmpty(orchestratorUrl))
{
    // Neither --work-item-id nor SignalR env vars: fail fast
    throw new InvalidOperationException(
        "Agent startup mode cannot be determined. Provide --work-item-id={id} for K8s mode, " +
        "or set ORCHESTRATOR_URL + AGENT_API_KEY for SignalR mode.");
}

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
    Log.Information("Agent Worker starting (AgentId={AgentId}, OrchestratorUrl={OrchestratorUrl}, Mode={Mode})",
        agentId, orchestratorUrl, isK8sMode ? "K8s" : "SignalR");

    var builder = WebApplication.CreateBuilder(args);

    // Use Serilog
    builder.Host.UseSerilog();
    builder.Services.AddSingleton(Log.Logger);

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
    builder.Services.AddSingleton(new AgentIdentity(agentId));

    // ── Hub connection manager ──
    builder.Services.AddSingleton(sp =>
        new HubConnectionManagerFactory(orchestratorUrl, agentId, agentApiKey, Log.Logger));
    builder.Services.AddSingleton(sp =>
        sp.GetRequiredService<HubConnectionManagerFactory>().Create());

    // ── Pipeline executor ──
    builder.Services.AddSingleton<IOpenIssueContextWriter>(sp => new OpenIssueContextWriter(Log.Logger));
    builder.Services.AddSingleton<IPipelineReporterFactory>(sp => new PipelineReporterFactory(Log.Logger));
    builder.Services.AddSingleton<IPipelineExecutor>(sp => new LocalPipelineExecutor(
        sp.GetRequiredService<IKiroCliOrchestrator>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        sp.GetRequiredService<PipelineConfiguration>(),
        sp.GetRequiredService<IQualityGateValidator>(),
        Log.Logger,
        sp.GetRequiredService<IBrainUpdateService>(),
        openIssueContextWriter: sp.GetRequiredService<IOpenIssueContextWriter>(),
        agentIdentity: sp.GetRequiredService<AgentIdentity>(),
        reporterFactory: sp.GetRequiredService<IPipelineReporterFactory>()));

    // ── Consolidation executor ──
    builder.Services.AddSingleton<IConsolidationExecutor>(sp => new LocalConsolidationExecutor(
        sp.GetRequiredService<IKiroCliOrchestrator>(),
        sp.GetRequiredService<IHttpClientFactory>(),
        Log.Logger));

    // ── Agent worker service (mode-conditional) ──
    if (isK8sMode)
    {
        // K8s mode: register WorkItemHttpClient and WorkItemAgentService
        builder.Services.AddHttpClient<WorkItemHttpClient>(client =>
        {
            client.BaseAddress = new Uri(orchestratorUrl.TrimEnd('/'));
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", agentApiKey);
            // DO NOT set client.Timeout — resilience handler manages timeouts
        })
        .AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(90);
            options.Retry.MaxRetryAttempts = 5;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        });

        builder.Services.AddSingleton<IWorkItemExecutor>(sp => new WorkItemExecutorRouter(
            sp.GetRequiredService<IPipelineExecutor>(),
            sp.GetRequiredService<IConsolidationExecutor>(),
            Log.Logger));

        builder.Services.AddSingleton<IWorkItemLifecycleClient>(sp =>
            sp.GetRequiredService<WorkItemHttpClient>());

        builder.Services.AddSingleton<IAgentConnectionManager>(sp => new AgentConnectionManager(
            sp.GetRequiredService<HubConnectionManager>(),
            sp.GetRequiredService<HubConnectionManagerFactory>(),
            sp.GetRequiredService<AgentIdentity>(),
            Log.Logger));

        builder.Services.AddSingleton<IJobCompletionReporter>(sp => new HttpPrimaryCompletionReporter(
            workItemId!,
            sp.GetRequiredService<IWorkItemLifecycleClient>(),
            sp.GetRequiredService<IAgentConnectionManager>(),
            sp.GetRequiredService<AgentIdentity>(),
            Log.Logger));

        builder.Services.AddSingleton(sp => new WorkItemAgentService(
            workItemId!,
            sp.GetRequiredService<IWorkItemLifecycleClient>(),
            sp.GetRequiredService<IAgentConnectionManager>(),
            sp.GetRequiredService<IWorkItemExecutor>(),
            sp.GetRequiredService<IJobCompletionReporter>(),
            sp.GetRequiredService<AgentIdentity>(),
            sp.GetRequiredService<IHostApplicationLifetime>(),
            Log.Logger));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkItemAgentService>());
        builder.Services.AddSingleton<IAgentService>(sp => sp.GetRequiredService<WorkItemAgentService>());
    }
    else
    {
        // SignalR mode: register AgentWorkerService with extracted components
        builder.Services.AddSingleton<CriticalMessageBuffer>();
        builder.Services.AddSingleton<SignalRCompletionReporter>(sp => new SignalRCompletionReporter(
            sp.GetRequiredService<HubConnectionManager>(),
            ResiliencePipelineFactory.CreateSignalRPipeline(Log.Logger),
            sp.GetRequiredService<CriticalMessageBuffer>(),
            Log.Logger));
        builder.Services.AddSingleton<IJobCompletionReporter>(sp => sp.GetRequiredService<SignalRCompletionReporter>());
        builder.Services.AddSingleton<AgentJobSlotManager>(sp =>
        {
            // Use lazy resolution to break the circular dependency:
            // AgentJobSlotManager -> AgentConnectionLifecycle -> AgentJobSlotManager.
            // The signalReady callback is only invoked at runtime (after DI construction),
            // so lazy resolution is safe here.
            var agentId = sp.GetRequiredService<AgentIdentity>().Id;
            return new AgentJobSlotManager(async () =>
            {
                try
                {
                    var connectionLifecycle = sp.GetRequiredService<AgentConnectionLifecycle>();
                    await connectionLifecycle.Connection.InvokeAsync(
                        HubMethodNames.AgentReady, agentId);
                }
                catch (Exception ex)
                {
                    Log.Logger.Warning(ex, "Failed to send AgentReady signal");
                }
            });
        });
        builder.Services.AddSingleton<AgentConnectionLifecycle>(sp => new AgentConnectionLifecycle(
            sp.GetRequiredService<HubConnectionManager>(),
            sp.GetRequiredService<HubConnectionManagerFactory>(),
            sp.GetRequiredService<SignalRCompletionReporter>(),
            sp.GetRequiredService<AgentJobSlotManager>(),
            sp.GetRequiredService<AgentIdentity>(),
            sp.GetRequiredService<IHostApplicationLifetime>(),
            Log.Logger));
        builder.Services.AddSingleton(sp => new AgentWorkerService(
            sp.GetRequiredService<AgentConnectionLifecycle>(),
            sp.GetRequiredService<AgentJobSlotManager>(),
            sp.GetRequiredService<AgentIdentity>(),
            sp.GetRequiredService<IPipelineExecutor>(),
            sp.GetRequiredService<IConsolidationExecutor>(),
            sp.GetRequiredService<IJobCompletionReporter>(),
            sp.GetRequiredService<IKiroCliOrchestrator>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            Log.Logger));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentWorkerService>());
        builder.Services.AddSingleton<IAgentService>(sp => sp.GetRequiredService<AgentWorkerService>());
    }

    var app = builder.Build();

    // ── Health endpoints (Kubernetes probes) ──
    app.MapHealthEndpoints();

    // Mark startup complete once the host is listening
    app.Lifetime.ApplicationStarted.Register(HealthEndpoints.MarkStarted);

    // ── SIGTERM handler for K8s mode ──
    if (isK8sMode)
    {
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            Log.Information("SIGTERM received, cancelling pipeline for work item {WorkItemId}", workItemId);
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
