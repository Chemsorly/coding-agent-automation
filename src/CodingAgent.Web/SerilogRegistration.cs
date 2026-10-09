using CodingAgent.Infrastructure.Telemetry;
using Serilog;
using Serilog.Events;

namespace CodingAgent.Web;

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
            LogEventLevel.Information);

        hostBuilder.UseSerilog((ctx, lc) => lc
            .ApplyHostDefaults(orchestratorLogLevel)
            // Suppress AgentApiKey "not authenticated" noise from k8s health probes (every 5-10s).
            // Probes hit unauthenticated endpoints but still pass through UseAuthentication(), causing
            // AuthenticateResult.NoResult() to be logged at Warning by the framework. Genuine invalid-key
            // failures are logged directly via the injected Serilog.ILogger and are NOT affected by this override.
            .MinimumLevel.Override("Microsoft.AspNetCore.Authentication", LogEventLevel.Error)
            .WriteToHostConsole()
            .WriteToOtlpIfConfigured("coding-agent-web", ctx.HostingEnvironment.EnvironmentName));

        return hostBuilder;
    }
}
