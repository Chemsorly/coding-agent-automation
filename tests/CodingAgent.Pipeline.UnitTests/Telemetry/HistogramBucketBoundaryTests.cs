using AwesomeAssertions;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.UnitTests.Telemetry;

public class HistogramBucketBoundaryTests
{
    // ── PipelineTelemetry histograms ────────────────────────────────────────────

    // TODO: Add assertion that bucket boundaries are monotonically increasing (strictly ascending)
    // to catch misordering bugs that would silently break quantile calculations.
    [Fact]
    public void RunDuration_HasExpectedBucketBoundaries()
    {
        var boundaries = PipelineTelemetry.RunDuration.Advice?.HistogramBucketBoundaries;
        boundaries.Should().NotBeNull("RunDuration must have explicit InstrumentAdvice boundaries (issue #2967)");
        boundaries.Should().Equal(60, 300, 600, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 21600, 28800, 43200);
    }

    [Fact]
    public void RunStepDuration_HasExpectedBucketBoundaries()
    {
        var boundaries = PipelineTelemetry.RunStepDuration.Advice?.HistogramBucketBoundaries;
        boundaries.Should().NotBeNull("RunStepDuration must have explicit InstrumentAdvice boundaries (issue #2974)");
        boundaries.Should().Equal(5, 15, 30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800);
    }

    [Fact]
    public void QueueWaitTime_HasExpectedBucketBoundaries()
    {
        var boundaries = PipelineTelemetry.QueueWaitTime.Advice?.HistogramBucketBoundaries;
        boundaries.Should().NotBeNull();
        boundaries.Should().Equal(5, 10, 30, 60, 120, 300, 600, 1200, 1800, 3600);
    }

    // ── WorkDistributionTelemetry histograms ────────────────────────────────────
    // These guard against removing InstrumentAdvice, which would revert to the SDK default
    // ms-scale boundaries (max = 1000ms) — useless for dispatch durations measured in seconds.

    [Fact]
    public void DispatchLatency_HasSecondScaleBucketBoundaries()
    {
        var boundaries = WorkDistributionTelemetry.DispatchLatency.Advice?.HistogramBucketBoundaries;
        boundaries.Should().NotBeNull("DispatchLatency must have explicit InstrumentAdvice boundaries");
        // Extended in issue #2976 to prevent p95 saturation at 3600 s.
        boundaries.Should().Equal(5, 10, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200, 14400, 28800, 43200, 86400);
    }

    [Fact]
    public void JobExecutionDuration_HasSecondScaleBucketBoundaries()
    {
        var boundaries = WorkDistributionTelemetry.JobExecutionDuration.Advice?.HistogramBucketBoundaries;
        boundaries.Should().NotBeNull("JobExecutionDuration must have explicit InstrumentAdvice boundaries");
        // Covers the full range of job execution durations (30s → 6h). Note: the bucket set
        // previously matched PipelineTelemetry.JobDuration, which was removed in issue #2967;
        // the replacement is PipelineTelemetry.RunDuration with different buckets. These
        // workdistribution buckets are intentionally kept at the original values.
        boundaries.Should().Equal(30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 18000, 21600);
    }

    [Fact]
    public void TimeoutExecutionAge_HasSecondScaleBucketBoundaries()
    {
        var boundaries = WorkDistributionTelemetry.TimeoutExecutionAge.Advice?.HistogramBucketBoundaries;
        boundaries.Should().NotBeNull("TimeoutExecutionAge must have explicit InstrumentAdvice boundaries");
        boundaries.Should().Equal(30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 18000, 21600);
    }

    // ── New QGC process-level histograms (issue #2367) ──────────────────────────

    [Fact]
    public void PostPrCiDuration_HasExpectedBucketBoundaries()
    {
        var boundaries = PipelineTelemetry.PostPrCiDuration.Advice?.HistogramBucketBoundaries;
        boundaries.Should().NotBeNull("PostPrCiDuration must have explicit InstrumentAdvice boundaries");
        boundaries.Should().Equal(5, 10, 30, 60, 120, 300, 600, 1200, 1800, 3600);
    }

    [Fact]
    public void ExternalCiDuration_HasExpectedBucketBoundaries()
    {
        var boundaries = PipelineTelemetry.ExternalCiDuration.Advice?.HistogramBucketBoundaries;
        boundaries.Should().NotBeNull(
            "ExternalCiDuration must have explicit InstrumentAdvice boundaries to prevent " +
            "exponential histogram emission to Grafana Cloud OTLP (issue #2367)");
        boundaries.Should().Equal(5, 10, 30, 60, 120, 300, 600, 1200, 1800, 3600);
    }

    [Fact]
    public void QgcProcessDuration_HasExpectedBucketBoundaries()
    {
        var boundaries = PipelineTelemetry.QgcProcessDuration.Advice?.HistogramBucketBoundaries;
        boundaries.Should().NotBeNull("QgcProcessDuration must have explicit InstrumentAdvice boundaries (issue #2367)");
        boundaries.Should().Equal(5, 10, 30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600);
    }

    // ── New WorkDistributionTelemetry histograms (issue #2976) ───────────────────────────────

    [Fact]
    public void PodStartSeconds_HasExpectedBucketBoundaries()
    {
        var boundaries = WorkDistributionTelemetry.PodStartSeconds.Advice?.HistogramBucketBoundaries;
        boundaries.Should().NotBeNull("PodStartSeconds must have explicit InstrumentAdvice boundaries");
        boundaries.Should().Equal(5, 10, 20, 30, 60, 120, 300, 600);
    }
}
