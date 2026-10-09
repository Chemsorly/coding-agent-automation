using CodingAgent.Infrastructure.Telemetry;
using Serilog;
using Serilog.Events;

namespace CodingAgent.Api;

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
            LogEventLevel.Information);
        var dbLogLevel = LogLevelParser.Parse(
            Environment.GetEnvironmentVariable("DB_LOG_LEVEL"),
            LogEventLevel.Warning);

        hostBuilder.UseSerilog((ctx, lc) => lc
            .ApplyHostDefaults(logLevel)
            // Longer prefix wins over the shared Microsoft.AspNetCore override — EF Core
            // database command logging is controlled separately via DB_LOG_LEVEL.
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", dbLogLevel)
            .MinimumLevel.Override("Npgsql", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            // Suppress AgentApiKey "not authenticated" noise from k8s health probes (every 5-10s).
            // Probes hit unauthenticated endpoints but still pass through UseAuthentication(), causing
            // AuthenticateResult.NoResult() to be logged at Warning by the framework. Genuine invalid-key
            // failures are logged directly via the injected Serilog.ILogger and are NOT affected by this override.
            // Was: LogEventLevel.Warning — probes emit at Warning so that level didn't suppress them.
            .MinimumLevel.Override("Microsoft.AspNetCore.Authentication", LogEventLevel.Error)
            .WriteToHostConsole()
            .WriteToOtlpIfConfigured("coding-agent-api", ctx.HostingEnvironment.EnvironmentName));

        return hostBuilder;
    }
}
