using CodingAgent.Api;
using CodingAgent.Api.Triage;
using CodingAgent.Api.Ui;
using CodingAgent.Infrastructure.Common.Auth;
using CodingAgent.AgentGateway;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.GitHub;
using CodingAgent.Infrastructure.Telemetry;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Serilog;

// Bootstrap logger: captures log output before UseSerilog takes over
Log.Logger = HostBootstrap.CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

// ── Startup identity log ─────────────────────────────────────────────────────
var version = Environment.GetEnvironmentVariable("SERVICE_VERSION") ?? "local";
var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "coding-agent-api";
HostBootstrap.LogStartupIdentity("Pipeline API", serviceName, version);

// ── Fast-fail: PostgreSQL required ──────────────────────────────────────────
var dbConnectionString = DatabaseConnectionResolver.Resolve(builder.Configuration);
if (string.IsNullOrEmpty(dbConnectionString))
{
    Log.Fatal("Database__Host is not configured. The Pipeline API requires PostgreSQL. Exiting.");
    return;
}

// ── Fast-fail: agent API key required ───────────────────────────────────────
var agentApiKey = builder.Configuration.GetValue<string>("AGENT_API_KEY");
if (string.IsNullOrEmpty(agentApiKey))
{
    Log.Fatal("AGENT_API_KEY is not configured. Exiting.");
    return;
}

// ── Fast-fail: user-token signing key required (spec 051, UI surface) ─────────
Microsoft.IdentityModel.Tokens.SecurityKey userTokenSigningKey;
try
{
    userTokenSigningKey = UserTokens.CreateSigningKey(builder.Configuration.GetValue<string>(UserTokens.SigningKeyConfigKey));
}
catch (InvalidOperationException ex)
{
    Log.Fatal(ex, "{Reason} Exiting.", ex.Message);
    return;
}

// ── Configure JSON serialization (enum-as-string to match agent DTOs) ───────
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// ── Host shutdown timeout ────────────────────────────────────────────────────
builder.Services.Configure<HostOptions>(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(40));

// ── Service registrations ────────────────────────────────────────────────────
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddApiInfrastructure(dbConnectionString);
builder.Services.AddApiOrchestration(builder.Configuration);
builder.Services.AddAgentHubServices();  // shared, from CodingAgent.AgentGateway

// ── SignalR (with optional Redis backplane) ──────────────────────────────────
builder.Services.AddApiSignalR(builder.Configuration, builder.Environment);

// ── Authentication + authorization: API keys (service surface), user token (UI surface) ──
builder.Services.AddApiAuthentication(agentApiKey, userTokenSigningKey, Log.Logger);

// ── Readiness drain on SIGTERM ────────────────────────────────────────────────
// Registered LAST so its StoppingAsync fires FIRST (IHostedLifecycleService fires in reverse order).
// Marks /readyz as 503 and waits READINESS_DRAIN_DELAY_SECONDS (default 15s) before allowing
// the host to proceed with shutdown. Configurable via READINESS_DRAIN_DELAY_SECONDS env var.
// TODO [WARNING]: The API host does not perform shutdown-budget validation (unlike the Web host, which
// calls ShutdownBudgetValidationExtensions.ValidateShutdownBudget). READINESS_DRAIN_DELAY_SECONDS is
// clamped to 0–120s by ReadinessDrainService.ResolveDrainDelay(), but HostOptions.ShutdownTimeout is
// fixed at 40s. If an operator sets readinessDrainDelaySeconds > 40 (e.g. 90), StoppingAsync starts a
// 90s Task.Delay but the host cancels it at 40s — the pod drains for only 40s with no startup warning.
// Consider calling ValidateShutdownBudget (or an equivalent API-specific version) after app.Build() to
// surface this misconfiguration at startup instead of silently at shutdown time.
builder.Services.AddSingleton<ReadinessState>();
builder.Services.AddHostedService(sp => new ReadinessDrainService(
    sp.GetRequiredService<ReadinessState>(),
    Log.Logger));

