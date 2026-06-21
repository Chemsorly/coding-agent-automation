using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

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
}
