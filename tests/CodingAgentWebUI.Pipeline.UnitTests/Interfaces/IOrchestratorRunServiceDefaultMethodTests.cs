using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Interfaces;

/// <summary>
/// Tests for <see cref="IOrchestratorRunService.GetActiveRunBranchesAsync"/> default interface method.
/// The DIM derives branch names from <see cref="IOrchestratorRunService.GetActiveRuns"/> — this test
/// exercises it via a minimal stub that only implements the abstract members, ensuring the DIM body
/// in <c>IOrchestratorRunService.cs</c> is covered by the Pipeline.UnitTests coverage report.
/// </summary>
public class IOrchestratorRunServiceDefaultMethodTests
{
    /// <summary>
    /// Minimal stub that implements the abstract API surface of IOrchestratorRunService
    /// and delegates GetActiveRuns() to a caller-supplied list. All other members are
    /// no-ops. GetActiveRunBranchesAsync is intentionally NOT overridden so the DIM runs.
    /// </summary>
    private sealed class StubRunService(IReadOnlyList<PipelineRun> activeRuns) : IOrchestratorRunService
    {
        public bool HasActiveRuns => activeRuns.Count > 0;
        public int ActiveRunCount => activeRuns.Count;
        public IReadOnlyList<PipelineRun> GetActiveRuns() => activeRuns;
        public PipelineRun? GetRun(RunId runId) => null;
        public void AddRun(PipelineRun run) { }
        public PipelineRun? RemoveRun(RunId runId) => null;
        public void ReplaceRun(PipelineRun run) { }
        public bool IsIssueBeingProcessed(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId) => false;
        public OutputRingBuffer GetOutputBuffer(RunId runId) => new(1);
        public void AppendOutputLines(RunId runId, IReadOnlyList<string> lines) { }
        public bool WasRecentlyCompleted(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId) => false;
        public void MarkRecentlyCompleted(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId) { }
    }

    private static PipelineRun MakeRun(string runId, string? branchName) => new()
    {
        RunId = runId,
        IssueIdentifier = "org/repo#1",
        IssueTitle = "Test",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        BranchName = branchName
    };

    [Fact]
    public async Task GetActiveRunBranchesAsync_DefaultImpl_ReturnsBranchNamesFromGetActiveRuns()
    {
        // Arrange: two runs — one with a branch, one without (null).
        IOrchestratorRunService svc = new StubRunService(
        [
            MakeRun("run-1", "feature/auto-1-task-a"),
            MakeRun("run-2", null) // branch not yet set — must be excluded
        ]);

        // Act: call through the interface so the DIM body executes
        var branches = await svc.GetActiveRunBranchesAsync(CancellationToken.None);

        // Assert
        branches.Should().ContainSingle("only the run with a non-null BranchName is included");
        branches.Should().Contain("feature/auto-1-task-a");
    }

    [Fact]
    public async Task GetActiveRunBranchesAsync_DefaultImpl_IsCaseInsensitive()
    {
        // Arrange
        IOrchestratorRunService svc = new StubRunService(
        [
            MakeRun("run-ci", "Feature/Auto-99-MyFeature")
        ]);

        // Act
        var branches = await svc.GetActiveRunBranchesAsync(CancellationToken.None);

        // Assert: the HashSet is OrdinalIgnoreCase
        branches.Contains("feature/auto-99-myfeature").Should().BeTrue(
            "default implementation uses case-insensitive comparison");
        branches.Contains("FEATURE/AUTO-99-MYFEATURE").Should().BeTrue(
            "default implementation uses case-insensitive comparison");
    }

    [Fact]
    public async Task GetActiveRunBranchesAsync_DefaultImpl_EmptyWhenNoActiveRuns()
    {
        // Arrange
        IOrchestratorRunService svc = new StubRunService([]);

        // Act
        var branches = await svc.GetActiveRunBranchesAsync(CancellationToken.None);

        // Assert
        branches.Should().BeEmpty("no active runs means no active branches");
    }
}
