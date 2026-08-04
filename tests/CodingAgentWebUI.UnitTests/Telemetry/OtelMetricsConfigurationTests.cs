using System.Diagnostics.Metrics;
using AwesomeAssertions;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace CodingAgentWebUI.UnitTests.Telemetry;

/// <summary>
/// Verifies that the OTLP metrics exporter is configured with Cumulative temporality,
/// which is required for Prometheus / Grafana Cloud compatibility.
///
/// Background: The OTel .NET SDK defaults to Delta temporality for histograms and counters.
/// Prometheus expects Cumulative temporality — it interprets monotonically increasing bucket
/// counts as a time-series. Delta histograms are silently dropped by Grafana Cloud's OTLP
/// receiver, while gauges (which have no temporality) continue to export correctly.
/// This was the root cause of dispatch_queue_wait_time_seconds never appearing in Grafana.
///
/// Note on test approach: MetricReaderTemporalityPreference is a write-only configuration
/// on the OTLP exporter's internal PeriodicExportingMetricReader — it cannot be read back
/// via MeterProvider DI inspection or MeterListener. Instead, we verify the observable
/// *effect*: when TemporalityPreference is Cumulative, the total measurement count accumulates
/// across collection cycles; when Delta, each cycle resets to only the latest measurements.
/// </summary>
/// <remarks>
/// TODO [WARNING]: Neither test here exercises the actual production Program.cs or
/// CodingAgentWebUI.Agent/Program.cs DI configuration. Both tests construct an isolated
/// BaseExportingMetricReader with the correct setting applied by the test itself. The root
/// defect (missing TemporalityPreference on the production OTLP exporter) could be silently
/// reintroduced by a future refactor of Program.cs — e.g., switching from
/// .AddOtlpExporter((_, readerOptions) => ...) to .AddOtlpExporter() — without either test
/// failing. A test that builds the actual service collection and inspects the registered
/// MeterProvider's reader configuration would catch this regression. (TestQualityReviewer finding)
///
/// TODO [WARNING]: No test covers the CodingAgentWebUI.Agent/Program.cs temporality fix.
/// The agent binary received the same TemporalityPreference = Cumulative change as the
/// orchestrator but has its own MeterProvider construction in a separate process. A revert
/// or misconfiguration of the agent Program.cs would not be caught by any test in this file.
/// Consider adding agent-specific coverage when the agent program setup becomes unit-testable.
/// (TestQualityReviewer finding)
/// </remarks>
[Collection("Metrics")]
public sealed class OtelMetricsConfigurationTests : IDisposable
{
    private readonly Meter _meter;
    private readonly SpyMetricExporter _spy;
    private readonly BaseExportingMetricReader _reader;
    private readonly MeterProvider _provider;

    public OtelMetricsConfigurationTests()
    {
        _meter = new Meter("OtelMetricsConfigurationTests.Meter." + Guid.NewGuid());
        _spy = new SpyMetricExporter();

        // Mirror the configuration from Program.cs — the key setting is TemporalityPreference.Cumulative.
        // We use BaseExportingMetricReader directly (instead of AddOtlpExporter) because the OTLP exporter
        // requires a live endpoint; both use the same MetricReaderOptions under the hood.
        _reader = new BaseExportingMetricReader(_spy)
        {
            TemporalityPreference = MetricReaderTemporalityPreference.Cumulative
        };

        _provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(_meter.Name)
            .AddReader(_reader)
            .Build()!;
    }

    public void Dispose()
    {
        _provider.Dispose();
        _meter.Dispose();
    }

