using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

namespace CodingAgent.Infrastructure.Telemetry;

/// <summary>
/// Shared startup helpers for all four host entry points (Api, Scheduler, JobController, Web).
/// Centralises the bootstrap-logger configuration and the startup-identity log format so that
/// a single change (e.g. adjusting the output template for Loki ingestion) takes effect
/// everywhere instead of having to be applied in four separate Program.cs files.
/// </summary>
public static class HostBootstrap
{
    /// <summary>
    /// Builds and returns a Serilog bootstrap logger that captures output before
    /// <c>UseSerilog</c> takes over at <c>Build()</c>.
    /// </summary>
    /// <returns>
    /// A <see cref="Serilog.ILogger"/> configured with <c>MinimumLevel.Information</c>
    /// and a plain console sink using the project-standard output template.
    /// Assign the return value to <c>Log.Logger</c> at the top of each host <c>Program.cs</c>.
    /// </returns>
    // TODO: The concrete runtime type returned by LoggerConfiguration.CreateBootstrapLogger() is
    // Serilog.Extensions.Hosting.ReloadableLogger which implements IDisposable, but the public
    // return type here is Serilog.ILogger (non-disposable). Callers that store the value outside
    // Log.Logger (e.g. tests) must cast to IDisposable to dispose the console sink. Consider
    // returning IDisposable or making callers aware of the lifetime concern. In production the OS
    // reclaims the handle at process exit, so this is benign today. (review-findings #2865)
    public static Serilog.ILogger CreateBootstrapLogger() =>
        new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level}] {Message:lj}{NewLine}{Exception}",
                theme: ConsoleTheme.None)
            .CreateBootstrapLogger();

    /// <summary>
    /// Emits the startup-identity log line in the project-standard format.
    /// Call this after assigning <c>Log.Logger</c> from <see cref="CreateBootstrapLogger"/>.
    /// </summary>
    /// <param name="serviceLabel">
    /// A human-readable display name for the service (e.g. "Pipeline API", "Scheduler",
    /// "Job Controller"). This is the only part that varies per host.
    /// </param>
    /// <param name="serviceName">
    /// The OTEL service name (e.g. "coding-agent-api"). Read from <c>OTEL_SERVICE_NAME</c>
    /// in each host and passed here so the helper does not read env vars directly.
    /// </param>
    /// <param name="version">
    /// The service version string. Read from <c>SERVICE_VERSION</c> in each host
    /// (defaulting to <c>"local"</c>) and passed here.
    /// </param>
    public static void LogStartupIdentity(string serviceLabel, string serviceName, string version)
    {
        ArgumentNullException.ThrowIfNull(serviceLabel);
        ArgumentNullException.ThrowIfNull(serviceName);
        ArgumentNullException.ThrowIfNull(version);

        Log.Information(
            "{ServiceLabel} starting: ServiceName={ServiceName} Version={Version}",
            serviceLabel, serviceName, version);
    }
}
