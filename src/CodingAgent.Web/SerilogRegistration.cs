using CodingAgent.Infrastructure.Telemetry;
using CodingAgent.Pipeline;
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
            Environment.GetEnvironmentVariable(AgentDefaults.EnvLogLevel),
            LogEventLevel.Information);

        hostBuilder.UseSerilog((ctx, lc) => lc
            .ApplyWebHostLevels(orchestratorLogLevel)
            .WriteToHostConsole()
            .WriteToOtlpIfConfigured("coding-agent-web", ctx.HostingEnvironment.EnvironmentName));

        return hostBuilder;
    }

    /// <summary>The shared host defaults plus the web host's own overrides.</summary>
    internal static LoggerConfiguration ApplyWebHostLevels(this LoggerConfiguration lc, LogEventLevel minimumLevel) => lc
        .ApplyHostDefaults(minimumLevel)
        // Suppress AgentApiKey "not authenticated" noise from k8s health probes (every 5-10s).
        // Probes hit unauthenticated endpoints but still pass through UseAuthentication(), causing
        // AuthenticateResult.NoResult() to be logged at Warning by the framework. Genuine invalid-key
        // failures are logged directly via the injected Serilog.ILogger and are NOT affected by this override.
        .MinimumLevel.Override("Microsoft.AspNetCore.Authentication", LogEventLevel.Error)
        // YARP logs every forwarded call at Information ("Proxying to …", "Received HTTP/1.1 response …").
        // Forwarding errors (the API unreachable → 502, no activity → 504) are Warning and still pass;
        // calls the browser cancels are Debug in YARP and stay out.
        .MinimumLevel.Override("Yarp", LogEventLevel.Warning);
}