    /// <summary>
    /// With Cumulative temporality, the second collection cycle must include ALL measurements
    /// recorded since the provider started — not just those since the last collection.
    ///
    /// We use GetHistogramCount() (total number of recorded observations) rather than summing
    /// per-bucket BucketCount values. Summing bucket counts is fragile: if two observations
    /// land in the same bucket the bucket count increases but the number of non-zero buckets
    /// does not, so the sum could remain 1 even after two recordings — incorrectly failing
    /// a valid Cumulative configuration. GetHistogramCount() always equals the number of
    /// observations regardless of bucket placement.
    /// </summary>
    [Fact]
    public void OtlpMetricsExporter_CumulativeTemporality_HistogramObservationCountAccumulates()
    {
        var histogram = _meter.CreateHistogram<double>("test.queue.wait_time", "s");

        // Record first measurement and collect. We deliberately use the same value twice
        // (5.0) so both observations land in the same bucket — this is the scenario that
        // would cause a bucket-sum-based assertion to wrongly fail.
        histogram.Record(5.0);
        _reader.Collect();

        var firstObservationCount = _spy.TotalHistogramObservationCount("test.queue.wait_time");
        firstObservationCount.Should().Be(1, "first collection should capture the first measurement");

        // Record second measurement with the same value so both land in the same bucket.
        // Under Cumulative temporality the SDK re-reports the cumulative count (2).
        // Under Delta temporality the SDK reports only the new count since last collect (1).
        histogram.Record(5.0);
        _reader.Collect();

        var secondObservationCount = _spy.TotalHistogramObservationCount("test.queue.wait_time");

        // Cumulative: second collect must report count = 2 (all observations since start)
        // Delta: second collect would report count = 1 (only observations since last collect)
        secondObservationCount.Should().Be(2,
            "Cumulative temporality must accumulate all observations across collection cycles; " +
            "Delta temporality (the broken default) would reset to 1 after the second collect");
    }

    /// <summary>
    /// Minimal <see cref="BaseExporter{T}"/> spy that captures the total observation count
    /// (via <c>GetHistogramCount()</c>) for each histogram metric, keyed by metric name.
    ///
    /// Design note: We track the observation count reported by the most recent Export call.
    /// With Cumulative temporality the SDK re-sends the running total on every collect, so the
    /// most-recent value equals the cumulative total. With Delta the most-recent value equals
    /// only the increment since the last collect — which is what we want to distinguish.
    ///
    /// Thread-safety is not required — tests are sequential within the [Collection("Metrics")]
    /// serialization group.
    /// </summary>
    /// <remarks>
    /// TODO [WARNING]: If the SDK emits multiple Export batches within a single Collect() cycle,
    /// _latestObservationCounts retains only the last batch, which could mask a Delta regression
    /// in high-frequency scenarios under future SDK versions. The cumulative-count assertion on
    /// the behavioural test relies on this being stable. Under the current SDK version with a
    /// single-point histogram this is safe, but the assumption should be revisited if the SDK
    /// changes batch-splitting behaviour. (TestQualityReviewer finding)
    /// </remarks>
    private sealed class SpyMetricExporter : BaseExporter<Metric>
    {
        // Stores the observation count from the most recent Export call, keyed by metric name.
        // Uses GetHistogramCount() — the total number of recorded observations — rather than
        // summing per-bucket BucketCount values. Summing bucket counts is fragile when multiple
        // observations land in the same bucket (the non-zero-bucket count stays 1 even after
        // two recordings, causing assertions on distinct-observation counts to fail incorrectly).
        private readonly Dictionary<string, long> _latestObservationCounts = new();

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                if (metric.MetricType != MetricType.Histogram)
                    continue;

                long total = 0;
                foreach (ref readonly var metricPoint in metric.GetMetricPoints())
                {
                    // GetHistogramCount() returns the total number of observations recorded
                    // for this metric point — independent of bucket placement.
                    total += metricPoint.GetHistogramCount();
                }

                _latestObservationCounts[metric.Name] = total;
            }

            return ExportResult.Success;
        }

        /// <summary>
        /// Returns the total observation count from the most recent export for the given metric name.
        /// Under Cumulative temporality this equals all observations since the provider started.
        /// Under Delta it equals only the observations since the last collect.
        /// </summary>
        public long TotalHistogramObservationCount(string metricName) =>
            _latestObservationCounts.TryGetValue(metricName, out var count) ? count : 0;
    }
}
