using CodingAgent.Agent;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.Telemetry;
using CodingAgent.Pipeline;
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

    // Configure OpenTelemetry (tracing only — agent pods record no metrics after #2967/#2974/#2978/#2979
    // migrated all metric recording to the API; see issue #2980)
    builder.Services.AddHostOpenTelemetry(
        "coding-agent-worker",
        includeAspNetCoreInstrumentation: false,
        includeMetrics: false);

    // ── Shared agent host services (logger, KiroCliLib, pipeline, runtime options, hub, executors) ──
    var agentProviderType = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentProviderType) ?? "";
    builder.Services.AddAgentHostServices(startupConfig, agentProviderType, Log.Logger);

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
            // Fires on every host stop, after a finished run as well as on SIGTERM.
            Log.Information("Host stopping, cancelling pipeline for work item {WorkItemId}", startupConfig.WorkItemId);
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
