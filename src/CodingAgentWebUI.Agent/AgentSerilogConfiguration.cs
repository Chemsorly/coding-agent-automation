using CodingAgentWebUI.Infrastructure.Telemetry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using Serilog.Enrichers.Span;
using Serilog.Formatting.Compact;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Provides Serilog configuration for the agent worker.
/// Extracted from Program.cs to reduce top-level statement complexity.
/// </summary>
/// <remarks>
/// <para><b>Log format:</b> Logs are emitted as CLEF (Compact Log Event Format) JSON
/// lines to stdout. This makes agent logs parseable by Loki's <c>| json</c> pipeline
/// stage, eliminating <c>JSONParserErr</c> when querying structured agent log fields
/// (e.g., <c>{service_name="agent"} | json | __error__ = ""</c>).</para>
///
/// <para><b>Field mapping:</b> CLEF uses <c>@l</c> for log level (not <c>level</c>),
/// <c>@t</c> for timestamp, and <c>@mt</c> for the message template. All enriched
/// properties (AgentId, TraceId, SpanId, etc.) appear as top-level JSON fields.
/// If the external Loki scrape config expects a <c>level</c> label, a label-rename
/// stage mapping <c>@l → level</c> must be added there.
/// Note: <c>@l</c> is omitted entirely for <c>Information</c>-level events per the
/// CLEF specification — only non-Information events carry the <c>@l</c> field.</para>
///
/// <para><b>OpenCode limitation:</b> OpenCode agent containers pipe log lines through
/// the entrypoint script, producing non-JSON prefixed output. Those lines will still
/// produce JSONParserErr in Loki. Only Kiro CLI agent pods are fully fixed by this change.</para>
///
/// <para><b>Known gap:</b> Pipeline progress lines emitted via <c>EmitOutputLine</c>
/// bypass Serilog entirely (streamed to the UI only) and do not appear in Loki.
/// Only structural lifecycle events (startup, SIGTERM, errors) are observable via Loki.
/// See issue #2178.</para>
/// </remarks>
internal static class AgentSerilogConfiguration
{
    internal static Serilog.ILogger CreateAgentLogger(AgentId agentId)
    {
        var logLevel = LogLevelParser.Parse(
            Environment.GetEnvironmentVariable(AgentDefaults.EnvLogLevel),
            Serilog.Events.LogEventLevel.Information);

        return new LoggerConfiguration()
            .MinimumLevel.Is(logLevel)
            // Suppress noisy ASP.NET Core request logging (health checks every 10s)
            .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
            // Suppress noisy HttpClient logging (OpenCode health monitor polls every 5s)
            .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
            // Suppress HttpClientFactory handler lifecycle logging (cleanup cycle every 10s)
            .MinimumLevel.Override("Microsoft.Extensions.Http", Serilog.Events.LogEventLevel.Warning)
            // Suppress Polly internal telemetry (StrategyExecuting/Executed fire at Debug on every call)
            .MinimumLevel.Override("Polly", Serilog.Events.LogEventLevel.Warning)
            // Suppress OpenTelemetry SDK internal logs (chatty at Debug — export errors still pass at Warning+)
            .MinimumLevel.Override("OpenTelemetry", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithSpan()
            // Use agentId.Value (the inner string) rather than the AgentId struct itself.
            // CompactJsonFormatter destructures structs, which would emit "AgentId":{"Value":"..."}
            // instead of a flat string — breaking Loki queries that filter on AgentId.
            .Enrich.WithProperty("AgentId", agentId.Value)
            // CLEF JSON format: emits single-line JSON per event, parseable by Loki's | json stage.
            // Each line includes @t (timestamp), @mt (message template), @l (level), and all
            // structured properties as top-level fields. Exceptions are serialized inline (no
            // multi-line output), which also fixes the multi-line log collection problem.
            .WriteTo.Console(new CompactJsonFormatter())
            .WriteToOtlpIfConfigured(Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? "coding-agent-worker")
            .CreateLogger();
    }
}
