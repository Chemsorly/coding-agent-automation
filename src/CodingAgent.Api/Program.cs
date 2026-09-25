using CodingAgent.Api;
using CodingAgent.AgentGateway;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.GitHub;
using CodingAgent.Infrastructure.Telemetry;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Services;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

// Bootstrap logger: captures log output before UseSerilog takes over
Log.Logger = HostBootstrap.CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);

// ── Startup identity log ─────────────────────────────────────────────────────
var version = Environment.GetEnvironmentVariable("SERVICE_VERSION") ?? "local";
var serviceName = builder.Configuration.GetValue<string>("OTEL_SERVICE_NAME") ?? "coding-agent-api";
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

// ── Agent API key authentication + authorization ─────────────────────────────
builder.Services.AddApiAuthentication(agentApiKey, Log.Logger);

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
var otelServiceName = builder.Configuration.GetValue<string>("OTEL_SERVICE_NAME") ?? "coding-agent-api";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: otelServiceName,
        serviceVersion: version))
    .WithTracing(t =>
    {
        t.AddAspNetCoreInstrumentation()
         .AddHttpClientInstrumentation()
         // AgentHub lives in the API process (moved from monolith in Spec 041).
         // Without this source, RegisterAgent / JobAccepted / JobCompleted hub invocations
         // produce no spans — agent lifecycle events are invisible in traces.
         .AddSource("Microsoft.AspNetCore.SignalR.Server")
         // Subscribe to the pipeline activity source so orchestrator-side ExecutePipeline spans
         // are exported to Tempo. These spans are started by PipelineRunFactory.CreateFromWorkItem
         // and stopped by RunLifecycleManager when the run reaches a terminal state (issue #2255).
         .AddSource(PipelineTelemetry.SourceName)
         .AddOtlpExporter();
    })
    .WithMetrics(m =>
    {
        m.AddAspNetCoreInstrumentation()
         .AddHttpClientInstrumentation()
         // WorkDistributionTelemetry.MeterName is exported here because the API owns some
         // work-distribution instruments: WorkItemEndpoints records terminal statuses and
         // dispatch latency via LogTerminalStatus/RecordDispatchLatency. (The workitems-by-status
         // gauges are fed by WorkItemCountsService in the Scheduler — a separate process — not here.)
         // NOTE: The epoch/credential-pool gauges (DispatcherLastPollEpoch, CredentialPoolAvailable,
         // CredentialPoolClaimed) are written ONLY by the Job Controller's DispatchService — the
         // API's DispatchStateBuilder does NOT call RecordLastPollEpoch or UpdateCredentialPoolMetrics.
         // This ensures the DispatcherStalled / CredentialPoolExhausted Helm alert rules evaluate
         // a single authoritative series from the Job Controller, not a conflicting API series.
         .AddMeter(WorkDistributionTelemetry.MeterName)
         // The API hosts AgentRegistryService and registers agent.jobs.active /
         // agent.connections.total ObservableGauges on PipelineTelemetry.Meter via
         // RegisterApiObservableGauges(). Without this AddMeter those gauges are created
         // on the meter but the meter is not subscribed — measurements are silently dropped.
         .AddMeter(PipelineTelemetry.SourceName)
         // GitHub-facing metrics (github.api.requests counter, github.rate_limit.remaining gauge).
         // Not registered in the agent — agent pods must not emit these series.
         .AddMeter(GitHubTelemetry.MeterName)
         // Prometheus requires Cumulative temporality; the OTLP exporter defaults to Delta for
         // histograms and counters, which Grafana Cloud silently drops. Matches the monolith.
         .AddOtlpExporter((_, readerOptions) =>
             readerOptions.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative);
    });

var app = builder.Build();

// Migrations MUST be awaited before app.Run().
// Hosted services start concurrently with app.Run() — the hub must not accept
// RegisterAgent calls against an unmigrated schema.
await app.RunApiMigrationsAsync(builder.Configuration);

app.MapApiHealthEndpoints();
app.RegisterApiObservableGauges();

// Pre-initialize github.api.requests counter tag combinations so Prometheus increase() works
// on first increment. Must run after builder.Build() so the MeterProvider is active.
GitHubTelemetry.PreInitialize();

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
app.MapConsolidationRunEndpoints();
app.MapHarnessSuggestionEndpoints();
app.MapFeedbackCommentOutboxEndpoints();
app.MapAgentEndpoints();
app.MapChatEndpoints();
app.MapApiSchedulerEndpoints();

// ── Startup DI validation ─────────────────────────────────────────────────────
// AssignmentEnricher is injected as optional [FromServices] in GetAssignment.
// If it is missing, new-schema work items silently receive a degraded identity-only 200
// response with no provider configs. Resolve eagerly to fail fast on misconfiguration.
_ = app.Services.GetRequiredService<AssignmentEnricher>();

// ── Counter pre-initialization ────────────────────────────────────────────────
// Pre-initializing all closed-tag series to 0 before the first real event means that
// Prometheus increase() is visible from the very first increment after a deploy.
// Each Add(0) creates the series; a single ForceFlush exports them to the OTLP endpoint.
// Histograms are intentionally excluded — see issue #2967.
PreInitializeMetrics(app.Services);

