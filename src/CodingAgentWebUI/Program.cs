using CodingAgentWebUI.Components;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Models;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.SignalR;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Register services
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton(BuildInfo.Load());

// Pipeline — Configuration Store
var configStore = new JsonConfigurationStore(PipelineConstants.ConfigBaseDirectory);
builder.Services.AddSingleton<IConfigurationStore>(configStore);
builder.Services.AddSingleton<IPipelineConfigStore>(configStore);
builder.Services.AddSingleton<IProviderConfigStore>(configStore);
builder.Services.AddSingleton<IAgentProfileStore>(configStore);
builder.Services.AddSingleton<IQualityGateConfigStore>(configStore);
builder.Services.AddSingleton<IReviewerConfigStore>(configStore);

// Load pipeline config eagerly (before DI container is built) to avoid sync-over-async deadlocks
var pipelineConfig = await configStore.LoadPipelineConfigAsync(CancellationToken.None);

// Pipeline — Provider Factory (creates provider instances from ProviderConfig at runtime)
builder.Services.AddSingleton<IProviderFactory>(sp =>
{
    return new ProviderFactory(pipelineConfig);
});

// Pipeline — Services (shared registrations: IQualityGateValidator, IBrainUpdateService, IAgentPhaseExecutor, IQualityGateExecutor)
builder.Services.AddPipelineServices(Serilog.Log.Logger);
builder.Services.AddSingleton<IPipelineRunHistoryService>(sp => new PipelineRunHistoryService(Serilog.Log.Logger));
// Pipeline — Lifecycle Service (owns run state, events, transitions, cancellation)
builder.Services.AddSingleton(sp => new PipelineRunLifecycleService(
    sp.GetRequiredService<IPipelineRunHistoryService>(),
    sp.GetRequiredService<OrchestratorRunService>(),
    Serilog.Log.Logger));

builder.Services.AddSingleton(sp => new PipelineOrchestrationService(
    sp.GetRequiredService<IConfigurationStore>(),
    sp.GetRequiredService<IProviderFactory>(),
    sp.GetRequiredService<IssueDescriptionParser>(),
    sp.GetRequiredService<IAgentPhaseExecutor>(),
    sp.GetRequiredService<IQualityGateExecutor>(),
    Serilog.Log.Logger,
    sp.GetRequiredService<IBrainUpdateService>(),
    sp.GetRequiredService<IPipelineRunHistoryService>(),
    sp.GetRequiredService<OrchestratorRunService>(),
    sp.GetRequiredService<PipelineRunLifecycleService>(),
    sp.GetRequiredService<IQualityGateValidator>(),
    sp.GetRequiredService<ILabelSwapper>()));

// Pipeline — Loop Service (background service, starts dormant)
builder.Services.AddSingleton<IDependencyChecker>(sp => new DependencyChecker(Serilog.Log.Logger));
builder.Services.AddSingleton<PipelineLoopService>(sp => new PipelineLoopService(
    sp.GetRequiredService<PipelineOrchestrationService>(),
    sp.GetRequiredService<IProviderFactory>(),
    sp.GetRequiredService<IPipelineConfigStore>(),
    sp.GetRequiredService<IProviderConfigStore>(),
    Serilog.Log.Logger,
    sp.GetRequiredService<IJobDispatcher>(),
    sp.GetRequiredService<IDependencyChecker>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<PipelineLoopService>());

builder.Services.AddTransient<IssueDescriptionParser>();
builder.Services.AddTransient<GitHubValidationService>(sp =>
    new GitHubValidationService(sp.GetRequiredService<IProviderFactory>()));

// Multi-agent orchestrator services (singletons)
builder.Services.AddSingleton(sp => new AgentRegistryService(Serilog.Log.Logger));
builder.Services.AddSingleton(sp => new JobDispatcherService(
    sp.GetRequiredService<AgentRegistryService>(),
    Serilog.Log.Logger));
builder.Services.AddHttpClient("TokenVending");
builder.Services.AddSingleton<ITokenVendingService>(sp => new TokenVendingService(Serilog.Log.Logger, sp.GetRequiredService<IHttpClientFactory>()));
builder.Services.AddSingleton(sp => new OrchestratorRunService(
    Serilog.Log.Logger,
    pipelineConfig.OutputBufferCapacity));
builder.Services.AddSingleton<IOrchestratorRunService>(sp => sp.GetRequiredService<OrchestratorRunService>());
builder.Services.AddSingleton<ILabelSwapper>(sp => new LabelSwapper(
    sp.GetRequiredService<IConfigurationStore>(),
    sp.GetRequiredService<IProviderFactory>(),
    Serilog.Log.Logger));
builder.Services.AddHostedService(sp => new HeartbeatMonitorService(
    sp.GetRequiredService<AgentRegistryService>(),
    sp.GetRequiredService<OrchestratorRunService>(),
    sp.GetRequiredService<IPipelineRunHistoryService>(),
    sp.GetRequiredService<JobDispatcherService>(),
    sp.GetRequiredService<ILabelSwapper>(),
    sp.GetRequiredService<IConfigurationStore>(),
    Serilog.Log.Logger));

// Job queue drain service — periodically matches queued jobs to idle agents
builder.Services.AddSingleton<ConsolidationQueueService>(sp => new ConsolidationQueueService(
    Serilog.Log.Logger));
builder.Services.AddSingleton(sp => new JobQueueDrainService(
    sp.GetRequiredService<JobDispatcherService>(),
    sp.GetRequiredService<AgentRegistryService>(),
    sp.GetRequiredService<IJobDispatcher>(),
    sp.GetRequiredService<ConsolidationQueueService>(),
    sp.GetRequiredService<IConsolidationService>(),
    sp.GetRequiredService<IConsolidationDispatcher>(),
    Serilog.Log.Logger));
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobQueueDrainService>());

