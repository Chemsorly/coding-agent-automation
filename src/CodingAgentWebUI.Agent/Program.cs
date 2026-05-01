using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Infrastructure.Git;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using KiroCliLib.Configuration;
using KiroCliLib.Core;
using KiroCliLib.Models;
using Serilog;

// ── Read required environment variables early ──
var orchestratorUrl = Environment.GetEnvironmentVariable("ORCHESTRATOR_URL")
    ?? throw new InvalidOperationException("ORCHESTRATOR_URL environment variable is required");
var agentApiKey = Environment.GetEnvironmentVariable("AGENT_API_KEY")
    ?? throw new InvalidOperationException("AGENT_API_KEY environment variable is required");
var agentId = Environment.GetEnvironmentVariable("AGENT_ID")
    ?? Environment.MachineName;

// Validate AGENT_TYPE is set (AgentWorkerService reads it, but fail fast here)
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AGENT_TYPE")))
    throw new InvalidOperationException("AGENT_TYPE environment variable is required");

// ── Configure Serilog ──
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    // Suppress noisy ASP.NET Core request logging (health checks every 10s)
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("AgentId", agentId)
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] [{AgentId}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

try
{
    Log.Information("Agent Worker starting (AgentId={AgentId}, OrchestratorUrl={OrchestratorUrl})",
        agentId, orchestratorUrl);

    var builder = WebApplication.CreateBuilder(args);

    // Use Serilog
    builder.Host.UseSerilog();

    // ── KiroCliLib ──
    var kiroConfig = new Configuration
    {
        KiroCliPath = "/home/ubuntu/.local/bin/kiro-cli",
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

    // ── Provider factory (agent-side: no IIssueProvider) ──
    builder.Services.AddSingleton<IProviderFactory>(sp =>
    {
        var orchestrator = sp.GetRequiredService<IKiroCliOrchestrator>();
        var pipelineConfig = sp.GetRequiredService<PipelineConfiguration>();
        return new AgentProviderFactory(orchestrator, pipelineConfig);
    });

    // ── Quality gate validator ──
    builder.Services.AddSingleton<IQualityGateValidator>(sp => new QualityGateValidator(Log.Logger));

    // ── Brain update service ──
    builder.Services.AddSingleton<IBrainUpdateService>(sp => new BrainUpdateService(Log.Logger));

    // ── Hub connection manager ──
    builder.Services.AddSingleton(sp =>
        new HubConnectionManager(orchestratorUrl, agentId, agentApiKey, Log.Logger));

    // ── Pipeline executor ──
    builder.Services.AddSingleton(sp => new LocalPipelineExecutor(
        sp.GetRequiredService<IProviderFactory>(),
        sp.GetRequiredService<IQualityGateValidator>(),
        Log.Logger,
        sp.GetRequiredService<IBrainUpdateService>()));

    // ── Agent worker service (BackgroundService) ──
    builder.Services.AddSingleton(sp => new AgentWorkerService(
        sp.GetRequiredService<HubConnectionManager>(),
        sp.GetRequiredService<LocalPipelineExecutor>(),
        sp.GetRequiredService<IKiroCliOrchestrator>(),
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
