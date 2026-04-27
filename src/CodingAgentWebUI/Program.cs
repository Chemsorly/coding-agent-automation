using KiroCliLib.Configuration;
using KiroCliLib.Core;
using CodingAgentWebUI.Components;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Infrastructure.Git;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Models;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Load KiroCliLib configuration
var config = await KiroCliLib.Configuration.ConfigurationManager.LoadAsync("config/appsettings.json");

// Register services
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(BuildInfo.Load());
builder.Services.AddScoped<KiroExecutionService>();

// Pipeline — Configuration Store
var configStore = new JsonConfigurationStore("config/pipeline");
builder.Services.AddSingleton<IConfigurationStore>(configStore);

// Load pipeline config eagerly (before DI container is built) to avoid sync-over-async deadlocks
var pipelineConfig = await configStore.LoadPipelineConfigAsync(CancellationToken.None);

// Pipeline — IKiroCliOrchestrator (needed by ProviderFactory for KiroCliAgentProvider)
builder.Services.AddSingleton<IKiroCliOrchestrator>(sp =>
{
    var cfg = sp.GetRequiredService<Configuration>();
    var callbackHandler = new CallbackHandler(Serilog.Log.Logger);
    return new KiroCliOrchestrator(cfg, callbackHandler, Serilog.Log.Logger);
});

// Pipeline — Provider Factory (creates provider instances from ProviderConfig at runtime)
builder.Services.AddSingleton<IProviderFactory>(sp =>
{
    var orchestrator = sp.GetRequiredService<IKiroCliOrchestrator>();
    return new ProviderFactory(orchestrator, pipelineConfig);
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
    sp.GetRequiredService<IPipelineRunHistoryService>()));

// Pipeline — Loop Service (background service, starts dormant)
builder.Services.AddSingleton<PipelineLoopService>(sp => new PipelineLoopService(
    sp.GetRequiredService<PipelineOrchestrationService>(),
    sp.GetRequiredService<IProviderFactory>(),
    sp.GetRequiredService<IConfigurationStore>(),
    Serilog.Log.Logger));
builder.Services.AddHostedService(sp => sp.GetRequiredService<PipelineLoopService>());

// TODO: [ARC-12] Captive dependency — IQualityGateValidator and IssueDescriptionParser are transient but captured by singleton PipelineOrchestrationService. Register as singleton or use factories.
builder.Services.AddTransient<IQualityGateValidator>(sp => new QualityGateValidator(Serilog.Log.Logger));
builder.Services.AddTransient<IssueDescriptionParser>();
builder.Services.AddSingleton(sp => new CiLogWriter(Serilog.Log.Logger));
builder.Services.AddTransient<GitHubValidationService>(sp =>
    new GitHubValidationService(sp.GetRequiredService<IProviderFactory>()));

// Configure Serilog
builder.Host.UseSerilog((ctx, lc) => lc
    .MinimumLevel.Is(config.LogLevel)
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

// Validate workspace directory
var workspace = config.WorkspaceDirectory;
if (!Directory.Exists(workspace))
    Log.Warning("Workspace directory not found: {Path}. Kiro CLI execution will fail.", workspace);

// GET /health
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

app.UseStaticFiles();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .DisableAntiforgery();

app.Run();

// Make Program class accessible for WebApplicationFactory in tests
public partial class Program { }
