using CodingAgentWebUI;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Infrastructure.Telemetry;
using CodingAgentWebUI.Models;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;

// ── CLI command: export-config ──────────────────────────────────────────────
if (args.Length >= 1 && args[0] == "export-config")
{
    var outputArg = args.FirstOrDefault(a => a.StartsWith("--output="))
        ?? args.FirstOrDefault(a => a == "--output");

    string? outputDir = null;
    if (outputArg is not null && outputArg.StartsWith("--output="))
    {
        outputDir = outputArg["--output=".Length..];
    }
    else if (outputArg == "--output")
    {
        var idx = Array.IndexOf(args, "--output");
        if (idx + 1 < args.Length)
            outputDir = args[idx + 1];
    }

    if (string.IsNullOrWhiteSpace(outputDir))
    {
        Console.Error.WriteLine("Usage: dotnet run -- export-config --output /path/to/dir");
        return;
    }

    // Initialize minimal Serilog for CLI mode
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Information()
        .WriteTo.Console(theme: Serilog.Sinks.SystemConsole.Themes.ConsoleTheme.None)
        .CreateLogger();

    var exportConfig = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Production"}.json", optional: true)
        .AddEnvironmentVariables()
        .Build();

    var connectionString = CodingAgentWebUI.Services.DatabaseConnectionResolver.Resolve(exportConfig);

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Log.Error("export-config requires Database:Host to be configured");
        return;
    }

    var services = new ServiceCollection();
    services.AddPooledDbContextFactory<PipelineDbContext>(o => o.UseNpgsql(connectionString));
    services.AddSingleton<ConfigExportService>();

#pragma warning disable ASP0000 // Intentional: CLI command uses isolated DI container, not the web host
    await using var sp = services.BuildServiceProvider();
#pragma warning restore ASP0000
    var exportService = sp.GetRequiredService<ConfigExportService>();

    Directory.CreateDirectory(outputDir);
    await exportService.ExportAsync(outputDir, CancellationToken.None);

    Log.Information("Export complete: {OutputDir}", outputDir);
    return;
}

// Bootstrap logger: captures log output during service registration (before UseSerilog takes over at Build())
// TODO: Add integration test verifying ResolveApiKey log messages appear in output (review-findings #953)
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(theme: Serilog.Sinks.SystemConsole.Themes.ConsoleTheme.None)
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

// Register services
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(BuildInfo.Load());

// Configure JSON serialization for minimal API endpoints (enum-as-string to match agent DTOs)
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// Host shutdown timeout: drain delay (15s) + ShutdownService timeout (15s) + buffer (10s) = 40s
builder.Services.Configure<HostOptions>(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(40));

// Pipeline — Configuration Store (created eagerly to load config before DI container is built)
var configStore = new JsonConfigurationStore(PipelineConstants.ConfigBaseDirectory);
var pipelineConfig = await configStore.LoadPipelineConfigAsync(CancellationToken.None);

// Domain service registrations (extracted into focused extension methods)
var dbConnectionString = CodingAgentWebUI.Services.DatabaseConnectionResolver.Resolve(builder.Configuration);
builder.Services.AddSingleton(new CodingAgentWebUI.Services.FeatureFlags
{
    IsDatabaseMode = !string.IsNullOrEmpty(dbConnectionString)
});
if (string.IsNullOrEmpty(dbConnectionString))
{
    // Legacy mode: JSON-based config store
    builder.Services.AddInfrastructureServices(configStore, pipelineConfig);
}
else
{
    // DB mode: infrastructure services without config store (handled by AddWorkDistribution)
    builder.Services.AddInfrastructureServicesWithoutConfigStore();
}
builder.Services.AddPipelineServices(Serilog.Log.Logger);
builder.Services.AddPipelineCoreServices(isDatabaseMode: !string.IsNullOrEmpty(dbConnectionString));
builder.Services.AddOrchestrationServices(pipelineConfig,
    string.IsNullOrEmpty(dbConnectionString) ? null : (builder.Configuration.GetValue<string>("WorkDistribution:Mode") ?? "SignalR"));
builder.Services.AddConsolidationServices(pipelineConfig);
builder.Services.AddWorkDistribution(builder.Configuration);
builder.Services.AddDatabaseHealthServices(builder.Configuration);

// Infrastructure health aggregation — reads from DatabaseHealthState + IConnectionMultiplexer (both optional)
builder.Services.AddSingleton<CodingAgentWebUI.Services.InfrastructureHealthService>();

// Page-level services (scoped — one instance per Blazor circuit)
builder.Services.AddScoped<CodingAgentWebUI.Services.AgentCodingPageService>();
builder.Services.AddScoped<CodingAgentWebUI.Services.AgentMonitoringPageService>();
builder.Services.AddScoped<CodingAgentWebUI.Services.NotificationService>();

