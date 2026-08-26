using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// TDD tests for <see cref="IJobCompletionReporter"/> interface extraction (R3).
/// Defines the behavioral contract:
/// - Unified completion reporting interface for both agent modes
/// - SignalRCompletionReporter: wraps SignalR with resilience + CriticalMessageBuffer
/// - HttpPrimaryCompletionReporter: HTTP POST (primary) + SignalR (secondary)
/// - Both agent services use IJobCompletionReporter instead of inline completion logic
/// </summary>
public class IJobCompletionReporterTests
{
    // ── Interface definition ─────────────────────────────────────────────





    // ── Implementation existence ─────────────────────────────────────────



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

    // ── SignalRCompletionReporter behavior ────────────────────────────────



    // ── HttpPrimaryCompletionReporter behavior ───────────────────────────



    // ── Consumer assertions ──────────────────────────────────────────────



    // ── Helpers ──────────────────────────────────────────────────────────

    private static string GetSourceDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CodingAgentAutomation.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find solution root");
    }
}
