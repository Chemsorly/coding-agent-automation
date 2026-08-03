using System.Diagnostics.Metrics;
using AwesomeAssertions;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace CodingAgentWebUI.UnitTests.Telemetry;

/// <summary>
/// Verifies that the OTel SDK BaseExportingMetricReader correctly accumulates histogram bucket
/// counts under Cumulative temporality and resets them under Delta temporality.
///
/// These tests exercise the same temporality semantics as the production OTLP exporter
/// configured in both Program.cs files. They document the broken behaviour (Delta) that
/// caused quality_gate.* metrics to be silently dropped by Prometheus, and verify the
/// corrected behaviour (Cumulative) required for metrics to survive ephemeral worker pod
/// lifecycles.
///
/// See issue #1750 and brain entry technology/opentelemetry.md for context.
/// </summary>
// TODO: [WARNING] These tests verify SDK histogram temporality semantics but do not assert
// that PipelineTelemetry.SourceName is registered via AddMeter() in either Program.cs.
// A bug removing that AddMeter call would satisfy all tests here while breaking all four
// acceptance criteria (quality_gate.* metrics would not reach Prometheus). Consider adding
// a test that constructs a MeterProvider mirroring the production configuration and records
// a PipelineTelemetry instrument to confirm end-to-end registration.
[Collection("Metrics")]
public sealed class OtelMetricsConfigurationTests : IDisposable
{
    private readonly Meter _meter;

    public OtelMetricsConfigurationTests()
    {
        // Use a unique meter name per test class instance to avoid cross-test interference
        // from the static MeterProvider observation pipeline.
        _meter = new Meter($"Test.OtelTemporality.{Guid.NewGuid():N}");
    }

    public void Dispose() => _meter.Dispose();

    [Fact]
    public void OtlpMetricsExporter_CumulativeTemporality_HistogramBucketCountsAccumulate()
    {
        // Arrange: build a provider with Cumulative temporality (the production setting
        // applied by AddOtlpExporter((_, r) => r.TemporalityPreference = Cumulative)).
        var spy = new SpyMetricExporter();
        var reader = new BaseExportingMetricReader(spy)
        {
            TemporalityPreference = MetricReaderTemporalityPreference.Cumulative
        };
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(_meter.Name)
            .AddReader(reader)
            .Build();

        var histogram = _meter.CreateHistogram<double>("test.histogram.cumulative");

        // Act: record a measurement, flush, then record a second measurement and flush again.
        histogram.Record(1.0);
        meterProvider.ForceFlush();

        var countAfterFirstFlush = spy.TotalHistogramBucketCounts("test.histogram.cumulative");

        histogram.Record(1.0);
        meterProvider.ForceFlush();

        var countAfterSecondFlush = spy.TotalHistogramBucketCounts("test.histogram.cumulative");

        // Assert: cumulative — the second flush should show the total accumulated count (2),
        // not just the count since the last flush (1). This is the behaviour required for
        // Prometheus to correctly reconstruct time series from ephemeral worker pod metrics.
        countAfterFirstFlush.Should().Be(1, "first flush should show 1 measurement");
        countAfterSecondFlush.Should().Be(2, "cumulative: second flush should accumulate to 2, not reset to 1");
    }

    [Fact]
    public void OtlpMetricsExporter_DeltaTemporality_HistogramBucketCountsReset()
    {
        // Arrange: build a provider with Delta temporality (the broken default that caused
        // quality_gate.* metrics to be invisible in Prometheus for issue #1750).
        // TODO: [WARNING] This test documents broken behaviour but does not guard against regression.
        // If the production fix is reverted (restoring Delta default), the Cumulative test above will
        // fail, but this test will pass — providing false comfort. Consider adding a test that directly
        // asserts the production AddOtlpExporter lambda produces Cumulative semantics (i.e. exercises
        // the actual two-argument overload used in Program.cs, not just the SDK behaviour in isolation).
        var spy = new SpyMetricExporter();
        var reader = new BaseExportingMetricReader(spy)
        {
            TemporalityPreference = MetricReaderTemporalityPreference.Delta
        };
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(_meter.Name)
            .AddReader(reader)
            .Build();

        var histogram = _meter.CreateHistogram<double>("test.histogram.delta");

        // Act: record a measurement, flush, then record a second measurement and flush again.
        histogram.Record(1.0);
        meterProvider.ForceFlush();

        var countAfterFirstFlush = spy.TotalHistogramBucketCounts("test.histogram.delta");

        histogram.Record(1.0);
        meterProvider.ForceFlush();

        var countAfterSecondFlush = spy.TotalHistogramBucketCounts("test.histogram.delta");

        // Assert: delta — the second flush should show only the measurements since the
        // last flush (1), not the accumulated total (2). This documents the broken behaviour
        // that caused ephemeral worker pod metrics to produce incomplete Prometheus time series.
        countAfterFirstFlush.Should().Be(1, "first flush should show 1 measurement");
        countAfterSecondFlush.Should().Be(1, "delta: second flush resets to 1, losing prior measurements");
    }

    /// <summary>
    /// Captures the total histogram bucket count per metric name on each export cycle.
    /// Used to observe whether the metric reader is using Cumulative or Delta temporality
    /// without requiring a live OTLP endpoint or reflection into SDK internals.
    /// </summary>
    private sealed class SpyMetricExporter : BaseExporter<Metric>
    {
        private readonly Dictionary<string, long> _latestBucketCounts = new();

        public override ExportResult Export(in Batch<Metric> batch)
        {
            foreach (var metric in batch)
            {
                if (metric.MetricType != MetricType.Histogram)
                    continue;

                // TODO: [WARNING] Summing all histogram bucket counts is a fragile proxy for
                // "number of measurements". It works here because both Record(1.0) calls land
                // in the same bucket, but measurements spread across different buckets (e.g.
                // Record(1.0) then Record(100.0)) would produce the same total as two same-bucket
                // records, masking incorrect aggregation. Consider replacing with mp.GetHistogramCount()
                // which directly returns the authoritative count of recorded measurements.
                long total = 0;
                foreach (ref readonly var mp in metric.GetMetricPoints())
                    foreach (var bucket in mp.GetHistogramBuckets())
                        total += bucket.BucketCount;

                _latestBucketCounts[metric.Name] = total;
            }

            return ExportResult.Success;
        }

        public long TotalHistogramBucketCounts(string name) =>
            _latestBucketCounts.TryGetValue(name, out var count) ? count : 0;
    }
}
