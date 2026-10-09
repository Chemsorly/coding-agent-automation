using AwesomeAssertions;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Redis;
using CodingAgent.Pipeline.Models;
using StackExchange.Redis;
using Testcontainers.Redis;

namespace CodingAgent.Infrastructure.IntegrationTests.Redis;

/// <summary>
/// Runs the Lua script inside <c>DistributedRunService.RemoveRunScript</c> against a real Redis
/// instance via <see cref="RedisStore"/>. All unit tests for <see cref="DistributedRunService"/>
/// use <c>FakeRedisStore</c>, which re-implements the Lua script in C#; deleting the nil-guard
/// from the script passes all unit tests without detection.
///
/// These tests require Docker and are excluded from the standard <c>build-and-test</c> job
/// (filter: <c>Category!=Integration</c>). They run in the <c>migration-integrity</c> CI job
/// (filter: <c>Category=Integration</c>) which runs directly on the Docker-enabled runner.
/// When Docker is unavailable (local Windows runs, the agent quality-gate sandbox), each test
/// returns early without failing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DistributedRunServiceRedisTests : IClassFixture<RedisContainerFixture>
{
    /// <summary>
    /// Number of rounds for the concurrent-claim race test.
    /// Must be exactly 20 per acceptance criteria.
    /// </summary>
    private const int RaceRounds = 20;

    private readonly RedisContainerFixture _fixture;

    public DistributedRunServiceRedisTests(RedisContainerFixture fixture)
    {
        _fixture = fixture;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Returns a unique run ID for each test invocation, preventing key collisions on the shared container.</summary>
    private static string NewRunId() => $"run-{Guid.NewGuid():N}";

    private static PipelineRun MakeRun(string runId) =>
        PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = new IssueIdentifier("org/repo#1"),
            IssueTitle = "Test issue",
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
        });

    // ── Tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Verifies that the Lua nil-guard (<c>if removed == 0 then return nil end</c>) is present
    /// and effective: a second <c>RemoveRun</c> on the same run must return null.
    /// Fails under the guard-deletion mutant because the second <c>SREM</c> returns 0 but the
    /// script proceeds, <c>HGETALL</c> returns the hash (TTL is still in the future from the
    /// first call's <c>EXPIREAT</c>), and a non-null run is returned.
    /// </summary>
    [Fact]
    public async Task RemoveRun_CalledTwiceForSameRun_SecondCallReturnsNull()
    {
        // Skip gracefully when Docker is unavailable; CI runs these in the Docker-enabled integration job.
        if (_fixture.IsDockerUnavailable) return;
        _fixture.InitializationException.Should().BeNull("the Redis container must start and accept a connection");

        var runId = NewRunId();
        var svc = _fixture.CreateService();

        await Task.Run(() => svc.AddRun(MakeRun(runId)));

        var first = await Task.Run(() => svc.RemoveRun(new RunId(runId)));
        var second = await Task.Run(() => svc.RemoveRun(new RunId(runId)));

        first.Should().NotBeNull();
        first!.RunId.Should().Be(runId);
        second.Should().BeNull("the script returns nil when SREM removed nothing, so a finished run can be claimed only once");
    }

    /// <summary>
    /// Verifies the atomicity guarantee of the Lua script under concurrent access from two
    /// separate <see cref="DistributedRunService"/> instances (simulating two API replicas).
    /// Exactly one of the two concurrent <c>RemoveRun</c> calls must return a non-null run in
    /// every round.
    /// </summary>
    [Fact]
    public async Task RemoveRun_TwoServicesRaceOnSameRun_ExactlyOneClaimsRun()
    {
        // Skip gracefully when Docker is unavailable; CI runs these in the Docker-enabled integration job.
        if (_fixture.IsDockerUnavailable) return;
        _fixture.InitializationException.Should().BeNull("the Redis container must start and accept a connection");

        var replicaA = _fixture.CreateService();
        var replicaB = _fixture.CreateService();

        for (var round = 1; round <= RaceRounds; round++)
        {
            var runId = NewRunId();
            await Task.Run(() => replicaA.AddRun(MakeRun(runId)));

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            // TODO: [WARNING] The `async` modifier is superfluous here because RemoveRun is synchronous
            // (PipelineRun?); the async state machine adds an allocation per call without benefit.
            // The `async/await start.Task` pause still achieves the intended simultaneous dispatch,
            // so this is behaviorally correct. Consider removing `async` and awaiting start.Task
            // before calling Task.Run if the allocation matters in future.
            var claimA = Task.Run(async () => { await start.Task; return replicaA.RemoveRun(new RunId(runId)); });
            var claimB = Task.Run(async () => { await start.Task; return replicaB.RemoveRun(new RunId(runId)); });
            start.SetResult();

            var claims = await Task.WhenAll(claimA, claimB);
            claims.Count(c => c is not null).Should().Be(1, $"round {round}: exactly one replica may claim a finished run");
        }
    }

    /// <summary>
    /// Verifies that <c>RemoveRun</c>:
    /// <list type="bullet">
    ///   <item>Returns the correct hash fields (kills the <c>HGETALL KEYS[3]</c> mutant via <c>BranchName</c>)</item>
    ///   <item>Hydrates <see cref="PipelineRun.OutputLines"/> from the Redis List</item>
    ///   <item>Removes the run from the <c>runs:active</c> set</item>
    ///   <item>Sets a positive TTL ≤ 5 minutes on both <c>run:{id}</c> and <c>run:{id}:output</c></item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task RemoveRun_FirstCall_ReturnsStoredRunAndOutputAndExpiresKeys()
    {
        // Skip gracefully when Docker is unavailable; CI runs these in the Docker-enabled integration job.
        if (_fixture.IsDockerUnavailable) return;
        _fixture.InitializationException.Should().BeNull("the Redis container must start and accept a connection");

        var db = _fixture.Multiplexer!.GetDatabase();
        var runId = NewRunId();
        var run = MakeRun(runId);
        run.BranchName = "feature/redis-claim";

        var svc = _fixture.CreateService();
        await Task.Run(() => svc.AddRun(run));

        // Write output directly to Redis to avoid the fire-and-forget race in AppendOutputLines.
        // This is the same technique used in DistributedRunServiceTests.RemoveRun_HydratesOutputLines_FromRedisList.
        await db.ListRightPushAsync($"run:{runId}:output", new RedisValue[] { "line-1", "line-2" });

        var removed = await Task.Run(() => svc.RemoveRun(new RunId(runId)));

        // Hash content
        removed.Should().NotBeNull();
        removed!.BranchName.Should().Be("feature/redis-claim", "HGETALL must target KEYS[2] (the run hash), not KEYS[3] (the output list)");
        removed.OutputLines.Should().Equal("line-1", "line-2");

        // Active set cleanup
        (await db.SetContainsAsync("runs:active", runId)).Should().BeFalse("SREM must have removed the run id from the active set");

        // TTL on run hash
        var ttlRun = await db.KeyTimeToLiveAsync($"run:{runId}");
        ttlRun.Should().NotBeNull("EXPIREAT must have set a TTL on the run hash");
        ttlRun!.Value.Should().BePositive("the TTL must be in the future");
        ttlRun.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(5), "RunPostCompletionTtl is 5 minutes");

        // TTL on output list
        var ttlOutput = await db.KeyTimeToLiveAsync($"run:{runId}:output");
        ttlOutput.Should().NotBeNull("EXPIREAT must have set a TTL on the output list");
        ttlOutput!.Value.Should().BePositive("the TTL must be in the future");
        ttlOutput.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(5), "RunPostCompletionTtl is 5 minutes");
    }
}

