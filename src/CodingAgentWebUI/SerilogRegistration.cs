using CodingAgentWebUI.Infrastructure.Telemetry;
using Serilog;
using Serilog.Enrichers.Span;

namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for configuring Serilog on the host builder.
/// </summary>
internal static class SerilogRegistration
{
    /// <summary>
    /// Configures Serilog with environment-variable-driven log levels, framework overrides,
    /// span enrichment, console output, and conditional OTLP export.
    /// </summary>
    public static IHostBuilder ConfigureSerilog(this IHostBuilder hostBuilder)
    {
        var orchestratorLogLevel = LogLevelParser.Parse(
            Environment.GetEnvironmentVariable("LOG_LEVEL"),
            Serilog.Events.LogEventLevel.Information);

        hostBuilder.UseSerilog((ctx, lc) => lc
            .MinimumLevel.Is(orchestratorLogLevel)
            // Suppress noisy ASP.NET Core framework logging (health checks, static files, Blazor negotiation, auth)
            .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
            // Suppress Polly internal telemetry (StrategyExecuting/Executed fire at Debug on every call)
            .MinimumLevel.Override("Polly", Serilog.Events.LogEventLevel.Warning)
            // Suppress per-request HttpClient trace logs (Start/End fire at Debug on every outbound call)
            .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
            // Suppress HttpClientFactory handler lifecycle logging (cleanup cycle every ~10s)
            .MinimumLevel.Override("Microsoft.Extensions.Http", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithSpan()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level}] {Message:lj}{NewLine}{Exception}",
                theme: Serilog.Sinks.SystemConsole.Themes.ConsoleTheme.None)
            .WriteToOtlpIfConfigured("coding-agent-orchestrator", ctx.HostingEnvironment.EnvironmentName));

        return hostBuilder;
    }
}
