using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Api.Chat;
using CodingAgent.Web.TestUtilities;

namespace CodingAgent.Api.IntegrationTests.Chat;

/// <summary>
/// Tests for <see cref="ChatSessionStore"/> covering the Redis-backed paths (redis != null) and the
/// in-memory TTL eviction path. The in-memory happy-path (redis == null) is already covered by
/// <see cref="ChatServiceTests"/>.
/// </summary>
public sealed class ChatSessionStoreTests
{
    private const string AgentId = "agent-1";
    private const string ExpectedKey = "chat-session:agent-1";

    private static ChatSessionRecord MakeRecord() =>
        new(AgentId, "oidc", "oidc:tester", null, "profile-1", "window-1");

    // ── SaveAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveAsync_WithRedis_WritesSerializedRecordToRedisWithCorrectKeyAndTtl()
    {
        var redis = new FakeRedisStore();
        var sut = new ChatSessionStore(redis, TimeSpan.FromHours(1));
        var record = MakeRecord();

        await sut.SaveAsync(record);

        var json = await redis.GetAsync(ExpectedKey);
        json.Should().NotBeNull();
        JsonSerializer.Deserialize<ChatSessionRecord>(json!).Should().Be(record);

        var expiry = redis.GetExpiry(ExpectedKey);
        expiry.Should().NotBeNull();
        // Assert the expiry matches the configured 1-hour lifetime, within a 5-second tolerance.
        // This catches bugs where production code passes the wrong TimeSpan (e.g. a hardcoded constant).
        expiry!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow.AddHours(1), precision: TimeSpan.FromSeconds(5));
    }

    // ── FindAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task FindAsync_WithRedis_ReturnsDeserializedRecord_WhenKeyPresent()
    {
        var redis = new FakeRedisStore();
        var sut = new ChatSessionStore(redis, TimeSpan.FromHours(1));
        var record = MakeRecord();
        await redis.SetAsync(ExpectedKey, JsonSerializer.Serialize(record), TimeSpan.FromHours(1));

        var result = await sut.FindAsync(AgentId);

        result.Should().NotBeNull();
        result.Should().Be(record);
    }

    [Fact]
    public async Task FindAsync_WithRedis_ReturnsNull_WhenKeyAbsent()
    {
        var redis = new FakeRedisStore();
        var sut = new ChatSessionStore(redis, TimeSpan.FromHours(1));

        // Pre-populate _local with a record so that accidental fallthrough to the in-memory path
        // would return a non-null value rather than null, making this assertion catch the regression.
        var staleRecord = MakeRecord();
        InternalLocal(sut)[AgentId] = (staleRecord, DateTimeOffset.UtcNow.AddHours(1));

        // Redis has no key for AgentId — the Redis path returns null.
        var result = await sut.FindAsync(AgentId);

        result.Should().BeNull();
        // If FindAsync accidentally fell through to _local, it would have returned staleRecord above.
        // The null result confirms the Redis path took the correct early-return without reading _local.
    }

    // ── DeleteAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAsync_WithRedis_RemovesKeyFromRedis()
    {
        var redis = new FakeRedisStore();
        var sut = new ChatSessionStore(redis, TimeSpan.FromHours(1));
        var record = MakeRecord();
        await redis.SetAsync(ExpectedKey, JsonSerializer.Serialize(record), TimeSpan.FromHours(1));

        await sut.DeleteAsync(AgentId);

        (await redis.GetAsync(ExpectedKey)).Should().BeNull();
    }

    // ── TTL eviction ──────────────────────────────────────────────────────

    [Fact]
    public async Task FindAsync_AfterTtlExpiry_ReturnsNull_AndEvictsKeyFromInMemoryCache()
    {
        var sut = new ChatSessionStore(null, TimeSpan.FromMilliseconds(1));
        var record = MakeRecord();
        await sut.SaveAsync(record);

        // Wait 20× the TTL to guarantee expiry on any CI machine.
        await Task.Delay(TimeSpan.FromMilliseconds(20));

        var result = await sut.FindAsync(AgentId);

        result.Should().BeNull();
        // Verify _local.TryRemove ran and cleaned up the expired entry.
        InternalLocal(sut).ContainsKey(AgentId).Should().BeFalse();
    }

    // ── Private helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Accesses the private <c>_local</c> ConcurrentDictionary via reflection to assert
    /// TTL eviction actually removes the entry (not merely returns null for an expired-but-present key).
    /// </summary>
    private static ConcurrentDictionary<string, (ChatSessionRecord Record, DateTimeOffset ExpiresAt)> InternalLocal(
        ChatSessionStore store)
    {
        var field = typeof(ChatSessionStore)
            .GetField("_local", BindingFlags.NonPublic | BindingFlags.Instance);
        return (ConcurrentDictionary<string, (ChatSessionRecord Record, DateTimeOffset ExpiresAt)>)field!.GetValue(store)!;
    }
}