await app.RunAsync();

// ── Pre-initialization helper ─────────────────────────────────────────────────

/// <summary>
/// Pre-initializes all closed-tag combinations for counters that would otherwise lose their
/// first increment to the Prometheus <c>increase()</c> gap on new series.
/// </summary>
/// <remarks>
/// TODO: [WARNING] This function writes directly to shared static instrument instances
/// (<see cref="PipelineTelemetry.RunOutcomes"/>, <see cref="WorkDistributionTelemetry.WorkItemsTerminated"/>).
/// If the application startup path is exercised more than once in the same process (e.g. in an integration
/// test suite using <c>WebApplicationFactory&lt;Program&gt;</c> with multiple test server instances), the
/// <c>Add(0)</c> calls execute multiple times — which is safe for counters (adding 0 is idempotent) — but
/// <c>ForceFlush()</c> is also invoked once per test host start, potentially causing unexpected OTLP export
/// side effects if a real OTLP endpoint is configured in CI. The <c>meterProvider?.ForceFlush()</c> null-check
/// silently skips the flush when OTLP is not configured, limiting the blast radius in practice.
/// </remarks>
static void PreInitializeMetrics(IServiceProvider services)
{
    // run_type values
    string[] runTypes = ["implementation", "review", "decomposition", "decompositionanalysis", "consolidation"];

    // Non-failure outcomes (failure_reason=none)
    string[] nonFailureOutcomes = ["cancelled", "conflict_restart", "needs_refinement", "wont_do", "pr_created", "draft_pr", "succeeded"];

    // failure_reason snake_case values for the "failed" outcome
    string[] failureReasons = ["timeout", "infrastructure_failure", "agent_error", "token_refresh_failure", "exit_code_failure", "quality_gate_exhausted", "gate_rejected"];

    // WorkItem terminal statuses
    string[] terminalStatuses = ["Succeeded", "Failed", "Cancelled"];

    // pipeline.run.outcomes: (5 run_types × 7 non-failure outcomes × none) +
    //                        (5 run_types × 1 timeout outcome × timeout) +
    //                        (5 run_types × 1 failed outcome × 7 failure_reasons)
    //                      = 35 + 5 + 35 = 75 series
    // pipeline.project_name is excluded from pre-initialization per Requirement 7:
    // it has unbounded cardinality so it cannot appear in the pre-init set.
    // The live recording in RecordRunOutcomeMetrics does include pipeline.project_name (4-tag series),
    // so pre-initialized series (3 tags) and live series (4 tags) have different label fingerprints —
    // which is acceptable: the closed dimensions are still pre-initialized correctly.
    foreach (var runType in runTypes)
    {
        foreach (var outcome in nonFailureOutcomes)
        {
            PipelineTelemetry.RunOutcomes.Add(0,
                new KeyValuePair<string, object?>("run_type", runType),
                new KeyValuePair<string, object?>("outcome", outcome),
                new KeyValuePair<string, object?>("failure_reason", "none"));
        }

        // timeout outcome
        PipelineTelemetry.RunOutcomes.Add(0,
            new KeyValuePair<string, object?>("run_type", runType),
            new KeyValuePair<string, object?>("outcome", "timeout"),
            new KeyValuePair<string, object?>("failure_reason", "timeout"));

        // failed outcome — one series per failure_reason
        foreach (var failureReason in failureReasons)
        {
            PipelineTelemetry.RunOutcomes.Add(0,
                new KeyValuePair<string, object?>("run_type", runType),
                new KeyValuePair<string, object?>("outcome", "failed"),
                new KeyValuePair<string, object?>("failure_reason", failureReason));
        }
    }

    // workdistribution.workitems_terminated: 3 statuses × (none + 7 failure_reasons) = 24 series
    // failure_reason values match the snake_case normalization in LogTerminalStatus (issue #2967).
    foreach (var status in terminalStatuses)
    {
        WorkDistributionTelemetry.WorkItemsTerminated.Add(0,
            new KeyValuePair<string, object?>("status", status),
            new KeyValuePair<string, object?>("failure_reason", "none"));

        foreach (var failureReason in failureReasons)
        {
            WorkDistributionTelemetry.WorkItemsTerminated.Add(0,
                new KeyValuePair<string, object?>("status", status),
                new KeyValuePair<string, object?>("failure_reason", failureReason));
        }
    }

    // Flush all pre-initialized series to the OTLP endpoint immediately.
    // TODO: [WARNING] meterProvider?.ForceFlush() silently skips the flush when GetService returns null.
    // This happens when metrics are wired without registering MeterProvider in DI (e.g. OTLP export is
    // not configured, or a future refactor removes the explicit AddOpenTelemetry().WithMetrics() call).
    // If the flush is skipped, the pre-initialized Add(0) series are never exported to the OTLP endpoint
    // before the first real event, defeating the pre-init goal. Add a log warning when meterProvider is
    // null so a misconfigured API startup is observable rather than silent.
    var meterProvider = services.GetService<OpenTelemetry.Metrics.MeterProvider>();
    meterProvider?.ForceFlush();
}

// Make Program accessible for WebApplicationFactory in integration tests
public partial class Program { } // NOSONAR S1118 — required for WebApplicationFactory<Program> in integration tests

