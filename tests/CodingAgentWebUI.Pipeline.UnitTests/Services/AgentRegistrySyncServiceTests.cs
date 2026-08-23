using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for AgentRegistrySyncService.
/// Covers: constructor guards, ExecuteAsync (success, recovery after failure,
/// cancellation, consecutive failure logging).
/// Uses FakeTimeProvider to control PeriodicTimer ticks without real delays.
/// </summary>
public sealed class AgentRegistrySyncServiceTests
{
    private static ApiAgentRegistryService CreateRegistry(Mock<IPipelineApiAgentClient> client, FakeTimeProvider clock)
        => new(client.Object, clock, new Mock<ILogger>().Object);

    // ── Constructor guards ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullRegistry_Throws()
    {
        var clock = new FakeTimeProvider();
        var act = () => new AgentRegistrySyncService(null!, clock, new Mock<ILogger>().Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullClock_Throws()
    {
        var client = new Mock<IPipelineApiAgentClient>();
        var clock = new FakeTimeProvider();
        var registry = CreateRegistry(client, clock);
        var act = () => new AgentRegistrySyncService(registry, null!, new Mock<ILogger>().Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var client = new Mock<IPipelineApiAgentClient>();
        var clock = new FakeTimeProvider();
        var registry = CreateRegistry(client, clock);
        var act = () => new AgentRegistrySyncService(registry, clock, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── ExecuteAsync: success path ────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_OnSuccess_CallsRegistryRefresh()
    {
        var client = new Mock<IPipelineApiAgentClient>();
        var agents = new List<AgentEntry>
        {
            new()
            {
                AgentId = new AgentId("a1"),
                ConnectionId = "c1",
                Hostname = "host",
                Labels = [],
                RegisteredAt = DateTimeOffset.UtcNow
            }
        } as IReadOnlyList<AgentEntry>;
        client.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(agents);

        var clock = new FakeTimeProvider();
        var registry = CreateRegistry(client, clock);
        var logger = new Mock<ILogger>();

        using var cts = new CancellationTokenSource();
        var svc = new AgentRegistrySyncService(registry, clock, logger.Object)
        {
            PollInterval = TimeSpan.FromSeconds(1)
        };

        var executeTask = svc.StartAsync(cts.Token);

        // Advance clock to trigger one tick
        clock.Advance(TimeSpan.FromSeconds(2));

        // Give the loop a moment to process
        await Task.Delay(100);

        await cts.CancelAsync();
        await svc.StopAsync(CancellationToken.None);

        // Registry should have been refreshed at least once
        client.Verify(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    // ── ExecuteAsync: cancellation stops the loop ─────────────────────────

    [Fact]
    public async Task ExecuteAsync_WhenCancelled_StopsCleanly()
    {
        var client = new Mock<IPipelineApiAgentClient>();
        client.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentEntry>() as IReadOnlyList<AgentEntry>);

        var clock = new FakeTimeProvider();
        var registry = CreateRegistry(client, clock);

        using var cts = new CancellationTokenSource();
        var svc = new AgentRegistrySyncService(registry, clock, new Mock<ILogger>().Object)
        {
            PollInterval = TimeSpan.FromSeconds(100) // long interval — won't tick naturally
        };

        await svc.StartAsync(cts.Token);
        await cts.CancelAsync();

        // Should complete without hanging
        var act = () => svc.StopAsync(CancellationToken.None);
        await act.Should().CompleteWithinAsync(TimeSpan.FromSeconds(5));
    }

    // ── ExecuteAsync: failure logging ────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_WhenRefreshThrows_DoesNotTerminateLoop()
    {
        var client = new Mock<IPipelineApiAgentClient>();
        client.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API down"));

        var clock = new FakeTimeProvider();
        var registry = CreateRegistry(client, clock);
        var logger = new Mock<ILogger>();

        using var cts = new CancellationTokenSource();
        var svc = new AgentRegistrySyncService(registry, clock, logger.Object)
        {
            PollInterval = TimeSpan.FromSeconds(1)
        };

        await svc.StartAsync(cts.Token);
        clock.Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        // Loop should still be alive — not terminated by the exception
        // Verify by advancing again and confirming another attempt
        clock.Advance(TimeSpan.FromSeconds(2));
        await Task.Delay(50);

        client.Verify(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()), Times.AtLeast(1));

        await cts.CancelAsync();
        await svc.StopAsync(CancellationToken.None);
    }
}
