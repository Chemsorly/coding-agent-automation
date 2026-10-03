using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using Moq;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Tests for <see cref="IJobCompletionReporter"/> interface behavioral contract.
/// </summary>
public class IJobCompletionReporterTests
{
    // ── Behavioral tests: mock completion reporter ───────────────────────

    [Fact]
    public async Task MockReporter_ReportCompletionAsync_CanBeInvoked()
    {
        var mock = new Mock<IJobCompletionReporter>();
        mock.Setup(x => x.ReportCompletionAsync(
                It.IsAny<JobId>(),
                It.IsAny<JobCompletionPayload>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow
        };

        await mock.Object.ReportCompletionAsync("job-123", payload, CancellationToken.None);

        mock.Verify(x => x.ReportCompletionAsync(new JobId("job-123"), payload, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task MockReporter_ReportCompletionAsync_PropagatesFailurePayload()
    {
        var mock = new Mock<IJobCompletionReporter>();
        mock.Setup(x => x.ReportCompletionAsync(
                It.IsAny<JobId>(),
                It.IsAny<JobCompletionPayload>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Failed,
            FailureReason = "Quality gates failed",
            CompletedAt = DateTimeOffset.UtcNow
        };

        await mock.Object.ReportCompletionAsync("job-fail", payload, CancellationToken.None);

        mock.Verify(x => x.ReportCompletionAsync(
            new JobId("job-fail"),
            It.Is<JobCompletionPayload>(p => p.FinalStep == PipelineStep.Failed && p.FailureReason == "Quality gates failed"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
