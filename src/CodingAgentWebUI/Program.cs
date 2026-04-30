using CodingAgentWebUI.Components;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Infrastructure.Git;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Models;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.SignalR;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Register services
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton(BuildInfo.Load());

// Pipeline — Configuration Store
var configStore = new JsonConfigurationStore("config/pipeline");
builder.Services.AddSingleton<IConfigurationStore>(configStore);

// Load pipeline config eagerly (before DI container is built) to avoid sync-over-async deadlocks
var pipelineConfig = await configStore.LoadPipelineConfigAsync(CancellationToken.None);

// Pipeline — Provider Factory (creates provider instances from ProviderConfig at runtime)
builder.Services.AddSingleton<IProviderFactory>(sp =>
{
    return new ProviderFactory(pipelineConfig);
});

// Pipeline — Services
builder.Services.AddSingleton<IBrainUpdateService>(sp => new BrainUpdateService(Serilog.Log.Logger));
builder.Services.AddSingleton<IPipelineRunHistoryService>(sp => new PipelineRunHistoryService(Serilog.Log.Logger));
builder.Services.AddSingleton(sp => new PipelineOrchestrationService(
    sp.GetRequiredService<IConfigurationStore>(),
    sp.GetRequiredService<IProviderFactory>(),
    sp.GetRequiredService<IssueDescriptionParser>(),
    sp.GetRequiredService<IQualityGateValidator>(),
    sp.GetRequiredService<CiLogWriter>(),
    Serilog.Log.Logger,
    sp.GetRequiredService<IBrainUpdateService>(),
    sp.GetRequiredService<IPipelineRunHistoryService>(),
    sp.GetRequiredService<OrchestratorRunService>()));

// Pipeline — Loop Service (background service, starts dormant)
builder.Services.AddSingleton<PipelineLoopService>(sp => new PipelineLoopService(
    sp.GetRequiredService<PipelineOrchestrationService>(),
    sp.GetRequiredService<IProviderFactory>(),
    sp.GetRequiredService<IConfigurationStore>(),
    Serilog.Log.Logger,
    sp.GetRequiredService<IJobDispatcher>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<PipelineLoopService>());

// TODO: [ARC-12] Captive dependency — IQualityGateValidator and IssueDescriptionParser are transient but captured by singleton PipelineOrchestrationService. Register as singleton or use factories.
builder.Services.AddTransient<IQualityGateValidator>(sp => new QualityGateValidator(Serilog.Log.Logger));
builder.Services.AddTransient<IssueDescriptionParser>();
builder.Services.AddSingleton(sp => new CiLogWriter(Serilog.Log.Logger));
builder.Services.AddTransient<GitHubValidationService>(sp =>
    new GitHubValidationService(sp.GetRequiredService<IProviderFactory>()));

// Multi-agent orchestrator services (singletons)
builder.Services.AddSingleton(sp => new AgentRegistryService(Serilog.Log.Logger));
builder.Services.AddSingleton(sp => new JobDispatcherService(
    sp.GetRequiredService<AgentRegistryService>(),
    Serilog.Log.Logger));
builder.Services.AddSingleton(sp => new TokenVendingService(Serilog.Log.Logger));
builder.Services.AddSingleton(sp => new OrchestratorRunService(
    Serilog.Log.Logger,
    pipelineConfig.OutputBufferCapacity));
builder.Services.AddSingleton<IOrchestratorRunService>(sp => sp.GetRequiredService<OrchestratorRunService>());
builder.Services.AddHostedService(sp => new HeartbeatMonitorService(
    sp.GetRequiredService<AgentRegistryService>(),
    sp.GetRequiredService<OrchestratorRunService>(),
    sp.GetRequiredService<IPipelineRunHistoryService>(),
    sp.GetRequiredService<JobDispatcherService>(),
    sp.GetRequiredService<IProviderFactory>(),
    sp.GetRequiredService<IConfigurationStore>(),
    Serilog.Log.Logger));

// Quality gate migration — ensures at least one QGC exists before first dispatch
builder.Services.AddHostedService(sp => new QualityGateMigrationService(
    sp.GetRequiredService<IConfigurationStore>(),
    Serilog.Log.Logger));

// Job queue drain service — periodically matches queued jobs to idle agents
builder.Services.AddSingleton(sp => new JobQueueDrainService(
    sp.GetRequiredService<JobDispatcherService>(),
    sp.GetRequiredService<AgentRegistryService>(),
    sp.GetRequiredService<IJobDispatcher>(),
    Serilog.Log.Logger));
builder.Services.AddHostedService(sp => sp.GetRequiredService<JobQueueDrainService>());

// Multi-agent job dispatcher (bridges loop service to agent dispatch)
builder.Services.AddSingleton<ProfileResolver>();
builder.Services.AddSingleton<QualityGateResolver>();
builder.Services.AddSingleton<IJobDispatcher>(sp => new AgentJobDispatcher(
    sp.GetRequiredService<JobDispatcherService>(),
    sp.GetRequiredService<AgentRegistryService>(),
    sp.GetRequiredService<OrchestratorRunService>(),
    sp.GetRequiredService<PipelineOrchestrationService>(),
    sp.GetRequiredService<TokenVendingService>(),
    sp.GetRequiredService<IConfigurationStore>(),
    sp.GetRequiredService<IProviderFactory>(),
    sp.GetRequiredService<ProfileResolver>(),
    sp.GetRequiredService<QualityGateResolver>(),
    sp.GetRequiredService<IHubContext<AgentHub, IAgentHubClient>>(),
    Serilog.Log.Logger));

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

var app = builder.Build();

// Graceful shutdown: swap to agent:cancelled label before exit
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    var pipeline = app.Services.GetRequiredService<PipelineOrchestrationService>();
    if (pipeline.IsRunning)
    {
        pipeline.CancelPipelineAsync().GetAwaiter().GetResult();
    }
});

// GET /health — anonymous, no auth required
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }))
    .AllowAnonymous();

app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

// SignalR hub endpoint for agent connections
app.MapHub<AgentHub>("/hubs/agent").RequireAuthorization("AgentApiKey");

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .DisableAntiforgery();

app.Run();

// Make Program class accessible for WebApplicationFactory in tests
public partial class Program { }
