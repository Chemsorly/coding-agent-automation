using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;
using Serilog;
using Xunit;

namespace CodingAgent.Orchestration.UnitTests;

/// <summary>
/// Shared contract tests that verify the behavioral rules documented in the role interfaces.
/// Both <see cref="OrchestratorRunService"/> and <see cref="DistributedRunService"/> must pass.
/// </summary>
public abstract class RunServiceContractTests
{
    protected abstract IOrchestratorRunService CreateService();

    protected static PipelineRun MakeRun(string runId = "run-1", string issue = "org/repo#1", string? branch = null)
    {
        var run = new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = issue,
            IssueTitle = "Test issue",
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
        };
        if (branch != null) run.BranchName = branch;
        return run;
    }

    // ── AddRun upsert semantics ───────────────────────────────────────────────

    /// <summary>
    /// Acceptance criterion: duplicate AddRun is an upsert — the second call replaces the stored
    /// run, the run remains active, and the backlog is preserved.
    /// </summary>
    [Fact]
    public async Task AddRun_SameRunIdTwice_SecondAddReplacesStoredRunAndKeepsBacklog()
    {
        var svc = CreateService();
        var r1 = MakeRun("run-dup", "org/repo#1");
        svc.AddRun(r1);
        svc.AppendOutputLines(r1.RunId, ["a"]);
        // TODO: await Task.Yield() is used here to allow DistributedRunService's fire-and-forget
        // Redis write (AppendOutputToRedisAsync) to complete before asserting the backlog. This works
        // only because FakeRedisStore completes all tasks synchronously (Task.FromResult), so the
        // continuation runs before the next statement. If FakeRedisStore is ever replaced with a
        // genuinely async store, this yield is insufficient and the backlog assertion may race.
        await Task.Yield(); // allow fire-and-forget paths to settle

        var r2 = MakeRun("run-dup", "org/repo#2");
        svc.AddRun(r2);
        await Task.Yield();

        // One active run
        svc.ActiveRunCount.Should().Be(1, "duplicate AddRun keeps exactly one active run");

        // Second run's issue is stored
        var retrieved = svc.GetRun(new RunId("run-dup"));
        retrieved.Should().NotBeNull();
        retrieved!.IssueIdentifier.Value.Should().Be("org/repo#2",
            "the second AddRun must replace the stored run");

        // Backlog is preserved
        var backlog = await svc.GetOutputBacklogAsync(new RunId("run-dup"));
        backlog.Should().ContainSingle(l => l == "a",
            "the output backlog must be preserved across an AddRun upsert");
    }

    // ── ReplaceRun no-op semantics ────────────────────────────────────────────

    /// <summary>
    /// ReplaceRun on a RunId that was never added must not create a new active run.
    /// </summary>
    [Fact]
    public void ReplaceRun_UnknownRunId_DoesNotAddRun()
    {
        var svc = CreateService();
        var run = MakeRun("run-unknown");
        svc.ReplaceRun(run);

        svc.GetActiveRuns().Should().NotContain(r => r.RunId == "run-unknown",
            "ReplaceRun on an unknown RunId must not add the run to the active set");
        svc.GetRun(new RunId("run-unknown")).Should().BeNull(
            "GetRun must return null for a RunId that was never added");
    }

    /// <summary>
    /// ReplaceRun after RemoveRun must not reactivate the removed run.
    /// </summary>
    [Fact]
    public void ReplaceRun_AfterRemoveRun_DoesNotReactivateRun()
    {
        var svc = CreateService();
        var run = MakeRun("run-removed");
        svc.AddRun(run);
        svc.RemoveRun(new RunId("run-removed"));

        // ReplaceRun must not resurrect the run
        svc.ReplaceRun(run);

        svc.GetActiveRuns().Should().NotContain(r => r.RunId == "run-removed",
            "ReplaceRun after RemoveRun must not reactivate the run");
    }

    /// <summary>
    /// Changes made via ReplaceRun are visible to subsequent GetRun and RemoveRun calls.
    /// </summary>
    [Fact]
    public void ReplaceRun_AfterChange_IsVisibleToGetRunAndRemoveRun()
    {
        var svc = CreateService();
        var run = MakeRun("run-replace");
        svc.AddRun(run);

        // TODO: This test mutates the same object reference (run.BranchName = "feature/x") and then
        // calls ReplaceRun(run). For OrchestratorRunService (in-memory), the stored entry IS the
        // same reference, so GetRun would return the updated BranchName even if ReplaceRun were a
        // no-op. The test does not fully verify that ReplaceRun is required for visibility in the
        // in-memory path. To make it a true specification test, pass a new PipelineRun instance with
        // the updated field to ReplaceRun instead of mutating the original reference.
        run.BranchName = "feature/x";
        svc.ReplaceRun(run);

        var fromGet = svc.GetRun(new RunId("run-replace"));
        fromGet.Should().NotBeNull();
        fromGet!.BranchName.Should().Be("feature/x",
            "GetRun must return the updated BranchName after ReplaceRun");

        var fromRemove = svc.RemoveRun(new RunId("run-replace"));
        fromRemove.Should().NotBeNull();
        fromRemove!.BranchName.Should().Be("feature/x",
            "RemoveRun must return the run with the updated BranchName after ReplaceRun");
    }

    // ── RemoveRun ─────────────────────────────────────────────────────────────

    [Fact]
    public void RemoveRun_ReturnsRunAndRemovesItFromActiveRuns()
    {
        var svc = CreateService();
        var run = MakeRun("run-rem");
        svc.AddRun(run);

        var removed = svc.RemoveRun(new RunId("run-rem"));

        removed.Should().NotBeNull("RemoveRun must return the removed run");
        removed!.RunId.Should().Be("run-rem");
        removed.IssueIdentifier.Value.Should().Be("org/repo#1");

        svc.GetActiveRuns().Should().NotContain(r => r.RunId == "run-rem",
            "after RemoveRun the run must not appear in GetActiveRuns");
    }

    // ── Output backlog ────────────────────────────────────────────────────────

    /// <summary>
    /// AppendOutputLines is the only write path. GetOutputBacklogAsync returns lines in order.
    /// An unknown RunId returns an empty list.
    /// </summary>
    [Fact]
    public async Task AppendOutputLines_ThenGetOutputBacklogAsync_ReturnsLinesInOrder()
    {
        var svc = CreateService();
        var run = MakeRun("run-out");
        svc.AddRun(run);

        svc.AppendOutputLines(new RunId("run-out"), ["l1", "l2"]);
        await Task.Yield(); // allow fire-and-forget paths to settle
        svc.AppendOutputLines(new RunId("run-out"), ["l3"]);
        await Task.Yield();

        var backlog = await svc.GetOutputBacklogAsync(new RunId("run-out"));
        backlog.Should().ContainInOrder("l1", "l2", "l3");
        backlog.Should().HaveCount(3, "all three appended lines must be in the backlog");

        // Unknown run gives empty list
        var empty = await svc.GetOutputBacklogAsync(new RunId("run-nobody"));
        empty.Should().BeEmpty("GetOutputBacklogAsync must return empty for an unknown RunId");
    }

    // ── IsIssueBeingProcessed ─────────────────────────────────────────────────

    /// <summary>
    /// IsIssueBeingProcessed throws ArgumentException for an empty identifier in all implementations.
    /// </summary>
    [Fact]
    public void IsIssueBeingProcessed_EmptyIdentifier_ThrowsArgumentException()
    {
        var svc = CreateService();
        var act = () => svc.IsIssueBeingProcessed(
            new IssueIdentifier(""),
            new ProviderConfigId("prov-1"));

        act.Should().Throw<ArgumentException>(
            "IsIssueBeingProcessed must throw ArgumentException for an empty identifier");
    }

    // ── GetActiveRunBranchesAsync ─────────────────────────────────────────────

    /// <summary>
    /// Branch names are case-insensitive. Runs with null BranchName are excluded.
    /// </summary>
    [Fact]
    public async Task GetActiveRunBranchesAsync_ReturnsBranchesOfActiveRunsIgnoringCase()
    {
        var svc = CreateService();
        svc.AddRun(MakeRun("run-b1", branch: "Feature/A"));
        svc.AddRun(MakeRun("run-b2", branch: null));

        var branches = await svc.GetActiveRunBranchesAsync();

        branches.Should().ContainSingle("only the run with a non-null BranchName is included");
        branches.Contains("feature/a").Should().BeTrue(
            "GetActiveRunBranchesAsync must use case-insensitive comparison");
        branches.Contains("FEATURE/A").Should().BeTrue(
            "GetActiveRunBranchesAsync must use OrdinalIgnoreCase");
    }
}

