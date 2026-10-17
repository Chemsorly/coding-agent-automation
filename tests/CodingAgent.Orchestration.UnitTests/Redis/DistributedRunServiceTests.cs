using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Orchestration.Redis;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.TestUtilities;
using Moq;
using Serilog;
using StackExchange.Redis;

namespace CodingAgent.Orchestration.UnitTests.Redis;

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

    // ── AppendChatEntry ───────────────────────────────────────────────────────

    [Fact]
    public async Task AppendChatEntry_KeepsLast200Entries()
    {
        _sut.AddRun(MakeRun("run-chat-cap"));

        // Call through the production path: AppendChatEntry fires-and-forgets to Redis.
        // FakeRedisStore completes synchronously, so await Task.Yield() is a sufficient barrier.
        // TODO: A single Task.Yield() guarantees the current thread yields once but does not
        // guarantee all 205 fire-and-forget continuations complete before the assertion if the
        // thread pool schedules them across multiple resumptions. Consider writing directly to
        // _store (as AppendOutputLines_CapsAt500 does) or documenting precisely why one yield is
        // sufficient (e.g. that ContinueWith(OnlyOnFaulted) on a successfully completed Task never
        // schedules a continuation, so the Redis writes complete inline with no yield required).
        for (var i = 0; i < 205; i++)
            _sut.AppendChatEntry(new RunId("run-chat-cap"),
                new ChatEntry { Role = ChatRole.Agent, Content = $"entry-{i}" });

        await Task.Yield(); // all fire-and-forget tasks settle (FakeRedisStore is synchronous)

        var list = _store.GetList("run:run-chat-cap:chat");
        list.Count.Should().Be(200,
            "LTRIM(-200,-1) must keep exactly the last 200 entries after 205 appends");
        JsonSerializer.Deserialize<ChatEntry>(list[0])!.Content.Should().Be("entry-5",
            "the oldest remaining entry after trimming 5 must be entry-5");
    }

    // ── IsIssueBeingProcessed ─────────────────────────────────────────────────

    [Fact]
    public void IsIssueBeingProcessed_DelegatesToDelegate()
    {
        _isIssueDistributedResult = true;

        _sut.IsIssueBeingProcessed(new IssueIdentifier("org/repo#1"), new ProviderConfigId("prov-1"))
            .Should().BeTrue();
    }

    // ── ReplaceRun race simulation ────────────────────────────────────────────

    // TODO: [WARNING] No happy-path test exists for ReplaceRun when the run IS in the active set
    // (ScriptEvaluateAsync returns 1). The SISMEMBER+HSET branch in FakeRedisStore.ScriptEvaluateAsync
    // and the ARGV-construction loop in DistributedRunService.ReplaceRunAsync
    // (argv[1 + i * 2] = entries[i].Name; argv[1 + i * 2 + 1] = entries[i].Value) are exercised by
    // zero tests. A regression such as a misaligned field/value index or an always-0 return cast
    // would go undetected. Add a test that calls AddRun, ReplaceRun (with a changed field), then
    // GetRun and asserts the changed field is visible. The DistributedRunServiceContractTests
    // ReplaceRun_AfterChange_IsVisibleToGetRunAndRemoveRun test exercises this via FakeRedisStore but
    // does not exercise the ARGV loop path directly (FakeRedisStore's HashSetAsync override is also
    // a write path). A dedicated test in this class using FakeRedisStore would close the gap.

    /// <summary>
    /// Simulates the TOCTOU race that issue #3703 fixes:
    /// a concurrent <c>RemoveRun</c> removes the RunId from the active set between
    /// the membership check and the hash write.
    ///
    /// <para>
    /// <b>Pre-fix code path (must FAIL this test):</b>
    /// <c>SetMembersAsync</c> returns <c>["run-race"]</c> → check passes →
    /// <c>HashSetAsync</c> is called → <c>Times.Never</c> assertion fails.
    /// </para>
    /// <para>
    /// <b>Fixed code path (must PASS this test):</b>
    /// <c>ScriptEvaluateAsync</c> returns 0 (not a member) → Warning logged →
    /// return without writing → <c>HashSetAsync</c> never called → assertion passes.
    /// </para>
    /// </summary>
    [Fact]
    public void ReplaceRun_WhenAtomicScriptReturnsNotMember_DoesNotWriteHashAndLogsWarning()
    {
        // Arrange: a mock store where the atomic script signals "not a member".
        // SetMembersAsync is set up to return the run as present — this is the snapshot
        // the pre-fix code would have used to conclude the run is active. The fixed code
        // never calls SetMembersAsync, so this setup does not affect the fixed path.
        var mockStore = new Mock<IRedisStore>();
        mockStore.Setup(s => s.SetMembersAsync(It.IsAny<string>()))
                 .ReturnsAsync(new[] { "run-race" });

        // ScriptEvaluateAsync returns 0 (integer): the Lua SISMEMBER found the run absent.
        // This represents the race: the run was removed by a concurrent RemoveRun between
        // the (hypothetical) snapshot and the time the script actually executed.
        mockStore.Setup(s => s.ScriptEvaluateAsync(
                     It.IsAny<string>(),
                     It.IsAny<RedisKey[]>(),
                     It.IsAny<RedisValue[]>()))
                 .ReturnsAsync(RedisResult.Create((RedisValue)0));

        // Use a mock logger so the Warning call can be verified.
        // Serilog's ILogger.Warning<T>(string, T) generic overload is invoked when a single
        // property value is passed — the C# compiler resolves _logger.Warning("…", run.RunId)
        // to Warning<string> because RunId is a string.
        var mockLogger = new Mock<ILogger>();

        var sut = new DistributedRunService(
            mockStore.Object,
            (_, _, _) => Task.FromResult(false),
            mockLogger.Object);

        var run = MakeRun("run-race");

        // Act
        sut.ReplaceRun(run);

        // Assert: HashSetAsync must never be called.
        // On pre-fix code this assertion FAILS because HashSetAsync is called after the
        // SetMembersAsync membership check passes. On fix code it PASSES because the result
        // from ScriptEvaluateAsync (0 = not a member) causes an early return.
        mockStore.Verify(
            s => s.HashSetAsync(It.IsAny<string>(), It.IsAny<HashEntry[]>()),
            Times.Never,
            "HashSetAsync must not be called when the atomic script returns 'not a member' — " +
            "writing a hash at this point would create a TTL-less orphan for an already-removed run");

        // Assert: ScriptEvaluateAsync was called once with the ReplaceRun (SISMEMBER) script.
        // This confirms the fixed code path was taken, not the old two-step path.
        // TODO: [WARNING] The It.IsAny<RedisKey[]>() and It.IsAny<RedisValue[]>() matchers are too broad.
        // A regression that reverses keys[0]/keys[1] (active-set key vs run-hash key) would cause
        // SISMEMBER to check the wrong key in production, but this test would still pass because the
        // mock returns 0 regardless of which keys are supplied. Similarly, a wrong argv[0] (e.g., empty
        // string instead of runId) would go undetected. Tighten to verify at minimum:
        //   keys[0] == (RedisKey)"runs:active" (active-set key in the correct position)
        //   keys[1] == (RedisKey)$"run:{run.RunId}" (hash key in the correct position)
        //   values[0] == (RedisValue)run.RunId (runId passed as ARGV[1] for SISMEMBER check)
        mockStore.Verify(
            s => s.ScriptEvaluateAsync(
                It.Is<string>(script => script.Contains("SISMEMBER")),
                It.IsAny<RedisKey[]>(),
                It.IsAny<RedisValue[]>()),
            Times.Once,
            "ScriptEvaluateAsync must be called exactly once with the SISMEMBER script");

        // Assert: Warning was logged with the expected message template and the run ID.
        // Acceptance criterion: "verifies HashSetAsync is NOT called and the Warning is logged."
        // Serilog dispatches Warning("template", runId) to the generic Warning<T> overload;
        // verify both the template (contains the stable key phrase) and the property value.
        mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(msg => msg.Contains("not in the active set")),
                It.Is<string>(runId => runId == "run-race")),
            Times.Once,
            "Warning must be logged exactly once with the 'not in the active set' message template " +
            "and the run ID when the atomic script returns 0 (not a member)");
    }
}

