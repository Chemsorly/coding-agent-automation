using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Api.Loop;
using CodingAgent.Orchestration.Redis;
using Moq;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Loop;

/// <summary>
/// <see cref="LoopStatusStore"/> keeps the Scheduler leader's last loop status, in Redis or in memory, and
/// treats a report older than <see cref="LoopStatusStore.MaxAge"/> as missing.
/// </summary>
public sealed class LoopStatusStoreTests
{
    private sealed class MutableTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static LoopStatusDto Status(int processed) =>
        new() { IsLoopActive = true, StatusMessage = "running", ProcessedCount = processed };

    [Fact]
    public async Task InMemory_ReturnsTheLastReportWithItsTime()
    {
        var time = new MutableTime();
        var store = new LoopStatusStore(null, time);

        await store.SaveAsync(Status(1));
        await store.SaveAsync(Status(2));

        var report = await store.FindAsync();
        report!.Status.ProcessedCount.Should().Be(2);
        report.ReportedAt.Should().Be(time.Now);
    }

    [Fact]
    public async Task InMemory_NothingReported_ReturnsNull()
    {
        (await new LoopStatusStore(null, new MutableTime()).FindAsync()).Should().BeNull();
    }

    [Fact]
    public async Task InMemory_ReportOlderThanMaxAge_ReturnsNull()
    {
        var time = new MutableTime();
        var store = new LoopStatusStore(null, time);
        await store.SaveAsync(Status(1));

        time.Now += LoopStatusStore.MaxAge - TimeSpan.FromSeconds(1);
        (await store.FindAsync()).Should().NotBeNull();
        time.Now += TimeSpan.FromSeconds(1);
        (await store.FindAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Redis_RoundTripsTheReportUnderOneKeyThatExpires()
    {
        var values = new Dictionary<string, string>();
        var redis = new Mock<IRedisStore>();
        redis.Setup(r => r.SetAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan?>(), It.IsAny<StackExchange.Redis.When>()))
            .Callback<string, string, TimeSpan?, StackExchange.Redis.When>((key, value, _, _) => values[key] = value)
            .ReturnsAsync(true);
        redis.Setup(r => r.GetAsync(It.IsAny<string>())).ReturnsAsync((string key) => values.GetValueOrDefault(key));
        var time = new MutableTime();
        var store = new LoopStatusStore(redis.Object, time);

        await store.SaveAsync(Status(7));
        var report = await new LoopStatusStore(redis.Object, time).FindAsync();

        report!.Status.ProcessedCount.Should().Be(7);
        report.Status.IsLoopActive.Should().BeTrue();
        report.ReportedAt.Should().Be(time.Now);
        redis.Verify(r => r.SetAsync("loop-status", It.IsAny<string>(), LoopStatusStore.MaxAge, It.IsAny<StackExchange.Redis.When>()), Times.Once());
    }
}
