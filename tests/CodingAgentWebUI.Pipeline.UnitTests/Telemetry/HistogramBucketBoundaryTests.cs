using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.UnitTests.Telemetry;

public class HistogramBucketBoundaryTests
{
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
}
