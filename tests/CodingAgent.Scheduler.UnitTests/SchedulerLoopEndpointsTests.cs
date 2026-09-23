using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Orchestration.Redis;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Scheduler;
using Microsoft.AspNetCore.Http;
using Moq;
using System.Reflection;
using System.Text.Json;
using Xunit;
using ILeaderGate = CodingAgent.Pipeline.Interfaces.ILeaderGate;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// Unit tests for SchedulerLoopEndpoints — tests handlers and ApiKeyFilter directly
/// without spinning up a WebApplication.
/// </summary>
public sealed class SchedulerLoopEndpointsTests
{
    // ── Handler: GetLoopStatus ──────────────────────────────────────────────

    [Fact]
    public async Task GetLoopStatus_WhenCacheHasValue_ReturnsCachedDto()
    {
        var cached = MakeDto(isActive: true, status: "Running");
        var cache = new LoopStatusCache();
        cache.Update(cached);

        var mockLoop = new Mock<IPipelineLoopService>();
        var result = await SchedulerLoopEndpoints.GetLoopStatus(mockLoop.Object, cache);

        var ok = result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<LoopStatusDto>>().Subject;
        ok.Value!.IsLoopActive.Should().BeTrue();
        ok.Value.StatusMessage.Should().Be("Running");
        mockLoop.Verify(l => l.IsLoopActive, Times.Never, "must serve from cache, not the loop service");
    }

    [Fact]
    public async Task GetLoopStatus_WhenCacheEmpty_BuildsFromLoopService()
    {
        var cache = new LoopStatusCache(); // empty — no Redis store
        var mockLoop = MockLoopService(isActive: false, status: "Stopped");

        var result = await SchedulerLoopEndpoints.GetLoopStatus(mockLoop.Object, cache);

        var ok = result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<LoopStatusDto>>().Subject;
        ok.Value!.IsLoopActive.Should().BeFalse();
        ok.Value.StatusMessage.Should().Be("Stopped");
    }

    [Fact]
    public async Task GetLoopStatus_WhenCacheEmptyAndRedisHasValue_ServesRedisDto()
    {
        // Simulates the non-leader pod: local cache is empty, Redis has the leader's snapshot.
        // Uses IsLeader=false to correctly exercise the non-leader path in ReadAsync.
        var redisDto = MakeDto(isActive: true, status: "🔄 Cycle complete. Polling 1 template every 300s.");
        var json = JsonSerializer.Serialize(redisDto, PipelineJsonOptions.Default);

        var mockStore = new Mock<IRedisStore>();
        mockStore.Setup(s => s.GetAsync(LoopStatusCache.RedisKey))
            .ReturnsAsync(json);

        var mockLeader = new Mock<ILeaderGate>();
        mockLeader.Setup(g => g.IsLeader).Returns(false);

        var cache = new LoopStatusCache(mockStore.Object, mockLeader.Object);
        var mockLoop = MockLoopService(isActive: false, status: "🔄 Loop starting…");

        var result = await SchedulerLoopEndpoints.GetLoopStatus(mockLoop.Object, cache);

        var ok = result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<LoopStatusDto>>().Subject;
        ok.Value!.StatusMessage.Should().Be("🔄 Cycle complete. Polling 1 template every 300s.",
            "non-leader pod must serve the Redis snapshot, not its own stale loop service state");
        ok.Value.IsLoopActive.Should().BeTrue();
    }

    [Fact]
    public async Task GetLoopStatus_WhenCacheEmptyAndRedisUnavailable_FallsBackToLoopService()
    {
        // Redis read throws — should fall back to BuildDto(loopService) without throwing.
        // TODO [WARNING]: This test constructs the cache without an ILeaderGate (_leaderGate is null),
        // which means it exercises the single-replica / no-leader-gate fallback path, NOT the
        // multi-replica non-leader Redis-unavailable path. The name "WhenCacheEmptyAndRedisUnavailable"
        // does not communicate this distinction, which could mislead a maintainer into thinking the
        // multi-replica case is covered here. The multi-replica non-leader Redis-exception path is
        // covered by GetLoopStatus_NonLeaderWithRedisException_ReturnsNeutralDto. This test provides
        // a guard that the no-leader-gate path does NOT accidentally return a neutral DTO; adding a
        // comment to that effect would make the intent explicit.
        var mockStore = new Mock<IRedisStore>();
        mockStore.Setup(s => s.GetAsync(It.IsAny<string>()))
            .ThrowsAsync(new Exception("Redis connection refused"));

        var cache = new LoopStatusCache(mockStore.Object);
        var mockLoop = MockLoopService(isActive: false, status: "Stopped");

        var result = await SchedulerLoopEndpoints.GetLoopStatus(mockLoop.Object, cache);

        // Must not throw and must return the loop service's current state
        var ok = result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<LoopStatusDto>>().Subject;
        ok.Value!.StatusMessage.Should().Be("Stopped");
    }

