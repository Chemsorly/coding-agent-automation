using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Tests for <see cref="AgentRegistrySyncService"/>, the background poller that keeps
/// <see cref="ApiAgentRegistryService"/> populated.
///
/// <para>
/// Without this poller the API-backed registry never leaves its empty initial state and the whole
/// fix is inert, so the tests assert the loop actually keeps ticking — including across a failed
/// fetch, which is the case that would otherwise silently freeze agent presence at whatever the
/// last successful poll saw.
/// </para>
/// </summary>
public sealed class AgentRegistrySyncServiceTests
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TestPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for the background loop to reach the mocked client before failing.</summary>
    private static readonly TimeSpan CallWait = TimeSpan.FromSeconds(10);

    private static AgentEntry Agent(string id) => new()
    {
        AgentId = new AgentId(id),
        ConnectionId = $"conn-{id}",
        Hostname = $"host-{id}",
        Labels = new List<string> { "dotnet" },
        Status = AgentStatus.Idle,
        RegisteredAt = Origin,
        LastHeartbeatAt = Origin
    };

    [Fact]
    public async Task Poller_PopulatesTheRegistryOnItsFirstTick()
    {
        var clock = new FakeTimeProvider(Origin);
        var calls = new SemaphoreSlim(0);
        var client = new Mock<IPipelineApiAgentClient>();
        client.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
              .Returns(() =>
              {
                  calls.Release();
                  return Task.FromResult<IReadOnlyList<AgentEntry>>(new List<AgentEntry> { Agent("a1") });
              });

        var registry = new ApiAgentRegistryService(client.Object, clock, new Mock<ILogger>().Object);
        var service = new AgentRegistrySyncService(registry, clock, new Mock<ILogger>().Object)
        {
            PollInterval = TestPollInterval
        };

        await service.StartAsync(CancellationToken.None);
        try
        {
            (await calls.WaitAsync(CallWait)).Should().BeTrue("the poller must fetch immediately, "
                + "not only after the first interval elapses — otherwise the UI shows no agents on load");

            // The semaphore is released inside the mock, before the loop publishes the snapshot.
            await WaitUntilAsync(() => registry.GetAllAgents().Count == 1);
            registry.GetAllAgents().Should().ContainSingle();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Poller_KeepsFetchingOnEachInterval()
    {
        var clock = new FakeTimeProvider(Origin);
        var calls = new SemaphoreSlim(0);
        var client = new Mock<IPipelineApiAgentClient>();
        client.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
              .Returns(() =>
              {
                  calls.Release();
                  return Task.FromResult<IReadOnlyList<AgentEntry>>(new List<AgentEntry> { Agent("a1") });
              });

        var registry = new ApiAgentRegistryService(client.Object, clock, new Mock<ILogger>().Object);
        var service = new AgentRegistrySyncService(registry, clock, new Mock<ILogger>().Object)
        {
            PollInterval = TestPollInterval
        };

        await service.StartAsync(CancellationToken.None);
        try
        {
            (await calls.WaitAsync(CallWait)).Should().BeTrue();

            clock.Advance(TestPollInterval);
            (await calls.WaitAsync(CallWait)).Should().BeTrue("tick 2");

            clock.Advance(TestPollInterval);
            (await calls.WaitAsync(CallWait)).Should().BeTrue("tick 3");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A transient API failure must not kill the loop. If it did, the registry would freeze at its
    /// last snapshot, age out, and report an empty cluster permanently — the exact defect this work
    /// set out to fix, reintroduced through the back door.
    /// </summary>
    [Fact]
    public async Task Poller_SurvivesAFailedFetch_AndRecoversOnTheNextTick()
    {
        var clock = new FakeTimeProvider(Origin);
        var calls = new SemaphoreSlim(0);
        var attempt = 0;
        var client = new Mock<IPipelineApiAgentClient>();
        client.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
              .Returns(() =>
              {
                  var n = Interlocked.Increment(ref attempt);
                  calls.Release();
                  return n == 1
                      ? Task.FromException<IReadOnlyList<AgentEntry>>(new HttpRequestException("api down"))
                      : Task.FromResult<IReadOnlyList<AgentEntry>>(new List<AgentEntry> { Agent("a1") });
              });

        var registry = new ApiAgentRegistryService(client.Object, clock, new Mock<ILogger>().Object);
        var service = new AgentRegistrySyncService(registry, clock, new Mock<ILogger>().Object)
        {
            PollInterval = TestPollInterval
        };

        await service.StartAsync(CancellationToken.None);
        try
        {
            (await calls.WaitAsync(CallWait)).Should().BeTrue("the failing first attempt");
            registry.GetAllAgents().Should().BeEmpty();

            clock.Advance(TestPollInterval);
            (await calls.WaitAsync(CallWait)).Should().BeTrue("the loop must still be alive");

            // The second fetch succeeds; give the loop a moment to publish the snapshot.
            await WaitUntilAsync(() => registry.GetAllAgents().Count == 1);
            registry.GetAllAgents().Should().ContainSingle();
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopAsync_EndsTheLoopCleanly()
    {
        var clock = new FakeTimeProvider(Origin);
        var calls = new SemaphoreSlim(0);
        var client = new Mock<IPipelineApiAgentClient>();
        client.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
              .Returns(() =>
              {
                  calls.Release();
                  return Task.FromResult<IReadOnlyList<AgentEntry>>(new List<AgentEntry>());
              });

        var registry = new ApiAgentRegistryService(client.Object, clock, new Mock<ILogger>().Object);
        var service = new AgentRegistrySyncService(registry, clock, new Mock<ILogger>().Object)
        {
            PollInterval = TestPollInterval
        };

        await service.StartAsync(CancellationToken.None);
        (await calls.WaitAsync(CallWait)).Should().BeTrue();

        // Cancellation of PeriodicTimer.WaitForNextTickAsync must be absorbed, not surfaced as a fault.
        var stop = async () => await service.StopAsync(CancellationToken.None);
        await stop.Should().NotThrowAsync();

        service.ExecuteTask.Should().NotBeNull();
        service.ExecuteTask!.IsFaulted.Should().BeFalse();
    }

    /// <summary>
    /// Polls a condition on the real clock. The service loop runs on the thread pool, so the
    /// snapshot publish happens shortly after the mocked fetch returns rather than synchronously.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + CallWait;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(20);
        }
    }
}
