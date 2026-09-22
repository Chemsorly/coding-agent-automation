using AwesomeAssertions;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Redis;
using Moq;
using Serilog;
using StackExchange.Redis;

namespace CodingAgent.Web.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="ChatHeartbeatTracker"/>.
/// </summary>
public class ChatHeartbeatTrackerTests
{
    private const string TestAgentId = "caa-chat-abc12345";
    private const string TestJobName = "caa-chat-abc12345";

    private static DispatchServiceOptions CreateOptions(int idleTimeoutSeconds = 90) => new()
    {
        Namespace = "coding-agent",
        KiroPvcPool = ["pvc-0"],
        OrchestratorUrl = "http://orchestrator:8080",
        AgentApiKeySecretName = "caa-secret",
        AgentServiceAccountName = "caa-agent",
        ChatIdleTimeoutSeconds = idleTimeoutSeconds
    };

    private static ChatHeartbeatTracker CreateTracker(
        IRedisStore? redis = null,
        DispatchServiceOptions? options = null)
    {
        return new ChatHeartbeatTracker(
            redis ?? new CodingAgent.Web.TestUtilities.FakeRedisStore(),
            options ?? CreateOptions(),
            Mock.Of<ILogger>());
    }

    // ─── WriteRedisHeartbeatAsync ─────────────────────────────────────────────

    [Fact]
    public async Task WriteRedisHeartbeat_ReturnedTask_CompletesSuccessfullyOnHappyPath()
    {
        var tracker = CreateTracker();

        var task = tracker.WriteRedisHeartbeatAsync(TestAgentId);
        await task;

        // TODO [WARNING]: The IsCompletedSuccessfully assertion below is redundant — if the task
        // were faulted or canceled, the `await task` above would already throw and fail the test
        // before reaching this line. The assertion can never add a failure the `await` wouldn't
        // already produce. Consider asserting task.IsCompletedSuccessfully *before* awaiting
        // (relying on the synchronous-completion of FakeRedisStore), or simply drop the assertion
        // and let the `await` serve as the sole regression detector.
        // See review finding: TestQualityReviewer WARNING @ ChatHeartbeatTrackerTests.cs:44.
        task.IsCompletedSuccessfully.Should().BeTrue(
            "WriteRedisHeartbeatAsync must return a task in RanToCompletion state on Redis success, " +
            "not a Canceled task from ContinueWith(OnlyOnFaulted)");
    }

    [Fact]
    public async Task WriteRedisHeartbeat_CallsSetAsyncWithCorrectKeyAndTtl()
    {
        var options = CreateOptions(idleTimeoutSeconds: 60);
        var fakeRedis = new CodingAgent.Web.TestUtilities.FakeRedisStore();
        var tracker = CreateTracker(fakeRedis, options);

        var beforeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await tracker.WriteRedisHeartbeatAsync(TestAgentId);

        var key = $"chat:heartbeat:{TestAgentId}";
        var raw = await fakeRedis.GetAsync(key);

        raw.Should().NotBeNull("heartbeat key must be written to Redis");
        long.TryParse(raw, out var ms).Should().BeTrue("value must be a Unix-ms timestamp");
        ms.Should().BeGreaterThanOrEqualTo(beforeMs, "timestamp must not be in the past");

        // TTL assertion: 2 × idleTimeoutSeconds = 2 × 60 = 120s from now
        var expiry = fakeRedis.GetExpiry(key);
        expiry.Should().NotBeNull("SetAsync must store a TTL expiry for the heartbeat key");
        // TODO [WARNING]: The BeCloseTo reference point (DateTimeOffset.UtcNow + 120s) is evaluated
        // *after* the await returns, while the expiry was computed inside WriteRedisHeartbeatAsync
        // using a separate UtcNow call. On a loaded test host the clock skew between the two calls
        // can erode the 2-second tolerance and produce a spurious failure. A more robust approach
        // is to capture a `beforeCall` timestamp before the await and use it as the lower bound
        // (e.g., assert expiry >= beforeCall + 120s AND expiry <= UtcNow + 120s + epsilon), which
        // makes the window deterministic regardless of execution time. The 2-second tolerance
        // satisfies the acceptance criteria spec; this is a reliability concern only.
        // See review finding: TestQualityReviewer WARNING @ ChatHeartbeatTrackerTests.cs:72.
        expiry!.Value.Should().BeCloseTo(
            DateTimeOffset.UtcNow + TimeSpan.FromSeconds(120),
            TimeSpan.FromSeconds(2),
            "TTL must be 2 × ChatIdleTimeoutSeconds (120s) from now");
    }

    [Fact]
    public async Task WriteRedisHeartbeat_WhenAgentIdIsNull_ThrowsArgumentNullException()
    {
        var tracker = CreateTracker();

        var act = async () => await tracker.WriteRedisHeartbeatAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task WriteRedisHeartbeat_WhenRedisFaults_DoesNotThrow()
    {
        var redisMock = new Mock<IRedisStore>();
        redisMock.Setup(r => r.SetAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TimeSpan?>(), It.IsAny<When>()))
            .ThrowsAsync(new InvalidOperationException("simulated Redis fault"));

        var tracker = CreateTracker(redisMock.Object);

        // Awaiting the returned task exercises the async fault path through the try/catch.
        var act = async () => await tracker.WriteRedisHeartbeatAsync(TestAgentId);
        await act.Should().NotThrowAsync("Redis faults in WriteRedisHeartbeatAsync must be swallowed");
    }