// SignalR — hub services with MessagePack protocol
builder.Services.AddSignalR(options =>
    {
        // Agents may send output chunks or large payloads; default 32KB is too restrictive.
        options.MaximumReceiveMessageSize = 128 * 1024; // 128 KB
    })
    .AddMessagePackProtocol(options =>
    {
        options.SerializerOptions = MessagePack.MessagePackSerializerOptions.Standard
            .WithResolver(MessagePack.Resolvers.CompositeResolver.Create(
                new MessagePack.Formatters.IMessagePackFormatter[] { new CodingAgentWebUI.Pipeline.Models.JobIdFormatter() },
                new MessagePack.IFormatterResolver[] { MessagePack.Resolvers.ContractlessStandardResolverAllowPrivate.Instance }));
    });

// SignalR — hub filter for agent authorization
builder.Services.AddSingleton<IHubFilter>(sp => new AgentAuthorizationFilter(
    sp.GetRequiredService<IAgentRegistryService>(),
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
var orchestratorLogLevel = Environment.GetEnvironmentVariable("LOG_LEVEL")?.ToLowerInvariant() switch
{
    "debug" or "dbg" => Serilog.Events.LogEventLevel.Debug,
    "verbose" or "trace" => Serilog.Events.LogEventLevel.Verbose,
    "warning" or "warn" => Serilog.Events.LogEventLevel.Warning,
    "error" or "err" => Serilog.Events.LogEventLevel.Error,
    _ => Serilog.Events.LogEventLevel.Information
};
var dbLogLevel = Environment.GetEnvironmentVariable("DB_LOG_LEVEL")?.ToLowerInvariant() switch
{
    "debug" or "dbg" => Serilog.Events.LogEventLevel.Debug,
    "information" or "info" => Serilog.Events.LogEventLevel.Information,
    "verbose" or "trace" => Serilog.Events.LogEventLevel.Verbose,
    "error" or "err" => Serilog.Events.LogEventLevel.Error,
    _ => Serilog.Events.LogEventLevel.Warning
};
builder.Host.UseSerilog((ctx, lc) => lc
    .MinimumLevel.Is(orchestratorLogLevel)
    // Suppress noisy ASP.NET Core framework logging (health checks, static files, Blazor negotiation, auth)
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    // EF Core SQL command logging — controlled separately via DB_LOG_LEVEL env var
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", dbLogLevel)
    // Suppress noisy Npgsql connection open/close logging (Verbose/Trace only)
    .MinimumLevel.Override("Npgsql", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithSpan()
    .WriteTo.Console(theme: Serilog.Sinks.SystemConsole.Themes.ConsoleTheme.None)
    .WriteToOtlpIfConfigured("coding-agent-orchestrator", ctx.HostingEnvironment.EnvironmentName));

// Configure OpenTelemetry (tracing + metrics)
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: "coding-agent-orchestrator",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddSource(PipelineTelemetry.SourceName)
            .AddSource("Microsoft.AspNetCore.SignalR.Server")
            .AddOtlpExporter();

        // DB mode: Npgsql tracing for query spans
        if (!string.IsNullOrEmpty(dbConnectionString))
            t.AddSource("Npgsql");

        // Redis backplane: trace Redis commands
        var redisConn = builder.Configuration.GetValue<string>("SignalR:Redis:ConnectionString");
        if (!string.IsNullOrEmpty(redisConn))
            t.AddSource("StackExchange.Redis");
    })
    .WithMetrics(m =>
    {
        m.AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddMeter(PipelineTelemetry.SourceName)
            .AddOtlpExporter();

        // Work distribution metrics (035a)
        if (!string.IsNullOrEmpty(dbConnectionString))
            m.AddMeter(CodingAgentWebUI.Orchestration.Telemetry.WorkDistributionTelemetry.MeterName);
    });

var app = builder.Build();

// ── Post-Build startup sequence ─────────────────────────────────────────────
// Each concern is extracted into its own WebApplication extension method.
// Ordering matters: InitializeDatabaseAsync must precede RehydrateActiveRunsAsync (needs DB),
// and MapApplicationEndpoints must precede RunConsolidationStartupAsync (needs middleware).
// TODO: Add unit/integration tests for each extracted startup extension method
// (ValidateShutdownBudget, ValidateDiWiring, RegisterObservableGauges, RunConsolidationStartupAsync,
// AutoStartPipelineLoopAsync). Extraction was done to enable independent testability but no tests
// were added yet. (review-findings)

app.ValidateShutdownBudget();
await app.InitializeDatabaseAsync();
await app.RehydrateActiveRunsAsync();
app.ValidateDiWiring();
app.RegisterObservableGauges();
// Note: MapApplicationEndpoints takes dbConnectionString as a parameter (existing API);
// other methods resolve it internally via DatabaseConnectionResolver.Resolve().
app.MapApplicationEndpoints(dbConnectionString);
await app.RunConsolidationStartupAsync(pipelineConfig);
await app.AutoStartPipelineLoopAsync(pipelineConfig);

app.Run();

// Make Program class accessible for WebApplicationFactory in tests
public partial class Program { }
