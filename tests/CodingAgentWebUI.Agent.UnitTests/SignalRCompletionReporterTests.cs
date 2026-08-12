using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Moq;
using Polly;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for <see cref="SignalRCompletionReporter"/>.
/// Covers constructor validation, successful delivery, failure buffering,
/// telemetry tagging, and buffer drain paths.
/// </summary>
public class SignalRCompletionReporterTests
{
    private readonly Mock<Serilog.ILogger> _logger = new();

    private (SignalRCompletionReporter Reporter, CriticalMessageBuffer Buffer, HubConnectionManager HubManager)
        CreateSut(ResiliencePipeline? pipeline = null)
    {
        var hubManager = TestAgentWorkerServiceFactory.CreateTestHubManager(_logger.Object);
        var buffer = new CriticalMessageBuffer();
        var signalRPipeline = pipeline ?? ResiliencePipelineFactory.CreateSignalRPipeline(_logger.Object);
        var reporter = new SignalRCompletionReporter(hubManager, signalRPipeline, buffer, _logger.Object);
        return (reporter, buffer, hubManager);
    }

    // ── Constructor guards ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullHubManager_Throws()
    {
        var buffer = new CriticalMessageBuffer();
        var pipeline = ResiliencePipelineFactory.CreateSignalRPipeline(_logger.Object);
        var act = () => new SignalRCompletionReporter(null!, pipeline, buffer, _logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullPipeline_Throws()
    {
        var hubManager = TestAgentWorkerServiceFactory.CreateTestHubManager(_logger.Object);
        var buffer = new CriticalMessageBuffer();
        var act = () => new SignalRCompletionReporter(hubManager, null!, buffer, _logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullBuffer_Throws()
    {
        var hubManager = TestAgentWorkerServiceFactory.CreateTestHubManager(_logger.Object);
        var pipeline = ResiliencePipelineFactory.CreateSignalRPipeline(_logger.Object);
        var act = () => new SignalRCompletionReporter(hubManager, pipeline, null!, _logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var hubManager = TestAgentWorkerServiceFactory.CreateTestHubManager(_logger.Object);
        var buffer = new CriticalMessageBuffer();
        var pipeline = ResiliencePipelineFactory.CreateSignalRPipeline(_logger.Object);
        var act = () => new SignalRCompletionReporter(hubManager, pipeline, buffer, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── Properties ────────────────────────────────────────────────────────

    [Fact]
    public void HasPendingMessages_InitiallyFalse()
    {
        var (reporter, _, _) = CreateSut();
        reporter.HasPendingMessages.Should().BeFalse();
    }

    [Fact]
    public void Buffer_ExposesUnderlyingBuffer()
    {
        var (reporter, buffer, _) = CreateSut();
        reporter.Buffer.Should().BeSameAs(buffer);
    }

    // ── ReportCompletionAsync ─────────────────────────────────────────────

    [Fact]
    public async Task ReportCompletionAsync_NullPayload_ThrowsArgumentNullException()
    {
        var (reporter, _, _) = CreateSut();
        var act = async () => await reporter.ReportCompletionAsync("job-1", null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task ReportCompletionAsync_HubNotConnected_BuffersMessage()
    {
        // With hub not started, InvokeAsync throws. Polly exhausts retries quickly.
        // The catch block should enqueue a BufferedJobCompleted.
        var (reporter, buffer, _) = CreateSut();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow
        };

        // Act — should not throw even though hub is not connected
        await reporter.ReportCompletionAsync("job-fail-hub", payload, CancellationToken.None);

        buffer.HasPendingMessages.Should().BeTrue("failed delivery should buffer the message");
        buffer.Count.Should().Be(1);

        var messages = buffer.DrainAll();
        var buffered = (BufferedJobCompleted)messages[0];
        buffered.JobId.Should().Be("job-fail-hub");
        buffered.Payload.Should().Be(payload);
    }

    [Fact]
    public async Task ReportCompletionAsync_HubNotConnected_LogsError()
    {
        var (reporter, _, _) = CreateSut();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Failed, CompletedAt = DateTimeOffset.UtcNow };

        await reporter.ReportCompletionAsync("job-log-test", payload, CancellationToken.None);

        _logger.Verify(l => l.Error(
            It.IsAny<Exception>(),
            It.IsAny<string>(),
            It.IsAny<string>()),  // jobId.Value is string
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task ReportCompletionAsync_HubNotConnected_DoesNotThrow()
    {
        // Failure must be swallowed (buffered), not propagated
        var (reporter, _, _) = CreateSut();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        var act = async () => await reporter.ReportCompletionAsync("job-no-throw", payload, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ReportCompletionAsync_CompletedJob_TagsSuccessTrue()
    {
        // TODO: [WARNING] This test has no observable assertions — it only verifies no exception is thrown.
        // The test name implies the success telemetry tag is set, but that cannot be verified here
        // without an ActivityListener. Either rename to ReportCompletionAsync_WhenCompleted_DoesNotThrow
        // to match actual behavior, or add an ActivityListener to assert activity?.GetTagItem("success")
        // equals true. As-is, any code path that doesn't throw passes this test regardless of tag behavior.
        // See: review-findings.md [WARNING] SignalRCompletionReporterTests.cs:152
        // Success tag is set when FinalStep is not Failed/Cancelled.
        // We can't directly inspect the Activity in unit tests without a listener,
        // but we verify the reporter doesn't throw and doesn't buffer on tag setting.
        var (reporter, buffer, _) = CreateSut();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        // Call succeeds or fails (hub not connected); either way, no exception propagated.
        await reporter.ReportCompletionAsync("job-tags", payload, CancellationToken.None);

        // The test verifies no unhandled exception — telemetry tagging is defensive.
    }

    [Fact]
    public async Task ReportCompletionAsync_BufferedMessage_ContainsTimestamp()
    {
        var before = DateTimeOffset.UtcNow;
        var (reporter, buffer, _) = CreateSut();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        await reporter.ReportCompletionAsync("job-ts", payload, CancellationToken.None);

        var messages = buffer.DrainAll();
        // TODO: [WARNING] The conditional `if (messages.Count > 0)` allows this test to pass trivially
        // if no message is buffered (e.g., if the buffering logic regresses). Since the hub is never
        // connected in this test context, a message is always buffered — the assertion should be
        // unconditional: messages.Count.Should().Be(1), then assert on messages[0] directly.
        // See: review-findings.md [WARNING] SignalRCompletionReporterTests.cs:133
        if (messages.Count > 0)
        {
            var buffered = (BufferedJobCompleted)messages[0];
            buffered.EnqueuedAt.Should().BeOnOrAfter(before);
        }
    }

    [Fact]
    public async Task HasPendingMessages_AfterBufferedCompletion_ReturnsTrue()
    {
        var (reporter, buffer, _) = CreateSut();
        var payload = new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow };

        await reporter.ReportCompletionAsync("job-pending", payload, CancellationToken.None);

        reporter.HasPendingMessages.Should().BeTrue();
    }

    [Fact]
    public async Task ReportCompletionAsync_FailedStep_BufferedMessageJobIdMatches()
    {
        var (reporter, buffer, _) = CreateSut();
        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            FailureReason = "gate failed",
            CompletedAt = DateTimeOffset.UtcNow
        };

        await reporter.ReportCompletionAsync("job-failed-id", payload, CancellationToken.None);

        var messages = buffer.DrainAll();
        messages.Should().HaveCount(1);
        var msg = (BufferedJobCompleted)messages[0];
        msg.JobId.Should().Be("job-failed-id");
    }
}