/// <summary>
/// Contract tests for the in-memory implementation.
/// </summary>
public sealed class InMemoryRunServiceContractTests : RunServiceContractTests
{
    protected override IOrchestratorRunService CreateService()
        => new OrchestratorRunService(Log.Logger);
}

/// <summary>
/// Contract tests for the Redis-backed distributed implementation using <see cref="FakeRedisStore"/>.
/// </summary>
/// <remarks>
/// These tests rely on <see cref="FakeRedisStore"/> completing all operations synchronously
/// (returning <c>Task.FromResult</c>). This means fire-and-forget continuations (e.g.
/// <c>AppendOutputToRedisAsync</c>) complete before the next statement executes, making
/// <c>await Task.Yield()</c> a sufficient synchronization point.
/// TODO: If FakeRedisStore is ever replaced with a genuinely asynchronous store, the
/// Task.Yield() calls in these tests will no longer be sufficient synchronization barriers
/// and timing-sensitive tests (e.g. AddRun_SameRunIdTwice_SecondAddReplacesStoredRunAndKeepsBacklog)
/// will need a proper awaitable write path.
/// </remarks>
public sealed class DistributedRunServiceContractTests : RunServiceContractTests
{
    protected override IOrchestratorRunService CreateService()
        => new DistributedRunService(new FakeRedisStore(), (_, _, _) => Task.FromResult(false), Log.Logger);
}
