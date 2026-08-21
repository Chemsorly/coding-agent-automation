using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.UnitTests.Telemetry;

public class HistogramBucketBoundaryTests
{
    // ── PipelineTelemetry histograms ────────────────────────────────────────────

    // TODO: Add assertion that bucket boundaries are monotonically increasing (strictly ascending)
    // to catch misordering bugs that would silently break quantile calculations.
    [Fact]
    public void JobDuration_HasExpectedBucketBoundaries()
    {
        var boundaries = PipelineTelemetry.JobDuration.Advice?.HistogramBucketBoundaries;
        boundaries.Should().NotBeNull();
        boundaries.Should().Equal(30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 18000, 21600);
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
        boundaries.Should().Equal(5, 10, 30, 60, 120, 300, 600, 900, 1800, 3600);
    }

    [Fact]
    public void PendingDuration_HasSecondScaleBucketBoundaries()
    {
        var boundaries = WorkDistributionTelemetry.PendingDuration.Advice?.HistogramBucketBoundaries;
        boundaries.Should().NotBeNull("PendingDuration must have explicit InstrumentAdvice boundaries");
        boundaries.Should().Equal(5, 10, 30, 60, 120, 300, 600, 900, 1800, 3600);
    }

    [Fact]
    public void JobExecutionDuration_HasSecondScaleBucketBoundaries()
    {
        var boundaries = WorkDistributionTelemetry.JobExecutionDuration.Advice?.HistogramBucketBoundaries;
        boundaries.Should().NotBeNull("JobExecutionDuration must have explicit InstrumentAdvice boundaries");
        // Aligned with PipelineTelemetry.JobDuration — same job lifecycle, same cardinality requirements
        boundaries.Should().Equal(30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 18000, 21600);
    }

    [Fact]
    public void TimeoutExecutionAge_HasSecondScaleBucketBoundaries()
    {
        var boundaries = WorkDistributionTelemetry.TimeoutExecutionAge.Advice?.HistogramBucketBoundaries;
        boundaries.Should().NotBeNull("TimeoutExecutionAge must have explicit InstrumentAdvice boundaries");
        boundaries.Should().Equal(30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 18000, 21600);
    }
}