    [Fact]
    public async Task GetLoopStatus_WhenLocalCachePopulated_ServesLocalWithoutHittingRedis()
    {
        // Simulates the leader pod: local value is present — Redis should never be queried.
        var localDto = MakeDto(isActive: true, status: "Local value");
        var mockStore = new Mock<IRedisStore>();

        // Explicitly mark this pod as the leader so the local fast-path is taken.
        var mockLeader = new Mock<ILeaderGate>();
        mockLeader.Setup(g => g.IsLeader).Returns(true);

        var cache = new LoopStatusCache(mockStore.Object, mockLeader.Object);
        cache.Update(localDto);

        var mockLoop = MockLoopService();
        var result = await SchedulerLoopEndpoints.GetLoopStatus(mockLoop.Object, cache);

        var ok = result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<LoopStatusDto>>().Subject;
        ok.Value!.StatusMessage.Should().Be("Local value");

        // Fast path: no Redis round-trip when local value is present and pod is leader
        mockStore.Verify(s => s.GetAsync(It.IsAny<string>()), Times.Never,
            "must not hit Redis when the local cache is populated on the leader pod");
    }

    [Fact]
    public async Task GetLoopStatus_NonLeaderWithStaleLocalCache_ServesRedisSnapshotNotLocalValue()
    {
        // Regression test for the auto-start scenario (ClosedLoopAutoStart=true):
        // Both pods call StartLoopAsync at boot, which fires OnChange and populates the local
        // cache with the stale "Loop starting…" snapshot on every pod. The non-leader pod's
        // local cache is populated but frozen — ExecuteAsync blocks in the leader-wait loop
        // without running cycles. ReadAsync must skip the stale local value and serve Redis.
        var staleLocalDto = MakeDto(isActive: true, status: "🔄 Loop starting…");
        var redisDto = MakeDto(isActive: true, status: "🔄 Cycle complete. Polling 1 template every 300s.");
        var json = JsonSerializer.Serialize(redisDto, PipelineJsonOptions.Default);

        var mockStore = new Mock<IRedisStore>();
        mockStore.Setup(s => s.GetAsync(LoopStatusCache.RedisKey)).ReturnsAsync(json);

        // Mark this pod as a non-leader.
        var mockLeader = new Mock<ILeaderGate>();
        mockLeader.Setup(g => g.IsLeader).Returns(false);

        var cache = new LoopStatusCache(mockStore.Object, mockLeader.Object);
        // Simulate auto-start: OnChange fired on this pod at boot, populating the local cache.
        cache.Update(staleLocalDto);

        var mockLoop = MockLoopService(isActive: true, status: "🔄 Loop starting…");
        var result = await SchedulerLoopEndpoints.GetLoopStatus(mockLoop.Object, cache);

        var ok = result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<LoopStatusDto>>().Subject;
        ok.Value!.StatusMessage.Should().Be("🔄 Cycle complete. Polling 1 template every 300s.",
            "non-leader pod must serve the Redis snapshot even when its local cache is populated with a stale value");
        ok.Value.IsLoopActive.Should().BeTrue();

        // The local fast-path must have been bypassed — Redis must have been queried.
        mockStore.Verify(s => s.GetAsync(LoopStatusCache.RedisKey), Times.Once,
            "non-leader pod must always query Redis, bypassing its stale local cache");
    }

    // ── LoopStatusCache.Update ──────────────────────────────────────────────