// ── Serilog ──────────────────────────────────────────────────────────────────
builder.Host.ConfigureApiSerilog();

// ── ASPNETCORE_URLS defaults to port 8080 ────────────────────────────────────
builder.WebHost.UseUrls("http://+:8080"); // NOSONAR S1075 — port is runtime infrastructure config, not a business URL

// ── OpenTelemetry ─────────────────────────────────────────────────────────────
builder.Services.AddHostOpenTelemetry(
    "coding-agent-api",
    configureTracing: t => t
        // AgentHub lives in the API process (moved from monolith in Spec 041).
        // Without this source, RegisterAgent / JobAccepted / JobCompleted hub invocations
        // produce no spans — agent lifecycle events are invisible in traces.
        .AddSource("Microsoft.AspNetCore.SignalR.Server")
        // Npgsql database query spans — the API is the only host with a database connection.
        // Requires Npgsql.OpenTelemetry package in CodingAgent.Api.csproj to activate
        // Npgsql's ActivitySource emission via assembly-load hooks.
        .AddSource("Npgsql"),
    configureMetrics: m => m
        // GitHub-facing metrics (github.api.requests counter, github.rate_limit.remaining gauge).
        // Not registered in the agent — agent pods must not emit these series.
        .AddMeter(GitHubTelemetry.MeterName)
        // Npgsql connection-pool metrics (pool_active_connections, pool_idle_connections, etc.)
        .AddMeter("Npgsql"));

var app = builder.Build();

// Migrations MUST be awaited before app.Run().
// Hosted services start concurrently with app.Run() — the hub must not accept
// RegisterAgent calls against an unmigrated schema.
await app.RunApiMigrationsAsync(builder.Configuration);

app.MapApiHealthEndpoints();
app.RegisterApiObservableGauges();

// Log every 4xx/5xx response as a structured Serilog event. This runs under the Serilog
// category (not Microsoft.AspNetCore), so it is NOT suppressed by the Warning override in
// ApiSerilogRegistration. Captures hub negotiate failures (e.g. 404 on /hubs/agent/negotiate)
// that would otherwise be invisible because Microsoft.AspNetCore is overridden to Warning.
app.UseSerilogRequestLogging(opts =>
{
    opts.GetLevel = (ctx, _, ex) =>
        ex is not null || ctx.Response.StatusCode >= 400
            ? Serilog.Events.LogEventLevel.Warning
            : Serilog.Events.LogEventLevel.Debug;
    opts.EnrichDiagnosticContext = (diag, ctx) =>
    {
        diag.Set("RequestHost", ctx.Request.Host.Value);
        diag.Set("RequestScheme", ctx.Request.Scheme);
        if (ctx.Response.StatusCode >= 400)
            diag.Set("ResponseStatusCode", ctx.Response.StatusCode);
    };
});

app.UseAuthentication();
app.UseAuthorization();

// SignalR hub — agents connect here.
app.MapHub<AgentHub>(HubRoutes.Agent).RequireAuthorization(ApiAuthPolicies.Agent);

app.MapWorkItemEndpoints();
app.MapPipelineRunEndpoints();
app.MapConfigEndpoints();
app.MapHarnessSuggestionEndpoints();
app.MapFeedbackCommentOutboxEndpoints();
app.MapTriageEndpoints();
app.MapAgentEndpoints();
app.MapChatEndpoints();
app.MapApiSchedulerEndpoints();
app.MapUiEndpoints();

// ── Startup DI validation ─────────────────────────────────────────────────────
// AssignmentEnricher is injected as optional [FromServices] in GetAssignment.
// If it is missing, new-schema work items silently receive a degraded identity-only 200
// response with no provider configs. Resolve eagerly to fail fast on misconfiguration.
_ = app.Services.GetRequiredService<AssignmentEnricher>();

await app.RunAsync();

// Make Program accessible for WebApplicationFactory in integration tests
public partial class Program // NOSONAR S1118 — required for WebApplicationFactory<Program> in integration tests
{
}

