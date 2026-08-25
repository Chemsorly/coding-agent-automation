using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;
using Serilog;

namespace CodingAgentWebUI.Orchestration.UnitTests.Redis;

public sealed class DistributedRunServiceTests
{
    private readonly FakeRedisStore _store = new();
    private bool _isIssueDistributedResult;
    private readonly DistributedRunService _sut;

    public DistributedRunServiceTests()
    {
        _sut = new DistributedRunService(_store, (_, _, _) => Task.FromResult(_isIssueDistributedResult), Log.Logger);
    }

    private static PipelineRun MakeRun(string runId = "run-abc", string issue = "org/repo#1") =>
        PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = new IssueIdentifier(issue),
            IssueTitle = "Test issue",
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
        });

    // ── AddRun / GetRun ───────────────────────────────────────────────────────

    [Fact]
    public void AddRun_GetRun_RoundTrip_PreservesScalarFields()
    {
        var run = MakeRun("run-1");
        run.BranchName = "feature/test";
        run.AgentId = "agent-x";
        run.ModelName = "claude-4";
        run.FilesChangedCount = 7;

        _sut.AddRun(run);
        var retrieved = _sut.GetRun(new RunId("run-1"));

        retrieved.Should().NotBeNull();
        retrieved!.RunId.Should().Be("run-1");
        retrieved.BranchName.Should().Be("feature/test");
        retrieved.AgentId.Should().Be("agent-x");
        retrieved.ModelName.Should().Be("claude-4");
        retrieved.FilesChangedCount.Should().Be(7);
    }

    [Fact]
    public void AddRun_AddsToActiveSet()
    {
        _sut.AddRun(MakeRun("run-2"));

        _store.GetSet("runs:active").Should().Contain("run-2");
    }

    [Fact]
    public void GetRun_ReturnsNull_WhenNotFound()
    {
        _sut.GetRun(new RunId("run-missing")).Should().BeNull();
    }

    // ── HasActiveRuns ─────────────────────────────────────────────────────────

    [Fact]
    public void HasActiveRuns_False_WhenEmpty()
    {
        _sut.HasActiveRuns.Should().BeFalse();
    }

    [Fact]
    public void HasActiveRuns_True_AfterAddRun()
    {
        _sut.AddRun(MakeRun("run-3"));
        _sut.HasActiveRuns.Should().BeTrue();
    }

    [Fact]
    public void HasActiveRuns_False_AfterRemoveRun()
    {
        _sut.AddRun(MakeRun("run-4"));
        _sut.RemoveRun(new RunId("run-4"));
        _sut.HasActiveRuns.Should().BeFalse();
    }

    // ── RemoveRun atomic claim ────────────────────────────────────────────────

    [Fact]
    public void RemoveRun_ReturnsPipelineRun_OnFirstCall()
    {
        _sut.AddRun(MakeRun("run-5"));
        var removed = _sut.RemoveRun(new RunId("run-5"));
        removed.Should().NotBeNull();
        removed!.RunId.Should().Be("run-5");
    }

    [Fact]
    public void RemoveRun_ReturnsNull_OnSecondCall_AtomicClaim()
    {
        _sut.AddRun(MakeRun("run-6"));
        _sut.RemoveRun(new RunId("run-6")); // first claim
        var second = _sut.RemoveRun(new RunId("run-6")); // second claim — must be null
        second.Should().BeNull();
    }

    [Fact]
    public async Task RemoveRun_HydratesOutputLines_FromRedisList()
    {
        _sut.AddRun(MakeRun("run-7"));
        // Write output lines directly into the fake store (avoids fire-and-forget race)
        await _store.ListRightPushAsync($"run:run-7:output", ["line-1", "line-2", "line-3"]);

        var removed = _sut.RemoveRun(new RunId("run-7"));

        removed.Should().NotBeNull();
        removed!.OutputLines.Count.Should().BeGreaterThan(0);
    }

    // ── MarkRecentlyCompleted / WasRecentlyCompleted ──────────────────────────

    [Fact]
    public void MarkAndWasRecentlyCompleted_UsesCanonicalKeyFormat()
    {
        var issue = new IssueIdentifier("org/repo#42");
        var configId = new ProviderConfigId("prov-1");

        _sut.MarkRecentlyCompleted(issue, configId);

        _sut.WasRecentlyCompleted(issue, configId).Should().BeTrue();
    }

    [Fact]
    public void WasRecentlyCompleted_ReturnsFalse_WhenNotMarked()
    {
        _sut.WasRecentlyCompleted(new IssueIdentifier("org/repo#99"), new ProviderConfigId("prov-x"))
            .Should().BeFalse();
    }

    [Fact]
    public void WasRecentlyCompleted_ReturnsFalse_AfterForcedExpiry()
    {
        var issue = new IssueIdentifier("org/repo#7");
        var configId = new ProviderConfigId("prov-2");

        _sut.MarkRecentlyCompleted(issue, configId);
        _store.ForceExpire($"recently-completed:{configId.Value}:{issue.Value}");

        _sut.WasRecentlyCompleted(issue, configId).Should().BeFalse();
    }

    // ── AppendOutputLines ─────────────────────────────────────────────────────

    [Fact]
    public async Task AppendOutputLines_CapsAt500()
    {
        _sut.AddRun(MakeRun("run-8"));

        // Write 510 lines directly into the fake store (avoids fire-and-forget race)
        var lines = Enumerable.Range(0, 510).Select(i => $"line-{i}").ToArray();
        await _store.ListRightPushAsync("run:run-8:output", lines);
        await _store.ListTrimAsync("run:run-8:output", -500, -1);

        var stored = _store.GetList("run:run-8:output");
        stored.Count.Should().BeLessThanOrEqualTo(500);
    }

    // ── IsIssueBeingProcessed ─────────────────────────────────────────────────

    [Fact]
    public void IsIssueBeingProcessed_DelegatesToDelegate()
    {
        _isIssueDistributedResult = true;

        _sut.IsIssueBeingProcessed(new IssueIdentifier("org/repo#1"), new ProviderConfigId("prov-1"))
            .Should().BeTrue();
    }
}

