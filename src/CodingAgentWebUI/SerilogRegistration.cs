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

        hostBuilder.UseSerilog((ctx, lc) => lc
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

        return hostBuilder;
    }
}
