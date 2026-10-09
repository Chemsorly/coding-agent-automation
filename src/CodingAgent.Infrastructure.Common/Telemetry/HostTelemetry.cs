using CodingAgent.Pipeline.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Enrichers.Span;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace CodingAgent.Infrastructure.Telemetry;

/// <summary>
/// Shared OpenTelemetry and Serilog configuration helpers for all host entry points.
/// Centralises the duplicated boilerplate (resource, noise filters, OTLP exporters,
/// shared meters/sources, log-level overrides, span enricher, console template) so that
/// a single change takes effect in every host.
/// </summary>
/// <remarks>
/// <para>
/// All five hosts (Api, Web, Scheduler, JobController, Agent) call
/// <see cref="AddHostOpenTelemetry"/> and <see cref="ApplyHostDefaults"/>. Each host
/// passes only what differs from the shared setup via the <c>configureTracing</c> /
/// <c>configureMetrics</c> callbacks and the boolean flags.
/// </para>
/// <para>
/// The <c>service.name</c> and <c>service.version</c> resource attributes are read
/// from the environment here, matching the rule in
/// <see cref="SerilogOtlpExtensions.WriteToOtlpIfConfigured"/> so that logs, traces and
/// metrics carry identical resource labels.
/// </para>
/// </remarks>
public static class HostTelemetry
{
    /// <summary>
    /// The standard console output template used by all long-lived host processes.
    /// Referenced by <see cref="WriteToHostConsole"/> and by <see cref="HostBootstrap"/>'s
    /// bootstrap logger so that all pre- and post-startup console output shares one format.
    /// </summary>
    public const string ConsoleOutputTemplate =
        "[{Timestamp:HH:mm:ss} {Level}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Registers OpenTelemetry tracing and (optionally) metrics with the service collection.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to register services into.</param>
    /// <param name="fallbackServiceName">
    /// The <c>service.name</c> resource attribute to use when <c>OTEL_SERVICE_NAME</c> is not set.
    /// </param>
    /// <param name="configureTracing">
    /// Optional callback to add host-specific trace sources or processors after the shared setup.
    /// Invoked inside <c>WithTracing</c>, after the shared sources and before the noise processor
    /// and OTLP exporter.
    /// </param>
    /// <param name="configureMetrics">
    /// Optional callback to add host-specific meters or views after the shared meters.
    /// Invoked inside <c>WithMetrics</c>, after the shared meters and before the OTLP exporter.
    /// </param>
    /// <param name="includeAspNetCoreInstrumentation">
    /// When <c>true</c> (default), ASP.NET Core request tracing and metrics are added.
    /// Set to <c>false</c> for the Agent, which is not a server-side host.
    /// </param>
    /// <param name="includeMetrics">
    /// When <c>true</c> (default), a metrics pipeline with OTLP export is registered.
    /// Set to <c>false</c> for the Agent, which records no metrics (issue #2980).
    /// </param>
    /// <returns>The <paramref name="services"/> for chaining.</returns>
    public static IServiceCollection AddHostOpenTelemetry(
        this IServiceCollection services,
        string fallbackServiceName,
        Action<TracerProviderBuilder>? configureTracing = null,
        Action<MeterProviderBuilder>? configureMetrics = null,
        bool includeAspNetCoreInstrumentation = true,
        bool includeMetrics = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(fallbackServiceName);

        var serviceName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME") ?? fallbackServiceName;
        var serviceVersion = Environment.GetEnvironmentVariable("SERVICE_VERSION") ?? "local";

        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                serviceName: serviceName,
                serviceVersion: serviceVersion))
            .WithTracing(t =>
            {
                // Pipeline source first so host-specific sources added via configureTracing
                // are appended after the shared ones.
                t.AddSource(PipelineTelemetry.SourceName);

                if (includeAspNetCoreInstrumentation)
                    t.AddAspNetCoreInstrumentation(opts =>
                        opts.Filter = OtelNoiseFilter.FilterAspNetCoreRequest);

                // HttpClient instrumentation always included (applies K8s API filter —
                // the agent makes no K8s calls so the filter is a no-op there).
                t.AddHttpClientInstrumentation(opts =>
                    opts.FilterHttpRequestMessage = OtelNoiseFilter.FilterHttpClientRequest);

                configureTracing?.Invoke(t);

                // Processor MUST precede the exporter (OtelNoiseFilter.cs:144): the processor
                // suppresses noise spans so they are not seen by any subsequent exporter.
                t.AddProcessor(new OtelNoiseSpanProcessor());
                t.AddOtlpExporter();
            });

        if (includeMetrics)
        {
            // TODO: [WARNING] The second AddOpenTelemetry() call here does not repeat
            // .ConfigureResource(...). This is correct today because the OTel Hosting SDK
            // shares a single provider across all AddOpenTelemetry() calls, so the resource
            // registered above is inherited by the metrics pipeline. However, the implicit
            // dependency on call order is non-obvious: if a future refactor removes the
            // tracing block or extracts the metrics block into a separate helper, the
            // service.name/service.version resource would silently be lost on metrics.
            // Consider calling ConfigureResource on both builders, or extracting resource
            // setup into a single upfront call before the two pipeline registrations.
            // (DotNetSpecialist review, issue #3446)
            services.AddOpenTelemetry()
                .WithMetrics(m =>
                {
                    if (includeAspNetCoreInstrumentation)
                        m.AddAspNetCoreInstrumentation();

                    m.AddHttpClientInstrumentation();

                    // Shared meters present in every server host. Agent pods omit metrics entirely
                    // (includeMetrics: false) — these meters must NOT appear in agent processes.
                    m.AddMeter(PipelineTelemetry.SourceName);
                    m.AddMeter(WorkDistributionTelemetry.MeterName);
                    m.AddMeter("System.Runtime");

                    configureMetrics?.Invoke(m);

                    // Prometheus requires Cumulative temporality; the OTLP exporter defaults to
                    // Delta for histograms/counters which Grafana Cloud silently drops.
                    m.AddOtlpExporter((_, readerOptions) =>
                        readerOptions.TemporalityPreference = MetricReaderTemporalityPreference.Cumulative);
                });
        }

        return services;
    }

    /// <summary>
    /// Applies the shared Serilog minimum-level and enricher defaults to a
    /// <see cref="LoggerConfiguration"/>.
    /// </summary>
    /// <remarks>
    /// Sets the process minimum level, raises the five noisy framework namespaces to
    /// <c>Warning</c>, and attaches the <c>FromLogContext</c> and <c>WithSpan</c> enrichers.
    /// Call this before adding host-specific overrides and sinks so that per-host overrides
    /// (e.g. <c>Microsoft.AspNetCore.Authentication → Error</c>) take effect via
    /// Serilog's longest-prefix-wins rule.
    /// </remarks>
    /// <param name="loggerConfiguration">The configuration to modify.</param>
    /// <param name="minimumLevel">The process-wide minimum log level.</param>
    /// <returns>The <paramref name="loggerConfiguration"/> for chaining.</returns>
    public static LoggerConfiguration ApplyHostDefaults(
        this LoggerConfiguration loggerConfiguration,
        LogEventLevel minimumLevel)
    {
        ArgumentNullException.ThrowIfNull(loggerConfiguration);

        return loggerConfiguration
            .MinimumLevel.Is(minimumLevel)
            // Suppress noisy ASP.NET Core framework logging (health checks, static files, auth)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            // Suppress Polly internal telemetry (StrategyExecuting/Executed fire at Debug on every call)
            .MinimumLevel.Override("Polly", LogEventLevel.Warning)
            // Suppress per-request HttpClient trace logs (Start/End fire at Debug on every outbound call)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            // Suppress HttpClientFactory handler lifecycle logging (cleanup cycle every ~10s)
            .MinimumLevel.Override("Microsoft.Extensions.Http", LogEventLevel.Warning)
            // Suppress OpenTelemetry SDK internal logs (export errors still pass at Warning+)
            .MinimumLevel.Override("OpenTelemetry", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithSpan();
    }

    /// <summary>
    /// Adds the standard host console sink using <see cref="ConsoleOutputTemplate"/>.
    /// </summary>
    /// <param name="loggerConfiguration">The configuration to modify.</param>
    /// <returns>The <paramref name="loggerConfiguration"/> for chaining.</returns>
    public static LoggerConfiguration WriteToHostConsole(
        this LoggerConfiguration loggerConfiguration)
    {
        ArgumentNullException.ThrowIfNull(loggerConfiguration);

        return loggerConfiguration
            .WriteTo.Console(outputTemplate: ConsoleOutputTemplate, theme: ConsoleTheme.None);
    }
}
