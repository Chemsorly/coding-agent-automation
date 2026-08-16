using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.Orchestration.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="OrchestratorRunServiceExtensions.PostDispatchTimingCorrection"/>.
/// Covers null-receiver, missing-run, and happy-path scenarios.
/// Uses a real <see cref="OrchestratorRunService"/> instance (not a mock) so that
/// <see cref="PipelineRun.StartedAtOffset"/> assertions verify actual side-effects.
/// </summary>
public class OrchestratorRunServiceExtensionsTests
{
    // ── null runService ─────────────────────────────────────────────────────

    [Fact]
    public void PostDispatchTimingCorrection_NullRunService_DoesNotThrow()
    {
        IOrchestratorRunService? runService = null;
        var dispatchedAt = DateTimeOffset.UtcNow;

        var act = () => runService.PostDispatchTimingCorrection("any-run-id", dispatchedAt);

        act.Should().NotThrow("a null runService must be handled gracefully — K8s path omits RunService in test setups");
    }

    // ── run not in memory ───────────────────────────────────────────────────

    [Fact]
    public void PostDispatchTimingCorrection_RunNotInMemory_DoesNotThrow()
    {
        var runService = new OrchestratorRunService(new Mock<Serilog.ILogger>().Object);
        var dispatchedAt = DateTimeOffset.UtcNow;

        // No run registered — GetRun will return null
        var act = () => runService.PostDispatchTimingCorrection("non-existent-run-id", dispatchedAt);

        act.Should().NotThrow("a missing in-memory run must be handled gracefully");
    }

    // ── happy path ──────────────────────────────────────────────────────────

    [Fact]
    public void PostDispatchTimingCorrection_RunExists_SetsStartedAtToDispatchTime()
    {
        // Arrange: run with a stale StartedAt (simulating preparation/enqueue time hours ago)
        var runId = Guid.NewGuid().ToString();
        var enqueueTime = DateTimeOffset.UtcNow.AddHours(-4);
        var dispatchedAt = DateTimeOffset.UtcNow;

        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = "owner/repo#2065",
            IssueTitle = "BUG-14 timing test",
            IssueProviderConfigId = "provider-1",
            RepoProviderConfigId = "repo-1",
            StartedAt = enqueueTime
        });

        var runService = new OrchestratorRunService(new Mock<Serilog.ILogger>().Object);
        runService.AddRun(run);

        // Act
        runService.PostDispatchTimingCorrection(runId, dispatchedAt);

        // Assert: StartedAtOffset must be updated to dispatch time, not enqueue time
        var updatedRun = runService.GetRun(runId);
        updatedRun.Should().NotBeNull();
        updatedRun!.StartedAtOffset.Should().Be(dispatchedAt,
            "PostDispatchTimingCorrection must call ResetStartedAt with the provided dispatchedAt");
        updatedRun.StartedAtOffset.Should().BeAfter(enqueueTime.AddHours(3),
            "the original enqueue time (4h ago) must no longer be StartedAt");
    }
}