/// <summary>
/// Starts one Redis container per test class and exposes a multiplexer and a factory method for
/// creating <see cref="DistributedRunService"/> instances wired to that container.
/// </summary>
/// <remarks>
/// The container is built inside <see cref="InitializeAsync"/>, never in a field initializer.
/// Building in a field initializer would throw in Docker-less environments before xUnit's trait
/// filtering can exclude these Integration tests, failing the test run with a construction error
/// rather than a graceful skip.
/// <para>
/// Note: <see cref="RedisBuilder(string)"/> is the recommended (non-deprecated) form.
/// Do not use the parameterless <c>new RedisBuilder()</c> — it is marked <c>[Obsolete]</c>
/// in Testcontainers 4.x. The pattern differs from the PostgreSQL fixtures in this project,
/// which use <c>new PostgreSqlBuilder().WithImage(...)</c>.
/// </para>
/// </remarks>
public sealed class RedisContainerFixture : IAsyncLifetime
{
    // Null until InitializeAsync runs.
    private RedisContainer? _container;

    public ConnectionMultiplexer? Multiplexer { get; private set; }

    /// <summary>Set when the container fails to start or the multiplexer connection fails.</summary>
    public Exception? InitializationException { get; private set; }

    /// <summary>True when the container could not be built, typically because Docker is unavailable.</summary>
    // TODO: [WARNING] This property is only true when RedisBuilder.Build() itself throws (before _container
    // is assigned). If Build() succeeds but StartAsync() or ConnectAsync() fails (e.g., Docker daemon
    // present but image pull fails), _container is non-null so IsDockerUnavailable is false and tests
    // proceed to the InitializationException.Should().BeNull() assertion. The property's doc comment
    // is therefore misleading for the StartAsync failure case; consider renaming to IsContainerBuildFailed
    // or expanding the doc to clarify the semantics.
    public bool IsDockerUnavailable => _container is null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new RedisBuilder("redis:7-alpine").Build();
            await _container.StartAsync();
            Multiplexer = await ConnectionMultiplexer.ConnectAsync(_container.GetConnectionString());
        }
        catch (Exception ex)
        {
            InitializationException = ex;
        }
    }

    public async Task DisposeAsync()
    {
        // TODO: [WARNING] ConnectionMultiplexer implements IAsyncDisposable; calling synchronous Dispose()
        // here blocks the calling thread and bypasses the async shutdown path. In a test fixture this is
        // low-risk, but consider: if (Multiplexer is not null) await Multiplexer.DisposeAsync();
        Multiplexer?.Dispose();
        if (_container is not null)
            await _container.DisposeAsync();
    }

    /// <summary>
    /// Creates a new <see cref="DistributedRunService"/> instance backed by the live Redis container.
    /// Each call returns a distinct instance — use two instances in the race test to simulate two replicas.
    /// </summary>
    public DistributedRunService CreateService() => new(
        new RedisStore(Multiplexer!.GetDatabase()),
        (_, _, _) => Task.FromResult(false),
        Serilog.Core.Logger.None);
}
