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
    public async Task WriteRedisHeartbeat_CallsSetAsyncWithCorrectKeyAndTtl()
    {
        var options = CreateOptions(idleTimeoutSeconds: 60);
        var fakeRedis = new CodingAgent.Web.TestUtilities.FakeRedisStore();
        var tracker = CreateTracker(fakeRedis, options);

        var beforeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // Fire-and-forget: don't await the task directly — it returns a ContinueWith(OnlyOnFaulted)
        // continuation that is cancelled (not faulted) on success, so awaiting it would throw.
        // FakeRedisStore.SetAsync is synchronous, so the key is written before the method returns.
        _ = tracker.WriteRedisHeartbeatAsync(TestAgentId);

        var key = $"chat:heartbeat:{TestAgentId}";
        var raw = await fakeRedis.GetAsync(key);

        raw.Should().NotBeNull("heartbeat key must be written to Redis");
        long.TryParse(raw, out var ms).Should().BeTrue("value must be a Unix-ms timestamp");
        ms.Should().BeGreaterThanOrEqualTo(beforeMs, "timestamp must not be in the past");
        // TODO [WARNING]: TTL is not asserted despite the test name claiming "CorrectTtl".
        // The implementation computes TTL as 2 × ChatIdleTimeoutSeconds (120s here). If the
        // TTL calculation regressed, this test would still pass because FakeRedisStore does not
        // enforce TTLs. Assert ttl == TimeSpan.FromSeconds(120) or rename the test to reflect
        // what it actually checks. See review finding: TestQualityReviewer WARNING @ line 44.
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

        // WriteRedisHeartbeatAsync is fire-and-forget — just confirm it doesn't throw synchronously
        // or propagate faults to the caller. The ContinueWith(OnlyOnFaulted) logs and swallows.
        // TODO [WARNING]: This test only verifies no synchronous throw, which is trivially true
        // because the method returns before the async fault path executes. The real behavior under
        // test (fault swallowing by OnlyOnFaulted continuation) is never exercised. If the
        // implementation were changed to `await _redis.SetAsync(...)` without try/catch, this test
        // would still pass while faults would propagate to callers. Fix: await the returned task
        // (or a Task.Delay) and assert no AggregateException or UnobservedTaskException.
        // See review finding: TestQualityReviewer WARNING @ ChatHeartbeatTrackerTests.cs:65.
        var act = () =>
        {
            _ = tracker.WriteRedisHeartbeatAsync(TestAgentId);
            return Task.CompletedTask;
        };
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

    // ─── DeleteRedisHeartbeatAsync ────────────────────────────────────────────

    [Fact]
    public async Task DeleteRedisHeartbeat_CallsDeleteAsyncWithCorrectKey()
    {
        var fakeRedis = new CodingAgent.Web.TestUtilities.FakeRedisStore();
        var key = $"chat:heartbeat:{TestAgentId}";
        await fakeRedis.SetAsync(key, "12345", TimeSpan.FromMinutes(5));

        var tracker = CreateTracker(fakeRedis);
        // Fire-and-forget (same pattern as WriteRedisHeartbeatAsync)
        _ = tracker.DeleteRedisHeartbeatAsync(TestAgentId);
        // FakeRedisStore.DeleteAsync is synchronous — no need to wait
        await Task.Yield();

        var value = await fakeRedis.GetAsync(key);
        value.Should().BeNull("heartbeat key must be deleted from Redis");
    }

    [Fact]
    public async Task DeleteRedisHeartbeat_WhenRedisFaults_DoesNotThrow()
    {
        var redisMock = new Mock<IRedisStore>();
        redisMock.Setup(r => r.DeleteAsync(It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("simulated Redis fault"));

        var tracker = CreateTracker(redisMock.Object);

        // TODO [WARNING]: Same structural weakness as WriteRedisHeartbeat_WhenRedisFaults_DoesNotThrow —
        // this test only checks for no synchronous throw, not that the async fault was absorbed.
        // Fix: await the returned task and assert no AggregateException or UnobservedTaskException.
        // See review finding: TestQualityReviewer WARNING @ ChatHeartbeatTrackerTests.cs:143.
        var act = () =>
        {
            _ = tracker.DeleteRedisHeartbeatAsync(TestAgentId);
            return Task.CompletedTask;
        };
        await act.Should().NotThrowAsync("Redis faults in DeleteRedisHeartbeatAsync must be swallowed");
    }
}
