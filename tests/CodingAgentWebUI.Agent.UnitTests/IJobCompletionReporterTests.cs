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

    [Fact]
    public void IJobCompletionReporter_HasReportCompletionAsyncMethod()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "IJobCompletionReporter.cs"));

        sourceCode.Should().Contain("Task ReportCompletionAsync",
            "IJobCompletionReporter must define ReportCompletionAsync");
    }

    [Fact]
    public void IJobCompletionReporter_AcceptsJobId()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "IJobCompletionReporter.cs"));

        sourceCode.Should().Contain("string jobId",
            "ReportCompletionAsync must accept a jobId parameter");
    }

    [Fact]
    public void IJobCompletionReporter_AcceptsJobCompletionPayload()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "IJobCompletionReporter.cs"));

        sourceCode.Should().Contain("JobCompletionPayload",
            "ReportCompletionAsync must accept a JobCompletionPayload parameter");
    }

    [Fact]
    public void IJobCompletionReporter_AcceptsCancellationToken()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "IJobCompletionReporter.cs"));

        sourceCode.Should().Contain("CancellationToken",
            "ReportCompletionAsync must accept a CancellationToken");
    }

    // ── Implementation existence ─────────────────────────────────────────

    [Fact]
    public void SignalRCompletionReporter_Exists()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "SignalRCompletionReporter.cs"));

        sourceCode.Should().Contain("class SignalRCompletionReporter",
            "SignalRCompletionReporter must exist for SignalR-mode completion reporting");
        sourceCode.Should().Contain("IJobCompletionReporter",
            "SignalRCompletionReporter must implement IJobCompletionReporter");
    }

    [Fact]
    public void HttpPrimaryCompletionReporter_Exists()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "HttpPrimaryCompletionReporter.cs"));

        sourceCode.Should().Contain("class HttpPrimaryCompletionReporter",
            "HttpPrimaryCompletionReporter must exist for K8s-mode completion reporting");
        sourceCode.Should().Contain("IJobCompletionReporter",
            "HttpPrimaryCompletionReporter must implement IJobCompletionReporter");
    }

    // ── Behavioral tests: mock completion reporter ───────────────────────

    [Fact]
    public async Task MockReporter_ReportCompletionAsync_CanBeInvoked()
    {
        var mock = new Mock<IJobCompletionReporter>();
        mock.Setup(x => x.ReportCompletionAsync(
                It.IsAny<string>(),
                It.IsAny<JobCompletionPayload>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow
        };

        await mock.Object.ReportCompletionAsync("job-123", payload, CancellationToken.None);

        mock.Verify(x => x.ReportCompletionAsync("job-123", payload, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task MockReporter_ReportCompletionAsync_PropagatesFailurePayload()
    {
        var mock = new Mock<IJobCompletionReporter>();
        mock.Setup(x => x.ReportCompletionAsync(
                It.IsAny<string>(),
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
            "job-fail",
            It.Is<JobCompletionPayload>(p => p.FinalStep == PipelineStep.Failed && p.FailureReason == "Quality gates failed"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── SignalRCompletionReporter behavior ────────────────────────────────

    [Fact]
    public void SignalRCompletionReporter_UsesResiliencePipeline()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "SignalRCompletionReporter.cs"));

        sourceCode.Should().Contain("ResiliencePipeline",
            "SignalRCompletionReporter must use Polly resilience for hub invocations");
    }

    [Fact]
    public void SignalRCompletionReporter_UsesCriticalMessageBuffer()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "SignalRCompletionReporter.cs"));

        sourceCode.Should().Contain("CriticalMessageBuffer",
            "SignalRCompletionReporter must buffer messages on failure for replay on reconnection");
    }

    // ── HttpPrimaryCompletionReporter behavior ───────────────────────────

    [Fact]
    public void HttpPrimaryCompletionReporter_PostsViaHttp()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "HttpPrimaryCompletionReporter.cs"));

        // Must use the lifecycle client for HTTP POST (primary/durable channel)
        sourceCode.Should().Contain("IWorkItemLifecycleClient",
            "HttpPrimaryCompletionReporter must use IWorkItemLifecycleClient for HTTP POST (primary channel)");
    }

    [Fact]
    public void HttpPrimaryCompletionReporter_AlsoReportsViaSignalR()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "HttpPrimaryCompletionReporter.cs"));

        // Must also report via SignalR (secondary/real-time channel)
        sourceCode.Should().Contain("ReportJobCompleted",
            "HttpPrimaryCompletionReporter must also report via SignalR as secondary channel");
    }

    // ── Consumer assertions ──────────────────────────────────────────────

    [Fact]
    public void SourceCode_AgentWorkerService_UsesIJobCompletionReporter()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "AgentWorkerService.cs"));

        sourceCode.Should().Contain("IJobCompletionReporter",
            "AgentWorkerService must use IJobCompletionReporter for unified completion reporting");
    }

    [Fact]
    public void SourceCode_WorkItemAgentService_UsesIJobCompletionReporter()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "WorkItemAgentService.cs"));

        sourceCode.Should().Contain("IJobCompletionReporter",
            "WorkItemAgentService must use IJobCompletionReporter for unified completion reporting");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string GetSourceDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CodingAgentAutomation.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find solution root");
    }
}
