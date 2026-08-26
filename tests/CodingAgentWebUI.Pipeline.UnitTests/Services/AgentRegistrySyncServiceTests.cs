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
/// Focuses on constructor guards and basic lifecycle. Heavy async/BackgroundService
/// tests are kept minimal to avoid parallel deadlocks with PeriodicTimer.
/// </summary>
[Collection("BackgroundServiceTests")]
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

    // ── PollInterval property ─────────────────────────────────────────────

    [Fact]
    public void PollInterval_DefaultIs2Seconds()
    {
        var client = new Mock<IPipelineApiAgentClient>();
        var clock = new FakeTimeProvider();
        var registry = CreateRegistry(client, clock);
        var svc = new AgentRegistrySyncService(registry, clock, new Mock<ILogger>().Object);
        svc.PollInterval.Should().Be(TimeSpan.FromSeconds(2));
    }

    // ── Cancellation stops service cleanly ────────────────────────────────

    [Fact]
    public async Task StopAsync_WhenStarted_CompletesCleanly()
    {
        var client = new Mock<IPipelineApiAgentClient>();
        client.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentEntry>() as IReadOnlyList<AgentEntry>);

        var clock = new FakeTimeProvider();
        var registry = CreateRegistry(client, clock);

        using var cts = new CancellationTokenSource();
        var svc = new AgentRegistrySyncService(registry, clock, new Mock<ILogger>().Object)
        {
            PollInterval = TimeSpan.FromHours(1) // very long — won't tick naturally
        };

        await svc.StartAsync(cts.Token);
        await cts.CancelAsync();

        var act = () => svc.StopAsync(CancellationToken.None);
        await act.Should().CompleteWithinAsync(TimeSpan.FromSeconds(5));
    }
}

// Isolates BackgroundService tests from parallel test execution to prevent PeriodicTimer deadlocks
[CollectionDefinition("BackgroundServiceTests", DisableParallelization = true)]
public sealed class BackgroundServiceTestsCollection { }
