using KiroCliLib.Configuration;
using KiroCliLib.Core;
using KiroCliLib.Models;
using KiroWebUI.Components;
using KiroWebUI.Models;
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
    sp.GetRequiredService<QualityGateValidator>(),
    Serilog.Log.Logger));
builder.Services.AddTransient(sp => new QualityGateValidator(Serilog.Log.Logger));
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

// Singleton execution lock for API endpoint (Req 12.2)
var apiExecutionLock = new SemaphoreSlim(1, 1);

// GET /health
app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));

// POST /api/prompt — creates orchestrator directly, no KiroExecutionService
app.MapPost("/api/prompt", async (PromptRequest request, IServiceProvider sp, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(request.Prompt))
        return Results.BadRequest(new { error = "Prompt is required and cannot be empty." });

    if (!await apiExecutionLock.WaitAsync(0, ct))
        return Results.Conflict(new { error = "An execution is already in progress." });

    try
    {
        var cfg = sp.GetRequiredService<Configuration>();
        var outputLines = new List<string>();
        IReadOnlyList<FileChange>? fileChanges = null;
        TestResult? testResults = null;

        var callbackHandler = new CallbackHandler(Log.Logger);
        callbackHandler.RegisterOnCompleted(ctx =>
        {
            fileChanges = ctx.FileChanges;
            testResults = ctx.TestResults;
        });

        var orchestrator = new KiroCliOrchestrator(cfg, callbackHandler, Log.Logger);
        var exitCode = await orchestrator.ExecutePromptAsync(
            request.Prompt,
            cfg.WorkspaceDirectory,
            request.UseResume,
            ct,
            onOutputLine: line => outputLines.Add(line));

        return Results.Ok(new PromptResponse
        {
            ExitCode = exitCode,
            OutputLines = outputLines.AsReadOnly(),
            FileChanges = fileChanges,
            TestResults = testResults
        });
    }
    finally
    {
        apiExecutionLock.Release();
    }
});

app.UseStaticFiles();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .DisableAntiforgery();

app.Run();

// Make Program class accessible for WebApplicationFactory in tests
public partial class Program { }
