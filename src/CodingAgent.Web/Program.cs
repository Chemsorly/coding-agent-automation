using CodingAgent.Web.Bff;
using CodingAgent.Web;
using CodingAgent.Infrastructure.Common.Auth;
using CodingAgent.Infrastructure.Telemetry;
using CodingAgent.Pipeline;
using CodingAgent.Web.Auth;
using CodingAgent.Web.Models;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

// Bootstrap logger: captures log output during service registration (before UseSerilog takes over at Build())
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

// Spec 052: the pages run in the browser as WebAssembly; this host renders only the static login
// page and the host page that loads the client.
builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(BuildInfoLoader.Load());

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

// Spec 052: the BFF forwards /api/ui/** and /hubs/ui to the API with a user token it mints per request.
builder.Services.AddSingleton(new UserTokenSigningKey(userTokenSigningKey));
builder.Services.AddSingleton<UserTokenMinter>();
builder.Services.AddBffForwarding();

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

// Spec 051 4.1: the web host holds no agent API key. Everything it shows comes from /api/ui and the UI
// hub, called as the signed-in user; the drain service keeps /readyz honest during shutdown.
builder.Services.AddReadinessDrain();

// User authentication (cookie, local admin, OIDC, role bindings)
builder.Services.AddUserAuthentication(builder.Configuration);

// Configure Serilog
builder.Host.ConfigureSerilog();

// Configure OpenTelemetry (tracing + metrics)
builder.Services.AddApplicationTelemetry();

var app = builder.Build();

// ── Post-Build startup sequence ─────────────────────────────────────────────
// Each concern is extracted into its own WebApplication extension method.
// Ordering: ValidateShutdownBudget, then MapApplicationEndpoints.

app.ValidateShutdownBudget();
app.MapApplicationEndpoints();

await app.RunAsync();

// Make Program class accessible for WebApplicationFactory in tests
public partial class Program { } // NOSONAR S1118 — required for WebApplicationFactory<Program> in integration tests
