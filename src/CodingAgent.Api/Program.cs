using CodingAgent.Api;
using CodingAgent.AgentGateway;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.GitHub;
using CodingAgent.Infrastructure.Telemetry;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
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
        t.AddAspNetCoreInstrumentation(opts =>
            opts.Filter = OtelNoiseFilter.FilterAspNetCoreRequest)
         .AddHttpClientInstrumentation(opts =>
             opts.FilterHttpRequestMessage = OtelNoiseFilter.FilterHttpClientRequest)
         // AgentHub lives in the API process (moved from monolith in Spec 041).
         // Without this source, RegisterAgent / JobAccepted / JobCompleted hub invocations
         // produce no spans — agent lifecycle events are invisible in traces.
         .AddSource("Microsoft.AspNetCore.SignalR.Server")
         // The pipeline activity source carries the API's own spans (Hub.ReportJobCompleted,
         // TokenVending.GenerateToken).
         .AddSource(PipelineTelemetry.SourceName)
         // Npgsql database query spans — the API is the only host with a database connection.
         // Requires Npgsql.OpenTelemetry package in CodingAgent.Api.csproj to activate
         // Npgsql's ActivitySource emission via assembly-load hooks.
         .AddSource("Npgsql")
         .AddProcessor(new OtelNoiseSpanProcessor())
         .AddOtlpExporter();
    })
    .WithMetrics(m =>
    {
        m.AddAspNetCoreInstrumentation()
         .AddHttpClientInstrumentation()
         // WorkDistributionTelemetry.MeterName is exported here because the API owns the dispatch
         // instruments: dispatch latency, dispatch attempts, pod start time, PVC pool exhaustions and
         // the credential-pool gauges (DispatchWorkItemService). The dispatcher poll epoch and the
         // workitems-by-status gauges belong to the Scheduler; DispatchStateBuilder does NOT call
         // RecordLastPollEpoch, so the DispatcherStalled / CredentialPoolExhausted alert rules each
         // evaluate a single authoritative series.
         .AddMeter(WorkDistributionTelemetry.MeterName)
         // The API hosts AgentRegistryService and registers agent.jobs.active /
         // agent.connections.total ObservableGauges on PipelineTelemetry.Meter via
         // RegisterApiObservableGauges(). Without this AddMeter those gauges are created
         // on the meter but the meter is not subscribed — measurements are silently dropped.
         .AddMeter(PipelineTelemetry.SourceName)
         // GitHub-facing metrics (github.api.requests counter, github.rate_limit.remaining gauge).
         // Not registered in the agent — agent pods must not emit these series.
         .AddMeter(GitHubTelemetry.MeterName)
         // Npgsql connection-pool metrics (pool_active_connections, pool_idle_connections, etc.)
         .AddMeter("Npgsql")
         // .NET runtime metrics (GC, thread pool, CPU, memory) — built-in since .NET 8.
         .AddMeter("System.Runtime")
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
app.MapAgentEndpoints();
app.MapChatEndpoints();
app.MapApiSchedulerEndpoints();

// ── Startup DI validation ─────────────────────────────────────────────────────
// AssignmentEnricher is injected as optional [FromServices] in GetAssignment.
// If it is missing, new-schema work items silently receive a degraded identity-only 200
// response with no provider configs. Resolve eagerly to fail fast on misconfiguration.
_ = app.Services.GetRequiredService<AssignmentEnricher>();

// ── Counter pre-initialization ────────────────────────────────────────────────
// Seeds every closed-tag counter series with 0 so Prometheus increase() sees the first real
// increment after a deploy. Histograms are intentionally excluded — see issue #2967.
MetricPreInitialization.Run(app.Services, () =>
{
    Program.EmitPreInitCounters();
    GitHubTelemetry.PreInitialize();
});

await app.RunAsync();

// Make Program accessible for WebApplicationFactory in integration tests
public partial class Program // NOSONAR S1118 — required for WebApplicationFactory<Program> in integration tests
{
    private const string RunTypeKey = "run_type";

    /// <summary>
    /// Emits <c>Add(0)</c> for all closed-tag combinations of the counters that must be pre-initialized.
    /// Callable from both the API startup path (via <c>MetricPreInitialization.Run</c>) and integration tests
    /// that need to verify pre-initialization coverage without re-implementing the logic.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>pipeline.run.outcomes:</strong> 75 series —
    /// 5 run_types × (7 non-failure outcomes + 1 timeout + 7 failed × 7 failure_reasons).
    /// <c>pipeline.project_name</c> is intentionally excluded (unbounded cardinality, Requirement 7).
    /// </para>
    /// <para>
    /// TODO: [WARNING] (run_type, "failed", "none") is NOT included. DeriveOutcome priority 9 returns
    /// failure_reason="none" when failureReason is null — which happens when a request carries
    /// Status=Failed with no parseable request.FailureReason and no payload FailureCategory. The first
    /// such event after a deploy is invisible to increase() until a second identical series event arrives.
    /// Fix: add (run_type, "failed", "none") per runType (5 additional series, total 80, under the ~100 limit).
    /// </para>
    /// </remarks>
    internal static void EmitPreInitCounters()
    {
        string[] runTypes = ["implementation", "review", "decomposition", "decompositionanalysis", "consolidation"];

        EmitRunOutcomePreInitCounters(runTypes);

        // workdistribution.dispatch.attempts: 7 result × reason series
        WorkDistributionTelemetry.PreInitializeDispatchAttempts();

        // pipeline.run.sub_issues: 2 series (result=created / result=failed)
        foreach (var result in new[] { "created", "failed" })
            PipelineTelemetry.RunSubIssues.Add(0, new KeyValuePair<string, object?>("result", result));

        // pipeline.run.brain_updates: 2 series (result=pushed / result=none)
        foreach (var result in new[] { "pushed", "none" })
            PipelineTelemetry.RunBrainUpdates.Add(0, new KeyValuePair<string, object?>("result", result));

        EmitQualityGateResultPreInitCounters(runTypes);

        // pipeline.run.ci.not_started_retriggers: 5 run_types (issue #2979)
        foreach (var runType in runTypes)
            PipelineTelemetry.RunCiNotStartedRetriggers.Add(0,
                new KeyValuePair<string, object?>(RunTypeKey, runType));

        EmitAgentStallPreInitCounters(runTypes);
        EmitRunPhasePreInitCounters(runTypes);
        EmitUsageDetailPreInitCounters(runTypes);
    }

    /// <summary>pipeline.run.outcomes: 75 series (3-tag; pipeline.project_name excluded per Req 7).</summary>
    private static void EmitRunOutcomePreInitCounters(string[] runTypes)
    {
        const string FailureReasonKey = "failure_reason";

        string[] nonFailureOutcomes = ["cancelled", "conflict_restart", "needs_refinement", "wont_do", "pr_created", "draft_pr", "succeeded"];
        string[] failureReasons = ["timeout", "infrastructure_failure", "agent_error", "token_refresh_failure", "exit_code_failure", "quality_gate_exhausted", "gate_rejected"];

        foreach (var runType in runTypes)
        {
            foreach (var outcome in nonFailureOutcomes)
            {
                PipelineTelemetry.RunOutcomes.Add(0,
                    new KeyValuePair<string, object?>(RunTypeKey, runType),
                    new KeyValuePair<string, object?>("outcome", outcome),
                    new KeyValuePair<string, object?>(FailureReasonKey, "none"));
            }

            // timeout outcome
            PipelineTelemetry.RunOutcomes.Add(0,
                new KeyValuePair<string, object?>(RunTypeKey, runType),
                new KeyValuePair<string, object?>("outcome", "timeout"),
                new KeyValuePair<string, object?>(FailureReasonKey, "timeout"));

            // failed outcome — one series per named failure_reason
            foreach (var failureReason in failureReasons)
            {
                PipelineTelemetry.RunOutcomes.Add(0,
                    new KeyValuePair<string, object?>(RunTypeKey, runType),
                    new KeyValuePair<string, object?>("outcome", "failed"),
                    new KeyValuePair<string, object?>(FailureReasonKey, failureReason));
            }
        }
    }

    /// <summary>
    /// pipeline.run.quality_gate.results: run_type × gate × result × infrastructure_failure (issue #2979).
    /// </summary>
    private static void EmitQualityGateResultPreInitCounters(string[] runTypes)
    {
        // Skip infrastructure_failure=true for compilation (never fires there).
        // TODO [WARNING]: The condition below (`gate != Compilation`) also pre-initializes external_ci with
        // infrastructure_failure=true, but RecordQualityGateResultMetrics hard-codes infraFailure:false for the
        // ExternalCi gate (GateResult.IsInfrastructureFailure is documented as only applicable to the Tests gate).
        // This creates a permanent external_ci/infrastructure_failure=true series that can never increment.
        // Fix: change the condition to `gate == PipelineTelemetry.QualityGateResultGates.Tests` so only the
        // tests gate gets infrastructure_failure=true pre-initialization. (Correctness #2979)
        foreach (var runType in runTypes)
        {
            foreach (var gate in PipelineTelemetry.QualityGateResultGates.All)
            {
                foreach (var result in new[] { "pass", "fail" })
                {
                    PipelineTelemetry.RunQualityGateResults.Add(0,
                        new KeyValuePair<string, object?>(RunTypeKey, runType),
                        new KeyValuePair<string, object?>("gate", gate),
                        new KeyValuePair<string, object?>("result", result),
                        new KeyValuePair<string, object?>("infrastructure_failure", "false"));

                    if (gate != PipelineTelemetry.QualityGateResultGates.Compilation)
                    {
                        PipelineTelemetry.RunQualityGateResults.Add(0,
                            new KeyValuePair<string, object?>(RunTypeKey, runType),
                            new KeyValuePair<string, object?>("gate", gate),
                            new KeyValuePair<string, object?>("result", result),
                            new KeyValuePair<string, object?>("infrastructure_failure", "true"));
                    }
                }
            }
        }
    }

    /// <summary>pipeline.run.agent_stalls: run_type × phase × kind (issue #2979).</summary>
    private static void EmitAgentStallPreInitCounters(string[] runTypes)
    {
        foreach (var runType in runTypes)
        {
            foreach (var phase in PipelineTelemetry.RunPhases.All)
            {
                foreach (var kind in PipelineTelemetry.AgentStallKinds.All)
                {
                    PipelineTelemetry.RunAgentStalls.Add(0,
                        new KeyValuePair<string, object?>(RunTypeKey, runType),
                        new KeyValuePair<string, object?>("phase", phase),
                        new KeyValuePair<string, object?>("kind", kind));
                }
            }
        }
    }

    /// <summary>
    /// Pre-initializes the per-phase usage counters: pipeline.run.tokens, pipeline.run.cost_usd,
    /// pipeline.run.agent_sessions and pipeline.run.agent_time.
    /// </summary>
    private static void EmitRunPhasePreInitCounters(string[] runTypes)
    {
        // 5 run_types × 10 phases × 4 providers = 200 series per metric, 4 metrics.
        // model is excluded from pre-initialization (unbounded cardinality per Req 7 additional comment).
        // TODO: run_type="unknown" can be emitted at runtime when ResolveRunContextAsync cannot resolve
        // the WorkItem (e.g. missing DB row, null dbFactory). That series is not pre-initialized here,
        // so Prometheus increase()/rate() will miss the first increment after a deploy. Consider adding
        // "unknown" to the runTypes array (adds 27 series/metric, keeping total well under 200/metric).
        foreach (var runType in runTypes)
        {
            foreach (var phase in PipelineTelemetry.RunPhases.All)
            {
                foreach (var provider in PipelineTelemetry.RunProviders.All)
                {
                    PipelineTelemetry.RunTokens.Add(0,
                        new KeyValuePair<string, object?>(RunTypeKey, runType),
                        new KeyValuePair<string, object?>("phase", phase),
                        new KeyValuePair<string, object?>("provider", provider));

                    PipelineTelemetry.RunCostUsd.Add(0,
                        new KeyValuePair<string, object?>(RunTypeKey, runType),
                        new KeyValuePair<string, object?>("phase", phase),
                        new KeyValuePair<string, object?>("provider", provider));

                    PipelineTelemetry.RunAgentSessions.Add(0,
                        new KeyValuePair<string, object?>(RunTypeKey, runType),
                        new KeyValuePair<string, object?>("phase", phase),
                        new KeyValuePair<string, object?>("provider", provider),
                        new KeyValuePair<string, object?>("model", "unknown"));

                    PipelineTelemetry.RunAgentTime.Add(0,
                        new KeyValuePair<string, object?>(RunTypeKey, runType),
                        new KeyValuePair<string, object?>("phase", phase),
                        new KeyValuePair<string, object?>("provider", provider));
                }
            }
        }
    }

    /// <summary>
    /// Pre-initializes the phase-less usage detail counters: 5 run_types × 4 providers × 5 token types
    /// (token_usage), × 3 billing modes (billing_cost_usd), turns and web searches per run_type × provider,
    /// and rate-limit readings per window × status for the claude provider (the only one that reports them).
    /// </summary>
    private static void EmitUsageDetailPreInitCounters(string[] runTypes)
    {
        foreach (var runType in runTypes)
        {
            foreach (var provider in PipelineTelemetry.RunProviders.All)
            {
                var runTypeTag = new KeyValuePair<string, object?>(RunTypeKey, runType);
                var providerTag = new KeyValuePair<string, object?>("provider", provider);

                foreach (var tokenType in PipelineTelemetry.TokenTypes.All)
                    PipelineTelemetry.RunTokenUsage.Add(0, runTypeTag, providerTag,
                        new KeyValuePair<string, object?>("token_type", tokenType));

                foreach (var billing in AgentBillingModes.All)
                    PipelineTelemetry.RunBillingCostUsd.Add(0, runTypeTag, providerTag,
                        new KeyValuePair<string, object?>("billing", billing));

                PipelineTelemetry.RunAgentTurns.Add(0, runTypeTag, providerTag);
                PipelineTelemetry.RunWebSearchRequests.Add(0, runTypeTag, providerTag);
            }
        }

        foreach (var window in PipelineTelemetry.RateLimitTags.Windows)
        {
            foreach (var status in PipelineTelemetry.RateLimitTags.Statuses)
            {
                PipelineTelemetry.RunRateLimitEvents.Add(0,
                    new KeyValuePair<string, object?>("provider", PipelineTelemetry.RunProviders.Claude),
                    new KeyValuePair<string, object?>("window", window),
                    new KeyValuePair<string, object?>("status", status));
            }
        }
    }
}

