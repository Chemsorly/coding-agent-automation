using CodingAgentWebUI;
using CodingAgentWebUI.Components;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Telemetry;
using CodingAgentWebUI.Models;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
using Microsoft.AspNetCore.SignalR;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;

var builder = WebApplication.CreateBuilder(args);

// Register services
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton(BuildInfo.Load());

// Pipeline — Configuration Store (created eagerly to load config before DI container is built)
var configStore = new JsonConfigurationStore(PipelineConstants.ConfigBaseDirectory);
var pipelineConfig = await configStore.LoadPipelineConfigAsync(CancellationToken.None);

// Domain service registrations (extracted into focused extension methods)
builder.Services.AddInfrastructureServices(configStore, pipelineConfig);
builder.Services.AddPipelineServices(Serilog.Log.Logger);
builder.Services.AddPipelineCoreServices();
builder.Services.AddOrchestrationServices(pipelineConfig);
builder.Services.AddConsolidationServices(pipelineConfig);

// SignalR — hub services with MessagePack protocol
builder.Services.AddSignalR()
    .AddMessagePackProtocol();

// SignalR — hub filter for agent authorization
builder.Services.AddSingleton<IHubFilter>(sp => new AgentAuthorizationFilter(
    sp.GetRequiredService<AgentRegistryService>(),
    Serilog.Log.Logger));

// Agent API key authentication — NOT set as default scheme to avoid interfering with Blazor UI.
// When only one scheme is registered, ASP.NET Core auto-promotes it to the default scheme,
// causing UseAuthentication() to challenge every request (health checks, Blazor, static files).
// Explicitly clear the defaults so only endpoints with RequireAuthorization("AgentApiKey") trigger it.
var agentApiKey = AgentApiKeyAuthHandler.ResolveApiKey(Serilog.Log.Logger);
builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = null;
        options.DefaultChallengeScheme = null;
    })
    .AddScheme<AgentApiKeyAuthOptions, AgentApiKeyAuthHandler>(
        AgentApiKeyDefaults.AuthenticationScheme,
        options => options.ApiKey = agentApiKey);
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AgentApiKey", policy =>
        policy.AddAuthenticationSchemes(AgentApiKeyDefaults.AuthenticationScheme)
              .RequireAuthenticatedUser());
});

// Configure Serilog
builder.Host.UseSerilog((ctx, lc) => lc
    .MinimumLevel.Is(Serilog.Events.LogEventLevel.Information)
    // Suppress noisy ASP.NET Core framework logging (health checks, static files, Blazor negotiation, auth)
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithSpan()
    .WriteTo.Console(theme: Serilog.Sinks.SystemConsole.Themes.ConsoleTheme.None)
    .WriteToOtlpIfConfigured("coding-agent-orchestrator", ctx.HostingEnvironment.EnvironmentName));

// Configure OpenTelemetry (tracing + metrics)
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: "coding-agent-orchestrator",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddSource(PipelineTelemetry.SourceName)
        .AddOtlpExporter())
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
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
        .AddView("dispatch.queue.wait_time", new ExplicitBucketHistogramConfiguration
        {
            Boundaries = new double[] { 1, 5, 10, 30, 60, 120, 300, 600 }
        })
        .AddOtlpExporter());

var app = builder.Build();

// Register observable gauges for dispatch queue and agent concurrency metrics
var dispatcher = app.Services.GetRequiredService<JobDispatcherService>();
var agentRegistry = app.Services.GetRequiredService<AgentRegistryService>();
_ = PipelineTelemetry.Meter.CreateObservableGauge("dispatch.queue.depth",
    () => dispatcher.QueueLength, "{item}", "Jobs waiting for available agent");
_ = PipelineTelemetry.Meter.CreateObservableGauge("agent.jobs.active",
    () => agentRegistry.GetBusyAgentCount(), "{job}", "Currently executing agent jobs");
_ = PipelineTelemetry.Meter.CreateObservableGauge("agent.connections.total",
    () => agentRegistry.GetAllAgents().Count, "{connection}", "Total registered agents");

// Graceful shutdown: swap to agent:cancelled label before exit
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    var lifecycle = app.Services.GetRequiredService<PipelineRunLifecycleService>();
    var pipeline = app.Services.GetRequiredService<PipelineOrchestrationService>();

    if (lifecycle.IsRunning)
        lifecycle.CancelPipelineAsync().GetAwaiter().GetResult();

    // Label swaps require providers → stays on orchestration
    pipeline.CancelActiveAgentRunsAsync().GetAwaiter().GetResult();
});

// Kubernetes-style health probes — anonymous, no auth required
app.MapGet("/healthz", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }))
    .AllowAnonymous();
app.MapGet("/readyz", () => Results.Ok(new { status = "ready", timestamp = DateTime.UtcNow }))
    .AllowAnonymous();

// Redirect root "/" to the main page
app.MapGet("/", () => Results.Redirect("/agent-coding"))
    .AllowAnonymous();

// Export run history as JSON download
app.MapGet("/api/export/runs.json", (IPipelineRunHistoryService history, bool? feedbackOnly) =>
{
    var runs = (IEnumerable<PipelineRunSummary>)history.GetRunHistory();
    if (feedbackOnly == true)
        runs = runs.Where(r => r.Feedback is not null);

    var json = System.Text.Json.JsonSerializer.Serialize(runs.ToList(), PipelineJsonOptions.Default);
    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
    var fileName = $"pipeline-runs-{DateTime.UtcNow:yyyy-MM-dd}.json";
    return Results.File(bytes, "application/json", fileName);
}).AllowAnonymous();

app.UseStaticFiles();
app.MapStaticAssets();

app.UseAuthentication();
app.UseAuthorization();

// SignalR hub endpoint for agent connections
app.MapHub<AgentHub>(HubRoutes.Agent).RequireAuthorization("AgentApiKey");

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .DisableAntiforgery();

// Clean up orphaned consolidation runs from previous sessions
var consolidationService = app.Services.GetRequiredService<IConsolidationService>();
await consolidationService.CleanupOrphanedRunsAsync(CancellationToken.None);

// Rehydrate queued consolidation runs (re-enqueue them for dispatch)
var queuedRuns = await consolidationService.RehydrateQueuedRunsAsync(CancellationToken.None);
if (queuedRuns.Count > 0)
{
    var consolidationQueue = app.Services.GetRequiredService<ConsolidationQueueService>();
    foreach (var run in queuedRuns)
    {
        var job = new PendingConsolidationJob
        {
            RunId = run.RunId,
            Type = run.Type,
            TemplateId = run.TemplateId,
            WorkspacePath = Path.Combine(pipelineConfig.WorkspaceBaseDirectory, "consolidation", run.RunId),
            RequiredLabels = run.QueuedRequiredLabels ?? [],
            EnqueuedAt = new DateTimeOffset(run.StartedAtUtc, TimeSpan.Zero) // TODO: StartedAtUtc may deserialize with DateTimeKind.Unspecified; explicit UTC offset ensures correct semantics
        };
        consolidationQueue.EnqueueJob(job);
    }
}

app.Run();

// Make Program class accessible for WebApplicationFactory in tests
public partial class Program { }
