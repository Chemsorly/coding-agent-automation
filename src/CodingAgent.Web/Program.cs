using CodingAgent.Web;
using CodingAgent.Api.Client;
using CodingAgent.AgentGateway;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.Common.Auth;
using CodingAgent.Infrastructure.Telemetry;
using CodingAgent.Pipeline;
using CodingAgent.Web.Auth;
using CodingAgent.Web.Models;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

// Bootstrap logger: captures log output during service registration (before UseSerilog takes over at Build())
// TODO: Add integration test verifying ResolveApiKey log messages appear in output (review-findings #953)
const string AgentApiKeyConfigKey = "AGENT_API_KEY"; // S1192: single source for repeated literal
Log.Logger = HostBootstrap.CreateBootstrapLogger();
// TODO: Web host has no startup-identity log; the other three hosts (Api, Scheduler, JobController)
// all call HostBootstrap.LogStartupIdentity(...) immediately after CreateBootstrapLogger(). Add a
// matching call here (read OTEL_SERVICE_NAME + SERVICE_VERSION and pass to LogStartupIdentity) so
// all four hosts emit a consistent startup line and failed pod starts are easier to diagnose.
// (review-findings #2865)

var builder = WebApplication.CreateBuilder(args);

// Spec 049: the chart renders the non-secret auth values to /app/config/auth.json. Environment
// variables are added again afterwards so they keep precedence (secrets, overrides).
builder.Configuration
    .AddJsonFile(builder.Configuration[AuthOptions.ConfigPathKey] ?? AuthOptions.DefaultConfigPath, optional: true, reloadOnChange: false)
    .AddEnvironmentVariables();

// Register services. The circuit hub keeps the 128 KB message limit it inherited from the
// agent-hub SignalR setup this host used to register (large pastes, e.g. config import).
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options =>
    {
        options.MaximumReceiveMessageSize = 128 * 1024;
        options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    });
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(BuildInfo.Load());

// Configure JSON serialization for minimal API endpoints (enum-as-string to match agent DTOs)
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// Host shutdown timeout: drain delay (15s) + ShutdownService timeout (15s) = 30s used, 10s headroom remaining.
// ShutdownBudgetValidation warns if headroom drops below 5s (i.e., drain + shutdown > 35s).
builder.Services.Configure<HostOptions>(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(40));

// ── Pipeline API client ───────────────────────────────────────────────────────────────────────
// Fast-fail: API URL required — monolith has no direct DB access.
// Helm injects this as PipelineApi__BaseUrl (double-underscore → colon convention).
var apiBaseUrl = builder.Configuration.GetValue<string>("PipelineApi:BaseUrl");
if (string.IsNullOrEmpty(apiBaseUrl))
{
    Log.Fatal("PipelineApi:BaseUrl is required. Ensure CodingAgent.Api is deployed first.");
    return;
}

// Spec 051: the web host signs a user token for each signed-in user; the API checks it on
// /api/ui. Fail fast on a missing or short key so the host never starts unable to call the API
// as its users.
SecurityKey userTokenSigningKey;
try
{
    userTokenSigningKey = UserTokens.CreateSigningKey(builder.Configuration.GetValue<string>(UserTokens.SigningKeyConfigKey));
}
catch (InvalidOperationException ex)
{
    Log.Fatal(ex, "{Reason} Exiting.", ex.Message);
    return;
}

builder.Services.AddUiClients(apiBaseUrl, userTokenSigningKey);

// Spec 051 D10: the login cookie and antiforgery tokens are protected with one fixed key from the
// same Secret, so every replica and every restart accepts them. No Redis, nothing written.
try
{
    builder.Services.AddDataProtectionServices(builder.Configuration.GetValue<string>(DataProtectionRegistration.KeyConfigKey));
}
catch (InvalidOperationException ex)
{
    Log.Fatal(ex, "{Reason} Exiting.", ex.Message);
    return;
}

var agentApiKey = builder.Configuration.GetValue<string>(AgentApiKeyConfigKey)
    ?? Environment.GetEnvironmentVariable(AgentApiKeyConfigKey)
    ?? "";

builder.Services.AddPipelineApiClient(new PipelineApiClientOptions
{
    BaseUrl = apiBaseUrl,
    AgentApiKey = agentApiKey
});

// Scoped hub connection — one per Blazor circuit (overrides the Transient registered
// by AddPipelineApiClient above). Connects to the API hub using AGENT_API_KEY as Bearer.
// Helm injects PipelineApi__HubUrl (double-underscore → PipelineApi:HubUrl IConfiguration key).
// Falls back to baseUrl + /hubs/agent if HubUrl is not set.
// See Req 3.6 L1 for the explicit wiring requirement.
var apiHubUrl = builder.Configuration.GetValue<string>("PipelineApi:HubUrl")
    ?? $"{apiBaseUrl.TrimEnd('/')}/hubs/agent";
builder.Services.AddScoped<IAgentHubConnection>(_ => new AgentHubConnection(apiHubUrl, agentApiKey));

builder.Services.AddInfrastructureServices();
builder.Services.AddPipelineServices(Serilog.Log.Logger);
builder.Services.AddPipelineCoreServices();
builder.Services.AddOrchestrationServices();
// The Consolidation page triggers through the API since spec 051 2.4b, so this host registers no
// consolidation service.
builder.Services.AddWorkDistribution(builder.Configuration);

// Frontend observability (Faro) — scoped so each Blazor circuit gets its own instance.
// FaroService wraps IJSRuntime; NotificationFaroBridge forwards notifications to Faro.
// Both silently no-op if Faro is not configured (Faro:CollectorUrl env var absent).
builder.Services.AddScoped<CodingAgent.Web.Services.IFaroService, CodingAgent.Web.Services.FaroService>();
builder.Services.AddScoped<CodingAgent.Web.Services.NotificationFaroBridge>();

// Page-level services (scoped — one instance per Blazor circuit)
builder.Services.AddScoped<CodingAgent.Web.Services.IIssueDrawerService, CodingAgent.Web.Services.IssueDrawerService>();
builder.Services.AddScoped<CodingAgent.Web.Services.IPrReviewDrawerService, CodingAgent.Web.Services.PrReviewDrawerService>();
builder.Services.AddScoped<CodingAgent.Web.Services.IEpicDrawerService, CodingAgent.Web.Services.EpicDrawerService>();
builder.Services.AddScoped<CodingAgent.Web.Services.AgentCodingPageService>();
builder.Services.AddScoped<CodingAgent.Web.Services.NotificationService>();
builder.Services.AddScoped<CodingAgent.Web.Services.CockpitState>();
builder.Services.AddScoped<CodingAgent.Web.Services.BlockedIssuesService>();

// User authentication (cookie, local admin, OIDC, role bindings)
builder.Services.AddUserAuthentication(builder.Configuration);

// Configure Serilog
builder.Host.ConfigureSerilog();

// Configure OpenTelemetry (tracing + metrics)
builder.Services.AddApplicationTelemetry();

var app = builder.Build();

// ── Post-Build startup sequence ─────────────────────────────────────────────
// Each concern is extracted into its own WebApplication extension method.
// Ordering: ValidateShutdownBudget, ValidateDiWiring, then MapApplicationEndpoints.

app.ValidateShutdownBudget();
app.ValidateDiWiring();
app.MapApplicationEndpoints();

await app.RunAsync();

// Make Program class accessible for WebApplicationFactory in tests
public partial class Program { } // NOSONAR S1118 — required for WebApplicationFactory<Program> in integration tests
