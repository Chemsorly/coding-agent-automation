using System.Diagnostics;
using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for <see cref="UnverifiedInputTelemetry"/>. The span is a bare <see cref="Activity"/> and not one from an
/// <see cref="ActivityListener"/>, which is process-wide and would pick up spans from tests running in parallel.
/// </summary>
public class UnverifiedInputTelemetryTests
{
    private static IssueDetail Issue(bool verified) => new()
    {
        Identifier = "42",
        Title = "Title",
        Description = "Body",
        Labels = [],
        IsAuthorVerified = verified
    };

    private static List<IssueComment> TwoVerifiedOneNullOneUnverified() =>
    [
        Comment("1", true),
        Comment("2", true),
        Comment("3", null),
        Comment("4", false)
    ];

    private static IssueComment Comment(string id, bool? verified) => new()
    {
        Id = id,
        Author = "someone",
        Body = "body",
        CreatedAt = DateTime.UnixEpoch,
        IsVerified = verified
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RecordIssueContext_CountsUnverifiedComments(bool authorVerified)
    {
        using var activity = new Activity("test");

        UnverifiedInputTelemetry.RecordIssueContext(
            activity, Mock.Of<ILogger>(), "run-1", Issue(authorVerified), TwoVerifiedOneNullOneUnverified());

        activity.GetTagItem("pipeline.comments.total").Should().Be(4);
        activity.GetTagItem("pipeline.comments.unverified").Should().Be(1);
        activity.GetTagItem("pipeline.issue.author_verified").Should().Be(authorVerified);
    }

    [Fact]
    public void RecordIssueContext_WritesOneInformationLineWithTheRunId()
    {
        var sink = new CollectingSink();
        using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        UnverifiedInputTelemetry.RecordIssueContext(
            null, logger, "run-1", Issue(verified: false), TwoVerifiedOneNullOneUnverified());

        var logEvent = sink.Events.Should().ContainSingle().Subject;
        logEvent.Level.Should().Be(LogEventLevel.Information);
        logEvent.Properties["RunId"].ToString().Should().Be("\"run-1\"");
        logEvent.Properties["UnverifiedComments"].ToString().Should().Be("1");
        logEvent.Properties["TotalComments"].ToString().Should().Be("4");
        logEvent.Properties["AuthorVerified"].ToString().Should().Be("False");
    }

    [Fact]
    public void RecordIssueContext_WithoutComments_CountsZero()
    {
        using var activity = new Activity("test");

        UnverifiedInputTelemetry.RecordIssueContext(activity, Mock.Of<ILogger>(), "run-1", Issue(verified: true), []);

        activity.GetTagItem("pipeline.comments.total").Should().Be(0);
        activity.GetTagItem("pipeline.comments.unverified").Should().Be(0);
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
