using CodingAgentWebUI.Infrastructure.Telemetry;
using Serilog;
using Serilog.Enrichers.Span;

namespace CodingAgentWebUI.Api;

/// <summary>
/// Extension methods for configuring Serilog on the Pipeline API host builder.
/// </summary>
internal static class ApiSerilogRegistration
{
    /// <summary>
    /// Configures Serilog with environment-variable-driven log levels,
    /// span enrichment, console output, and conditional OTLP export.
    /// Service name defaults to "coding-agent-api" (Req 8.4).
    /// </summary>
    public static IHostBuilder ConfigureApiSerilog(this IHostBuilder hostBuilder)
    {
        var logLevel = LogLevelParser.Parse(
            Environment.GetEnvironmentVariable("LOG_LEVEL"),
            Serilog.Events.LogEventLevel.Information);
        var dbLogLevel = LogLevelParser.Parse(
            Environment.GetEnvironmentVariable("DB_LOG_LEVEL"),
            Serilog.Events.LogEventLevel.Warning);

        hostBuilder.UseSerilog((ctx, lc) => lc
            .MinimumLevel.Is(logLevel)
            .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", dbLogLevel)
            .MinimumLevel.Override("Npgsql", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", Serilog.Events.LogEventLevel.Warning)
            // Suppress per-request auth handler Debug noise (fires on every authenticated request)
            .MinimumLevel.Override("Microsoft.AspNetCore.Authentication", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithSpan()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level}] {Message:lj}{NewLine}{Exception}",
                theme: Serilog.Sinks.SystemConsole.Themes.ConsoleTheme.None)
            .WriteToOtlpIfConfigured("coding-agent-api", ctx.HostingEnvironment.EnvironmentName));

        return hostBuilder;
    }
}
