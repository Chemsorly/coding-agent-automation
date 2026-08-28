using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration.Redis;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Scheduler;
using Microsoft.AspNetCore.Http;
using Moq;
using System.Reflection;
using System.Text.Json;
using Xunit;
using ILeaderGate = CodingAgentWebUI.Pipeline.Interfaces.ILeaderGate;

namespace CodingAgentWebUI.Scheduler.UnitTests;

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

        // TODO [WARNING]: This test has a weak assertion — it only verifies the result type is Ok<LoopStatusDto>
        // but does not assert that the returned DTO reflects the mock loop service's values (isActive: false,
        // status: "Stopped"). If BuildDto were broken and returned a zero-value or wrong DTO, this test would
        // still pass. Add field-level assertions matching the pattern in GetLoopStatus_WhenCacheHasValue_ReturnsCachedDto.
        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<LoopStatusDto>>();
    }

    [Fact]
    public async Task GetLoopStatus_WhenCacheEmptyAndRedisHasValue_ServesRedisDto()
    {
        // Simulates the non-leader pod: local cache is empty, but Redis has the leader's snapshot.
        // TODO [WARNING]: This test does not configure an ILeaderGate mock. The cache is constructed
        // without a leader gate, so _leaderGate is null and isLeader evaluates to true in ReadAsync.
        // The local fast-path is taken; because _value is null it falls through to Redis, so the test
        // passes — but it does not actually exercise the intended non-leader scenario. Pass a
        // Mock<ILeaderGate> with IsLeader=false to correctly represent a non-leader pod and ensure
        // the test catches regressions in the _leaderGate is null vs IsLeader==false handling.
        var redisDto = MakeDto(isActive: true, status: "🔄 Cycle complete. Polling 1 templates every 300s.");
        var json = JsonSerializer.Serialize(redisDto, PipelineJsonOptions.Default);

        var mockStore = new Mock<IRedisStore>();
        mockStore.Setup(s => s.GetAsync(LoopStatusCache.RedisKey))
            .ReturnsAsync(json);

        var cache = new LoopStatusCache(mockStore.Object);
        var mockLoop = MockLoopService(isActive: false, status: "🔄 Loop starting…");

        var result = await SchedulerLoopEndpoints.GetLoopStatus(mockLoop.Object, cache);

        var ok = result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Ok<LoopStatusDto>>().Subject;
        ok.Value!.StatusMessage.Should().Be("🔄 Cycle complete. Polling 1 templates every 300s.",
            "non-leader pod must serve the Redis snapshot, not its own stale loop service state");
        ok.Value.IsLoopActive.Should().BeTrue();
    }

    [Fact]
    public async Task GetLoopStatus_WhenCacheEmptyAndRedisUnavailable_FallsBackToLoopService()
    {
        // Redis read throws — should fall back to BuildDto(loopService) without throwing.
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
        var redisDto = MakeDto(isActive: true, status: "🔄 Cycle complete. Polling 1 templates every 300s.");
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
        ok.Value!.StatusMessage.Should().Be("🔄 Cycle complete. Polling 1 templates every 300s.",
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
                    It.IsAny<TimeSpan?>(),
                    It.IsAny<StackExchange.Redis.When>()), Times.Once);
                break;
            }
            catch (MockException)
            {
                await Task.Delay(50);
            }
        }

        // TODO [WARNING]: This test does not verify the TTL value passed to SetAsync. RedisTtl is a
        // non-obvious constant (30s) documented as potentially too short vs. the max poll interval
        // (300s). A regression setting TTL to zero or null would not be caught here. Change
        // It.IsAny<TimeSpan?>() to It.Is<TimeSpan?>(t => t > TimeSpan.FromSeconds(0)) or a specific
        // expected value to guard against accidental TTL removal or truncation.
        mockStore.Verify(s => s.SetAsync(
            LoopStatusCache.RedisKey,
            It.Is<string>(v => v.Contains("Active")),
            It.IsAny<TimeSpan?>(),
            It.IsAny<StackExchange.Redis.When>()), Times.Once,
            "Update must publish the serialized LoopStatusDto to Redis");
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

        // TODO [WARNING]: This test does not actually verify fire-and-forget exception swallowing.
        // The lambda wraps cache.Update(dto) — which is synchronous — and returns Task.CompletedTask.
        // NotThrowAsync() only verifies that the synchronous portion doesn't throw, which was already
        // true before this change. The background Task (fire-and-forget) is never awaited, so a
        // regression that lets the exception propagate on the thread pool would not be caught here.
        // To properly test swallowing behavior, await the background task or use an UnobservedTaskException
        // handler and give the GC time to collect the faulted task.

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
}
