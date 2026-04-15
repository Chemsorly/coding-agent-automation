using KiroCliLib.Configuration;
using KiroCliLib.Core;
using KiroWebUI.Components;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Providers;
using KiroWebUI.Pipeline.Services;
using KiroWebUI.Services;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Load KiroCliLib configuration
var config = await KiroCliLib.Configuration.ConfigurationManager.LoadAsync("config/appsettings.json");

// Register services
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton(config);
builder.Services.AddScoped<KiroExecutionService>();

// Pipeline — Configuration Store
builder.Services.AddSingleton<IConfigurationStore>(sp => new JsonConfigurationStore("config/pipeline"));

// Pipeline — IKiroCliOrchestrator (needed by ProviderFactory for KiroCliAgentProvider)
builder.Services.AddSingleton<IKiroCliOrchestrator>(sp =>
{
    var cfg = sp.GetRequiredService<Configuration>();
    var callbackHandler = new CallbackHandler(Serilog.Log.Logger);
    return new KiroCliOrchestrator(cfg, callbackHandler, Serilog.Log.Logger);
});

// Pipeline — Provider Factory (creates provider instances from ProviderConfig at runtime)
builder.Services.AddSingleton<IProviderFactory, ProviderFactory>();

// Pipeline — Services
builder.Services.AddSingleton(sp => new PipelineOrchestrationService(
    sp.GetRequiredService<IConfigurationStore>(),
    sp.GetRequiredService<IProviderFactory>(),
    sp.GetRequiredService<IssueDescriptionParser>(),
    sp.GetRequiredService<IQualityGateValidator>(),
    Serilog.Log.Logger));
builder.Services.AddTransient<IQualityGateValidator>(sp => new QualityGateValidator(Serilog.Log.Logger));
builder.Services.AddTransient<IssueDescriptionParser>();
builder.Services.AddTransient<GitHubValidationService>();

// Configure Serilog
builder.Host.UseSerilog((ctx, lc) => lc
    .MinimumLevel.Is(config.LogLevel)
    .WriteTo.Console());

var app = builder.Build();

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
