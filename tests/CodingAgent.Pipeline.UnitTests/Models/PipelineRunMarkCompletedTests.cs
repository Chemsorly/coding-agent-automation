using System.Threading;
using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

public class PipelineRunMarkCompletedTests
{
    private static PipelineRun CreateRun() => new()
    {
        RunId = "run-1",
        IssueIdentifier = "1",
        IssueTitle = "Test",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        StartedAt = DateTime.UtcNow
    };

    [Fact]
    public void MarkCompleted_SetsBothProperties()
    {
        var run = CreateRun();
        var before = DateTimeOffset.UtcNow;

        run.MarkCompleted();

        var after = DateTimeOffset.UtcNow;
#pragma warning disable CS0618
        run.CompletedAt.Should().NotBeNull();
        run.CompletedAt!.Value.Should().BeOnOrAfter(before.UtcDateTime).And.BeOnOrBefore(after.UtcDateTime);
#pragma warning restore CS0618
        run.CompletedAtOffset.Should().NotBeNull();
        run.CompletedAtOffset!.Value.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public void MarkCompleted_BothPropertiesRepresentSameInstant()
    {
        var run = CreateRun();

        run.MarkCompleted();

#pragma warning disable CS0618
        run.CompletedAt!.Value.Should().Be(run.CompletedAtOffset!.Value.UtcDateTime);
#pragma warning restore CS0618
    }

    [Fact]
    public void MarkCompleted_WithTimestamp_SetsBothFromProvidedValue()
    {
        var run = CreateRun();
        var timestamp = new DateTimeOffset(2026, 6, 15, 10, 30, 0, TimeSpan.FromHours(2));

        run.MarkCompleted(timestamp);

#pragma warning disable CS0618
        run.CompletedAt.Should().Be(timestamp.UtcDateTime);
#pragma warning restore CS0618
        run.CompletedAtOffset.Should().Be(timestamp);
    }

    [Fact]
    public void MarkCompleted_WithTimestamp_CalledTwice_DoesNotChangeTimestamp()
    {
        // Primary acceptance criterion test — clock-resolution-independent.
        // Two explicit distinct timestamps guarantee the assertion fails deterministically
        // if the guard is absent (the second call would overwrite the first).
        var run = CreateRun();
        var firstTimestamp = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var secondTimestamp = new DateTimeOffset(2026, 1, 1, 13, 0, 0, TimeSpan.Zero);

        run.MarkCompleted(firstTimestamp);
        run.MarkCompleted(secondTimestamp);

        run.CompletedAtOffset.Should().Be(firstTimestamp,
            "MarkCompleted(timestamp) is idempotent — second call must not overwrite CompletedAtOffset");
#pragma warning disable CS0618
        run.CompletedAt.Should().Be(firstTimestamp.UtcDateTime,
            "MarkCompleted(timestamp) is idempotent — second call must not overwrite CompletedAt");
#pragma warning restore CS0618
    }

    [Fact]
    public void MarkCompleted_CalledTwice_DoesNotChangeTimestamp()
    {
        // Thread.Sleep(1) advances the clock so the second call would produce a
        // measurably later DateTimeOffset.UtcNow if the guard were absent.
        // The equality assertion on the frozen first value is what proves idempotency.
        // TODO [WARNING] (TestQualityReviewer): Thread.Sleep(1) may not advance the system clock by even one tick on some
        // platforms (Windows default timer resolution is 15.6 ms; Linux sub-millisecond precision still risks same-tick
        // collisions). If both UtcNow calls land on the same tick, this test passes even without the idempotency guard,
        // making it a false positive. The explicit-timestamp overload test above is the deterministic proof.
        // Consider increasing to Thread.Sleep(50) or injecting a fake clock to eliminate the fragility.
        var run = CreateRun();

        run.MarkCompleted();
        var firstCompletedAtOffset = run.CompletedAtOffset;
#pragma warning disable CS0618
        var firstCompletedAt = run.CompletedAt;
#pragma warning restore CS0618

        Thread.Sleep(1);
        run.MarkCompleted();

#pragma warning disable CS0618
        run.CompletedAt.Should().Be(firstCompletedAt,
            "MarkCompleted is idempotent — second call must not overwrite the first timestamp");
#pragma warning restore CS0618
        run.CompletedAtOffset.Should().Be(firstCompletedAtOffset,
            "MarkCompleted is idempotent — second call must not overwrite CompletedAtOffset");
    }
}
