using OpenTelemetry.Metrics;

namespace CodingAgent.Infrastructure.Telemetry;

/// <summary>
/// Seeds counters with zero-value series at host startup so that Prometheus <c>increase()</c>
/// sees the first real increment after a deploy (a series that starts at 1 shows no increase).
/// </summary>
public static class MetricPreInitialization
{
    /// <summary>
    /// Builds the host's <see cref="MeterProvider"/>, runs <paramref name="emitZeroSeries"/>, and flushes
    /// the resulting series to the exporter.
    /// </summary>
    /// <remarks>
    /// The OpenTelemetry hosting integration only builds the <see cref="MeterProvider"/> when the host
    /// starts, and a counter drops every measurement made before a provider listens to its meter. Calling
    /// <c>Add(0)</c> right after <c>builder.Build()</c> therefore records nothing. Resolving the provider
    /// here builds it first, so the zero series reach the exporter.
    /// </remarks>
    /// <param name="services">The built host's service provider.</param>
    /// <param name="emitZeroSeries">Emits <c>Add(0)</c> for every series to seed.</param>
    public static void Run(IServiceProvider services, Action emitZeroSeries)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(emitZeroSeries);

        var meterProvider = (MeterProvider?)services.GetService(typeof(MeterProvider));
        if (meterProvider is null)
        {
            Serilog.Log.Warning("No MeterProvider is registered; zero-value counter series are not exported");
            return;
        }

        emitZeroSeries();
        meterProvider.ForceFlush();
    }
}