    [Fact]
    public async Task LoopStatusCache_Update_PublishesToRedis()
    {
        var mockStore = new Mock<IRedisStore>();
        mockStore.Setup(s => s.SetAsync(
                LoopStatusCache.RedisKey,
                It.IsAny<string>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<StackExchange.Redis.When>()))
            .ReturnsAsync(true);

        var cache = new LoopStatusCache(mockStore.Object);
        var dto = MakeDto(isActive: true, status: "Active");

        cache.Update(dto);

        // Fire-and-forget: poll until the async write completes (up to 5s).
        // Brain entry: poll-until-verified for fire-and-forget background tasks (general/lessons-learned.md#flaky-test).
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                mockStore.Verify(s => s.SetAsync(
                    LoopStatusCache.RedisKey,
                    It.Is<string>(v => v.Contains("Active")),
                    It.Is<TimeSpan?>(t => t >= TimeSpan.FromSeconds(300)),
                    It.IsAny<StackExchange.Redis.When>()), Times.Once);
                break;
            }
            catch (MockException)
            {
                await Task.Delay(50);
            }
        }

        mockStore.Verify(s => s.SetAsync(
            LoopStatusCache.RedisKey,
            It.Is<string>(v => v.Contains("Active")),
            It.Is<TimeSpan?>(t => t >= TimeSpan.FromSeconds(300)),
            It.IsAny<StackExchange.Redis.When>()), Times.Once,
            "Update must publish to Redis with a TTL that exceeds the maximum poll interval (300s)");
    }

    [Fact]
    public async Task LoopStatusCache_Update_WhenRedisThrows_DoesNotBubbleException()
    {
        var mockStore = new Mock<IRedisStore>();
        mockStore.Setup(s => s.SetAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<StackExchange.Redis.When>()))
            .ThrowsAsync(new Exception("Redis unavailable"));

        var cache = new LoopStatusCache(mockStore.Object);
        var dto = MakeDto(isActive: true, status: "Active");

        // Must not throw — Redis failure is fire-and-forget and must be swallowed
        var act = () => { cache.Update(dto); return Task.CompletedTask; };
        await act.Should().NotThrowAsync("Redis write failures must not propagate from Update");

        // Local value must still be set even when Redis fails
        cache.Read()!.StatusMessage.Should().Be("Active");
    }

    [Fact]
    public void LoopStatusCache_Update_WithNullStore_SetsLocalOnly()
    {
        // No Redis store — in-process only (single replica or offline).
        var cache = new LoopStatusCache();
        var dto = MakeDto(isActive: true, status: "Running");

        cache.Update(dto);

        cache.Read()!.StatusMessage.Should().Be("Running");
        cache.Read()!.IsLoopActive.Should().BeTrue();
    }

    // ── Handler: StartLoop ──────────────────────────────────────────────────

    [Fact]
    public async Task StartLoop_WhenStartsSuccessfully_PersistsAutoStartAndReturnsOk()
    {
        var mockLoop = MockLoopService();
        mockLoop.Setup(l => l.StartLoopAsync()).ReturnsAsync(true);

        var mockConfig = new Mock<IPipelineApiConfigClient>();
        mockConfig.Setup(c => c.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await SchedulerLoopEndpoints.StartLoop(mockLoop.Object, mockConfig.Object, CancellationToken.None);

        var ok = result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<LoopStartResultDto>>().Subject;
        ok.Value!.Started.Should().BeTrue();
        ok.Value.Error.Should().BeNull();
        mockConfig.Verify(c => c.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()), Times.Once,
            "ClosedLoopAutoStart must be persisted on successful start");
    }

    [Fact]
    public async Task StartLoop_WhenAlreadyActive_ReturnsErrorMessage()
    {
        var mockLoop = MockLoopService();
        mockLoop.Setup(l => l.StartLoopAsync()).ReturnsAsync(false);
        mockLoop.Setup(l => l.IsLoopActive).Returns(true);
        mockLoop.Setup(l => l.ValidationErrors).Returns([]);

        var result = await SchedulerLoopEndpoints.StartLoop(mockLoop.Object, Mock.Of<IPipelineApiConfigClient>(), CancellationToken.None);

        var ok = result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<LoopStartResultDto>>().Subject;
        ok.Value!.Started.Should().BeFalse();
        ok.Value.Error.Should().Contain("already active");
    }

    [Fact]
    public async Task StartLoop_WhenValidationErrors_ReturnsValidationErrorMessage()
    {
        var mockLoop = MockLoopService();
        mockLoop.Setup(l => l.StartLoopAsync()).ReturnsAsync(false);
        mockLoop.Setup(l => l.IsLoopActive).Returns(false);
        mockLoop.Setup(l => l.ValidationErrors).Returns(["No templates configured"]);

        var result = await SchedulerLoopEndpoints.StartLoop(mockLoop.Object, Mock.Of<IPipelineApiConfigClient>(), CancellationToken.None);

        var ok = result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<LoopStartResultDto>>().Subject;
        ok.Value!.Error.Should().Contain("validation errors");
    }

    // ── Handler: StopLoop ───────────────────────────────────────────────────

    [Fact]
    public async Task StopLoop_CallsStopLoopAndPersistsAutoStartFalse()
    {
        var mockLoop = MockLoopService();
        var mockConfig = new Mock<IPipelineApiConfigClient>();
        mockConfig.Setup(c => c.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await SchedulerLoopEndpoints.StopLoop(mockLoop.Object, mockConfig.Object, CancellationToken.None);

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.NoContent>();
        mockLoop.Verify(l => l.StopLoop(), Times.Once);
        mockConfig.Verify(c => c.UpdatePipelineConfigAsync(It.IsAny<Func<PipelineConfiguration, PipelineConfiguration>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Handler: ResumeLoop ─────────────────────────────────────────────────

    [Fact]
    public void ResumeLoop_CallsResumeLoopAndReturnsNoContent()
    {
        var mockLoop = MockLoopService();

        var result = SchedulerLoopEndpoints.ResumeLoop(mockLoop.Object);

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.NoContent>();
        mockLoop.Verify(l => l.ResumeLoop(), Times.Once);
    }

    // ── BuildDto ────────────────────────────────────────────────────────────

    [Fact]
    public void BuildDto_MapsAllFieldsFromLoopService()
    {
        var mockLoop = MockLoopService(isActive: true, status: "Active");
        mockLoop.Setup(l => l.ProcessedCount).Returns(5);
        mockLoop.Setup(l => l.FailedCount).Returns(1);

        var dto = SchedulerLoopEndpoints.BuildDto(mockLoop.Object);

        dto.IsLoopActive.Should().BeTrue();
        dto.StatusMessage.Should().Be("Active");
        dto.ProcessedCount.Should().Be(5);
        dto.FailedCount.Should().Be(1);
    }

    // ── ApiKeyFilter ────────────────────────────────────────────────────────

    [Fact]
    public async Task ApiKeyFilter_WhenKeyEmpty_Returns503AndDoesNotCallNext()
    {
        var filter = CreateFilter("");
        var nextCalled = false;
        var result = await filter.InvokeAsync(MakeContext(null), _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(null);
        });
        nextCalled.Should().BeFalse("empty/unconfigured key must block the request");
    }

    [Fact]
    public async Task ApiKeyFilter_WhenKeyMatches_CallsNext()
    {
        var filter = CreateFilter("secret");
        var nextCalled = false;
        await filter.InvokeAsync(MakeContext("secret"), _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(null);
        });
        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ApiKeyFilter_WhenKeyMissing_Returns401()
    {
        var filter = CreateFilter("secret");
        var nextCalled = false;
        var result = await filter.InvokeAsync(MakeContext(null), _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(null);
        });
        nextCalled.Should().BeFalse();
        result.Should().BeAssignableTo<IResult>();
    }

    [Fact]
    public async Task ApiKeyFilter_WhenKeyWrong_Returns401()
    {
        var filter = CreateFilter("secret");
        var nextCalled = false;
        var result = await filter.InvokeAsync(MakeContext("wrong"), _ =>
        {
            nextCalled = true;
            return ValueTask.FromResult<object?>(null);
        });
        nextCalled.Should().BeFalse();
        result.Should().BeAssignableTo<IResult>();
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static IEndpointFilter CreateFilter(string expectedKey)
    {
        var filterType = typeof(SchedulerLoopEndpoints)
            .GetNestedType("ApiKeyFilter", BindingFlags.NonPublic | BindingFlags.Instance)!;
        return (IEndpointFilter)Activator.CreateInstance(filterType, expectedKey)!;
    }

    private static EndpointFilterInvocationContext MakeContext(string? headerValue)
    {
        var httpContext = new DefaultHttpContext();
        if (headerValue is not null)
            httpContext.Request.Headers["X-Api-Key"] = headerValue;
        var mock = new Mock<EndpointFilterInvocationContext>();
        mock.Setup(m => m.HttpContext).Returns(httpContext);
        return mock.Object;
    }

    private static Mock<IPipelineLoopService> MockLoopService(bool isActive = false, string status = "")
    {
        var mock = new Mock<IPipelineLoopService>();
        mock.Setup(l => l.IsLoopActive).Returns(isActive);
        mock.Setup(l => l.StatusMessage).Returns(status);
        mock.Setup(l => l.CurrentIssueIdentifier).Returns((string?)null);
        mock.Setup(l => l.ProcessedCount).Returns(0);
        mock.Setup(l => l.FailedCount).Returns(0);
        mock.Setup(l => l.QueueCount).Returns(0);
        mock.Setup(l => l.IsCircuitBroken).Returns(false);
        mock.Setup(l => l.LastPollError).Returns((string?)null);
        mock.Setup(l => l.CurrentCycleTemplateIndex).Returns(0);
        mock.Setup(l => l.CurrentCycleTemplateCount).Returns(0);
        mock.Setup(l => l.ValidationErrors).Returns([]);
        mock.Setup(l => l.TemplateStatuses).Returns(new Dictionary<string, ConfigStatusSnapshot>());
        return mock;
    }

    private static LoopStatusDto MakeDto(bool isActive, string status) => new(
        isActive, status, null, 0, 0, 0, false, null, 0, 0, [], new Dictionary<string, ConfigStatusSnapshot>());

    // ── New tests: Change A — TTL exceeds max poll interval ─────────────────

    /// <summary>
    /// Acceptance criterion: both pods return the leader's current statusMessage > 30 s after
    /// "Cycle complete". The TTL written to Redis must be at least as long as the maximum poll
    /// interval (300 s) so the key does not expire during the idle DelayOrStop wait.
    /// </summary>
    [Fact]
    public async Task LoopStatusCache_Update_WritesRedisWithTtlGreaterThanMaxPollInterval()
    {
        TimeSpan? capturedTtl = null;
        var mockStore = new Mock<IRedisStore>();
        mockStore.Setup(s => s.SetAsync(
                LoopStatusCache.RedisKey,
                It.IsAny<string>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<StackExchange.Redis.When>()))
            .Callback<string, string, TimeSpan?, StackExchange.Redis.When>((_, _, ttl, _) => capturedTtl = ttl)
            .ReturnsAsync(true);

        var cache = new LoopStatusCache(mockStore.Object);
        cache.Update(MakeDto(isActive: true, status: "Cycle complete"));

        // Poll until the fire-and-forget write completes
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (capturedTtl is null && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        capturedTtl.Should().NotBeNull("TTL must be explicitly set — null means no expiry guard");
        // TODO [WARNING]: The lower bound here (300s) is lower than the actual RedisTtl constant
        // (600s). A regression that sets RedisTtl back to, say, 400s would still pass this test.
        // Consider asserting >= TimeSpan.FromSeconds(600) to lock in the specific 2× safety margin,
        // or assert equality to TimeSpan.FromSeconds(600) to catch any accidental TTL reduction.
        // The same applies to the TTL assertion in Update_WhenLeaderAndLoopActive_PublishesToRedis.
        capturedTtl!.Value.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(300),
            "Redis key must survive the full 300s poll interval; the old 30s TTL caused it to expire mid-cycle");
    }

    // ── New tests: Change B — non-leader write guard ─────────────────────────

    /// <summary>
    /// Acceptance criterion: a non-leader restart does not overwrite the leader's Redis snapshot.
    /// Non-leader pods must skip the SetAsync call in Update() while still updating local state.
    /// </summary>
    [Fact]
    public async Task LoopStatusCache_Update_WhenNonLeader_DoesNotWriteToRedis()
    {
        var mockStore = new Mock<IRedisStore>();
        mockStore.Setup(s => s.SetAsync(
                It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<TimeSpan?>(), It.IsAny<StackExchange.Redis.When>()))
            .ReturnsAsync(true);

        var mockLeader = new Mock<ILeaderGate>();
        mockLeader.Setup(g => g.IsLeader).Returns(false);

        var cache = new LoopStatusCache(mockStore.Object, mockLeader.Object);
        var dto = MakeDto(isActive: true, status: "🔄 Loop starting…");

        cache.Update(dto);

        // TODO [WARNING]: The 100ms delay here is defensive — on the non-leader path, the write
        // guard returns before any Task is created, so Times.Never is deterministic and the delay
        // is not strictly needed. However, the 100ms is an arbitrary wall-clock wait: on a very
        // slow or stressed CI machine a hypothetical async write path could start but not complete
        // within this window, making the verify non-deterministic. Consider using a
        // TaskCompletionSource or polling loop (as in WhenLeader_WritesToRedis) for symmetry,
        // or at minimum add a comment explaining why no async work is created on this path.
        // Give any potential fire-and-forget a moment to execute
        await Task.Delay(100);

        // Non-leader must NOT write to Redis
        mockStore.Verify(s => s.SetAsync(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<TimeSpan?>(), It.IsAny<StackExchange.Redis.When>()),
            Times.Never,
            "non-leader pod must not overwrite the leader's Redis snapshot");

        // Local value must still be updated (Volatile.Write is unconditional)
        cache.Read()!.StatusMessage.Should().Be("🔄 Loop starting…",
            "local in-memory snapshot must always be updated regardless of leader status");
    }

    /// <summary>
    /// Positive counterpart to WhenNonLeader_DoesNotWriteToRedis: a leader pod must still
    /// write to Redis. Paired to ensure the non-leader test is not vacuously passing.
    /// </summary>
    [Fact]
    public async Task LoopStatusCache_Update_WhenLeader_WritesToRedis()
    {
        var mockStore = new Mock<IRedisStore>();
        mockStore.Setup(s => s.SetAsync(
                LoopStatusCache.RedisKey,
                It.IsAny<string>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<StackExchange.Redis.When>()))
            .ReturnsAsync(true);

        var mockLeader = new Mock<ILeaderGate>();
        mockLeader.Setup(g => g.IsLeader).Returns(true);

        var cache = new LoopStatusCache(mockStore.Object, mockLeader.Object);
        cache.Update(MakeDto(isActive: true, status: "Running"));

        // Poll until the fire-and-forget write completes
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                mockStore.Verify(s => s.SetAsync(
                    LoopStatusCache.RedisKey, It.IsAny<string>(),
                    It.IsAny<TimeSpan?>(), It.IsAny<StackExchange.Redis.When>()), Times.Once);
                break;
            }
            catch (MockException) { await Task.Delay(20); }
        }

        mockStore.Verify(s => s.SetAsync(
            LoopStatusCache.RedisKey, It.IsAny<string>(),
            It.IsAny<TimeSpan?>(), It.IsAny<StackExchange.Redis.When>()), Times.Once,
            "leader pod must write to Redis on Update");
    }

    // ── New tests: Change C — non-leader fallback returns neutral DTO ────────

    /// <summary>
    /// Acceptance criterion: non-leader fallback when Redis is empty.
    /// When the Redis key is missing (expired between cycles), non-leader pods must return
    /// a neutral DTO rather than falling back to BuildDto(loopService) and serving stale
    /// "Loop starting…" state.
    /// </summary>
    [Fact]
    public async Task GetLoopStatus_NonLeaderWithEmptyRedis_ReturnsNeutralDto()
    {
        var mockStore = new Mock<IRedisStore>();
        mockStore.Setup(s => s.GetAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        var mockLeader = new Mock<ILeaderGate>();
        mockLeader.Setup(g => g.IsLeader).Returns(false);

        var cache = new LoopStatusCache(mockStore.Object, mockLeader.Object);
        // Simulate stale local state from auto-start on this non-leader pod.
        // TODO [WARNING]: Update() on a non-leader skips the Redis write but still updates
        // _value via Volatile.Write. In ReadAsync, the non-leader path unconditionally skips
        // the local fast-path and goes to Redis, so the stale _value populated here is never
        // consulted regardless of BuildNeutralDtoIfNonLeader. This means the test does not
        // directly exercise the scenario where a regressed ReadAsync accidentally reads _value
        // on a non-leader — it only catches regressions that re-introduce the local fast-path
        // for non-leaders (removal of the _leaderGate guard in ReadAsync). A complementary
        // test with _value=null (pod just started, Update never called) would cover the
        // zero-local-state path explicitly.
        cache.Update(MakeDto(isActive: true, status: "🔄 Loop starting…"));

        var mockLoop = MockLoopService(isActive: true, status: "🔄 Loop starting…");
        var result = await SchedulerLoopEndpoints.GetLoopStatus(mockLoop.Object, cache);

        var ok = result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<LoopStatusDto>>().Subject;
        // Assert the exact neutral DTO message so that any change to BuildNeutralDtoIfNonLeader
        // (e.g. renaming the message) is caught immediately, rather than relying on a weaker
        // NotBe or Contain check that could pass accidentally via an unrelated code path.
        ok.Value!.StatusMessage.Should().Be("⏳ Waiting for leader status…",
            "non-leader pod must return the exact neutral DTO message when Redis has no value, not its own stale local state");
        ok.Value.IsLoopActive.Should().BeFalse(
            "neutral DTO must indicate the loop is not active on this non-leader pod");
    }

    /// <summary>
    /// When Redis throws on a non-leader pod (outage), the neutral DTO must also be returned
    /// rather than falling back to the stale local loop service state.
    /// </summary>
    [Fact]
    public async Task GetLoopStatus_NonLeaderWithRedisException_ReturnsNeutralDto()
    {
        var mockStore = new Mock<IRedisStore>();
        mockStore.Setup(s => s.GetAsync(It.IsAny<string>()))
            .ThrowsAsync(new Exception("Redis connection refused"));

        var mockLeader = new Mock<ILeaderGate>();
        mockLeader.Setup(g => g.IsLeader).Returns(false);

        var cache = new LoopStatusCache(mockStore.Object, mockLeader.Object);
        cache.Update(MakeDto(isActive: true, status: "🔄 Loop starting…"));

        var mockLoop = MockLoopService(isActive: true, status: "🔄 Loop starting…");
        var result = await SchedulerLoopEndpoints.GetLoopStatus(mockLoop.Object, cache);

        var ok = result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<LoopStatusDto>>().Subject;
        // Assert the exact neutral DTO message so that any change to BuildNeutralDtoIfNonLeader
        // is caught immediately, rather than relying on weaker NotBe/Contain checks.
        ok.Value!.StatusMessage.Should().Be("⏳ Waiting for leader status…",
            "neutral DTO must be returned on Redis exception for non-leader pods, not the stale local state");
        ok.Value.IsLoopActive.Should().BeFalse(
            "neutral DTO must report IsLoopActive=false");
    }

    /// <summary>
    /// Guard against accidentally applying the neutral DTO to leader pods:
    /// when the leader has no local value yet and Redis is empty (first cycle not yet complete),
    /// GetLoopStatus must still fall back to BuildDto(loopService).
    /// </summary>
    [Fact]
    public async Task GetLoopStatus_LeaderWithEmptyRedisAndNoLocalValue_FallsBackToLoopService()
    {
        var mockStore = new Mock<IRedisStore>();
        mockStore.Setup(s => s.GetAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        var mockLeader = new Mock<ILeaderGate>();
        mockLeader.Setup(g => g.IsLeader).Returns(true);

        // Leader with no local value yet (_value is null — first cycle not started)
        var cache = new LoopStatusCache(mockStore.Object, mockLeader.Object);

        var mockLoop = MockLoopService(isActive: true, status: "🔄 Loop starting…");
        var result = await SchedulerLoopEndpoints.GetLoopStatus(mockLoop.Object, cache);

        var ok = result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<LoopStatusDto>>().Subject;
        ok.Value!.StatusMessage.Should().Be("🔄 Loop starting…",
            "leader must fall back to BuildDto(loopService) during the pre-first-cycle startup window");
        ok.Value.IsLoopActive.Should().BeTrue(
            "leader's local loop service state must be reflected in the fallback");
    }
}
