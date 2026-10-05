using System.Collections.Concurrent;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace CodingAgent.Web.TestUtilities;

/// <summary>
/// Metric exporter that records, per instrument name, the tag sets of every exported metric point.
/// Register it on a host through <c>ConfigureOpenTelemetryMeterProvider(b =&gt;
/// b.AddReader(new BaseExportingMetricReader(exporter)))</c> and call
/// <c>MeterProvider.ForceFlush()</c> to see the series the host's OTLP exporter would send.
/// </summary>
public sealed class CapturingMetricExporter : BaseExporter<Metric>
{
    private readonly ConcurrentDictionary<string, ConcurrentBag<IReadOnlyDictionary<string, string>>> _points = new();

    /// <summary>
    /// The tag sets exported for <paramref name="instrumentName"/>, one entry per metric point and export.
    /// Empty when the instrument was never exported.
    /// </summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string>> PointsFor(string instrumentName) =>
        _points.TryGetValue(instrumentName, out var points) ? [.. points] : [];

    /// <inheritdoc/>
    public override ExportResult Export(in Batch<Metric> batch)
    {
        foreach (var metric in batch)
        {
            var points = _points.GetOrAdd(metric.Name, _ => []);
            foreach (ref readonly var point in metric.GetMetricPoints())
            {
                var tags = new Dictionary<string, string>();
                foreach (var tag in point.Tags)
                    tags[tag.Key] = tag.Value?.ToString() ?? "";
                points.Add(tags);
            }
        }

        return ExportResult.Success;
    }
}