// Multi-agent job dispatcher (bridges loop service to agent dispatch)
builder.Services.AddSingleton<ProfileResolver>();
builder.Services.AddSingleton<QualityGateResolver>();
builder.Services.AddSingleton<ReviewerResolver>();

// Agent communication abstraction (wraps SignalR IHubContext)
builder.Services.AddSingleton<IAgentCommunication>(sp => new SignalRAgentCommunication(
    sp.GetRequiredService<IHubContext<AgentHub, IAgentHubClient>>()));

// Consolidation services
builder.Services.AddSingleton<IConsolidationDispatcher>(sp => new ConsolidationDispatcher(
    sp.GetRequiredService<AgentRegistryService>(),
    sp.GetRequiredService<JobDispatcherService>(),
    sp.GetRequiredService<IAgentCommunication>(),
    sp.GetRequiredService<IConfigurationStore>(),
    sp.GetRequiredService<ITokenVendingService>(),
    pipelineConfig,
    sp.GetRequiredService<ConsolidationQueueService>(),
    sp.GetRequiredService<IPipelineRunHistoryService>(),
    Serilog.Log.Logger));
builder.Services.AddSingleton<IConsolidationService>(sp => new ConsolidationService(
    Serilog.Log.Logger,
    pipelineConfig,
    sp.GetRequiredService<IPipelineRunHistoryService>(),
    sp.GetRequiredService<IConsolidationDispatcher>()));
builder.Services.AddSingleton<ConsolidationBadgeService>();

builder.Services.AddSingleton<ModelFetchService>(sp => new ModelFetchService(
    sp.GetRequiredService<AgentRegistryService>(),
    sp.GetRequiredService<IAgentCommunication>(),
    Serilog.Log.Logger));
builder.Services.AddSingleton<IJobDispatcher>(sp => new AgentJobDispatcher(
    sp.GetRequiredService<JobDispatcherService>(),
    sp.GetRequiredService<AgentRegistryService>(),
    sp.GetRequiredService<OrchestratorRunService>(),
    sp.GetRequiredService<PipelineOrchestrationService>(),
    sp.GetRequiredService<ITokenVendingService>(),
    sp.GetRequiredService<IConfigurationStore>(),
    sp.GetRequiredService<IProviderFactory>(),
    sp.GetRequiredService<ILabelSwapper>(),
    sp.GetRequiredService<ProfileResolver>(),
    sp.GetRequiredService<QualityGateResolver>(),
    sp.GetRequiredService<ReviewerResolver>(),
    sp.GetRequiredService<IAgentCommunication>(),
    Serilog.Log.Logger));

// AgentHub facade — groups registry, run state, dispatch, history, and issue provider operations
builder.Services.AddSingleton<IAgentHubFacade>(sp => new AgentHubFacade(
    sp.GetRequiredService<AgentRegistryService>(),
    sp.GetRequiredService<OrchestratorRunService>(),
    sp.GetRequiredService<JobDispatcherService>(),
    sp.GetRequiredService<JobQueueDrainService>(),
    sp.GetRequiredService<IPipelineRunHistoryService>(),
    sp.GetRequiredService<IConfigurationStore>(),
    sp.GetRequiredService<IProviderFactory>()));

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
    .WriteTo.Console(theme: Serilog.Sinks.SystemConsole.Themes.ConsoleTheme.None));

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
        .AddOtlpExporter());

var app = builder.Build();

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