    // ─── TryGetRedisHeartbeatAsync ────────────────────────────────────────────

    [Fact]
    public async Task TryGetRedisHeartbeat_KeyPresent_ReturnsTimestamp()
    {
        var fakeRedis = new CodingAgent.Web.TestUtilities.FakeRedisStore();
        var expectedMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var key = $"chat:heartbeat:{TestAgentId}";
        await fakeRedis.SetAsync(key, expectedMs.ToString(), TimeSpan.FromMinutes(5));

        var tracker = CreateTracker(fakeRedis);
        var (available, heartbeat) = await tracker.TryGetRedisHeartbeatAsync(TestJobName, TestAgentId);

        available.Should().BeTrue();
        heartbeat.Should().NotBeNull();
        heartbeat!.Value.ToUnixTimeMilliseconds().Should().Be(expectedMs);
    }

    [Fact]
    public async Task TryGetRedisHeartbeat_KeyAbsent_ReturnsAvailableTrueNullHeartbeat()
    {
        var fakeRedis = new CodingAgent.Web.TestUtilities.FakeRedisStore();
        // No key written — Redis is available but the key does not exist
        var tracker = CreateTracker(fakeRedis);
        var (available, heartbeat) = await tracker.TryGetRedisHeartbeatAsync(TestJobName, TestAgentId);

        available.Should().BeTrue("Redis is reachable — key-not-found is not a fault");
        heartbeat.Should().BeNull("key does not exist — no heartbeat was recorded");
    }

    [Fact]
    public async Task TryGetRedisHeartbeat_RedisFaults_ReturnsAvailableFalse()
    {
        var redisMock = new Mock<IRedisStore>();
        redisMock.Setup(r => r.GetAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("simulated Redis fault"));

        var tracker = CreateTracker(redisMock.Object);
        var (available, heartbeat) = await tracker.TryGetRedisHeartbeatAsync(TestJobName, TestAgentId);

        available.Should().BeFalse("Redis exception must be surfaced as Available=false");
        heartbeat.Should().BeNull();
    }

    [Fact]
    public async Task TryGetRedisHeartbeat_WhenAgentIdIsNull_ThrowsArgumentNullException()
    {
        var tracker = CreateTracker();

        var act = async () => await tracker.TryGetRedisHeartbeatAsync(TestJobName, null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ─── DeleteRedisHeartbeatAsync ────────────────────────────────────────────

    [Fact]
    public async Task DeleteRedisHeartbeat_ReturnedTask_CompletesSuccessfullyOnHappyPath()
    {
        var fakeRedis = new CodingAgent.Web.TestUtilities.FakeRedisStore();
        // Pre-populate so DeleteAsync has a key to delete (makes test intent clearer)
        await fakeRedis.SetAsync($"chat:heartbeat:{TestAgentId}", "123", TimeSpan.FromMinutes(1));
        var tracker = CreateTracker(fakeRedis);

        var task = tracker.DeleteRedisHeartbeatAsync(TestAgentId);
        await task;

        // TODO [WARNING]: Same structural issue as WriteRedisHeartbeat_ReturnedTask_CompletesSuccessfullyOnHappyPath —
        // the IsCompletedSuccessfully assertion is unreachable unless the task already completed
        // successfully (because `await task` would have thrown otherwise). The assertion is
        // redundant; the `await` is the actual regression detector. See review finding:
        // TestQualityReviewer WARNING @ ChatHeartbeatTrackerTests.cs:44.
        task.IsCompletedSuccessfully.Should().BeTrue(
            "DeleteRedisHeartbeatAsync must return a task in RanToCompletion state on Redis success, " +
            "not a Canceled task from ContinueWith(OnlyOnFaulted)");
    }

    [Fact]
    public async Task DeleteRedisHeartbeat_CallsDeleteAsyncWithCorrectKey()
    {
        var fakeRedis = new CodingAgent.Web.TestUtilities.FakeRedisStore();
        var key = $"chat:heartbeat:{TestAgentId}";
        await fakeRedis.SetAsync(key, "12345", TimeSpan.FromMinutes(5));

        var tracker = CreateTracker(fakeRedis);
        await tracker.DeleteRedisHeartbeatAsync(TestAgentId);

        var value = await fakeRedis.GetAsync(key);
        value.Should().BeNull("heartbeat key must be deleted from Redis");
    }

    [Fact]
    public async Task DeleteRedisHeartbeat_WhenAgentIdIsNull_ThrowsArgumentNullException()
    {
        var tracker = CreateTracker();

        var act = async () => await tracker.DeleteRedisHeartbeatAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task DeleteRedisHeartbeat_WhenRedisFaults_DoesNotThrow()
    {
        var redisMock = new Mock<IRedisStore>();
        redisMock.Setup(r => r.DeleteAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("simulated Redis fault"));

        var tracker = CreateTracker(redisMock.Object);

        // Awaiting the returned task exercises the async fault path through the try/catch.
        var act = async () => await tracker.DeleteRedisHeartbeatAsync(TestAgentId);
        await act.Should().NotThrowAsync("Redis faults in DeleteRedisHeartbeatAsync must be swallowed");
    }
}
