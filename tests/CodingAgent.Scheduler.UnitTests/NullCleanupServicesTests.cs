using AwesomeAssertions;
using CodingAgent.Scheduler.Services;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// Tests for <see cref="NullAgentRegistryCleanupService"/> and <see cref="NullRunServiceCleanupService"/>,
/// the no-op stand-ins registered when Redis is not configured.
/// </summary>
public sealed class NullCleanupServicesTests
{
    private const string AgentRegistry = nameof(NullAgentRegistryCleanupService);
    private const string RunService = nameof(NullRunServiceCleanupService);

    private static BackgroundService Create(string name) => name switch
    {
        AgentRegistry => new NullAgentRegistryCleanupService(),
        RunService => new NullRunServiceCleanupService(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown null object"),
    };

    [Theory]
    [InlineData(AgentRegistry)]
    [InlineData(RunService)]
    public async Task StartAsync_CompletesSynchronously_AndNeverStartsSweepLoop(string name)
    {
        // Arrange
        using var service = Create(name);

        // Act
        var start = service.StartAsync(CancellationToken.None);

        // Assert — completed inline and BackgroundService.ExecuteTask (the sweep loop) was never created.
        start.IsCompletedSuccessfully.Should().BeTrue($"{name}.StartAsync must be a no-op");
        await start;
        service.ExecuteTask.Should().BeNull($"{name} must not start the PeriodicTimer sweep loop");
    }

    [Theory]
    [InlineData(AgentRegistry)]
    [InlineData(RunService)]
    public async Task StopAsync_CompletesSynchronously_EvenWithCancelledToken(string name)
    {
        // Arrange
        using var service = Create(name);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // Act
        // TODO [WARNING] StopAsync_CompletesSynchronously_EvenWithCancelledToken would still pass if the StopAsync
        // override were deleted: StartAsync is never called here and BackgroundService.StopAsync returns immediately
        // when _executeTask is null, even with a cancelled token. Locking in the no-op StopAsync contract needs a
        // started base service (private BackgroundService state), so it is left for a person to decide.
        var stop = service.StopAsync(cancelled.Token);

        // Assert
        stop.IsCompletedSuccessfully.Should().BeTrue($"{name}.StopAsync must be a no-op");
    }

    [Theory]
    [InlineData(AgentRegistry)]
    [InlineData(RunService)]
    public async Task HostLifecycle_ThroughIHostedService_NeverStartsSweepLoop(string name)
    {
        // Arrange — the generic host only sees the service as IHostedService.
        var service = Create(name);
        IHostedService hosted = service;

        // Act
        await hosted.StartAsync(CancellationToken.None);
        await hosted.StopAsync(CancellationToken.None);
        service.Dispose();

        // Assert — a real sweep service would have created ExecuteTask on start.
        service.ExecuteTask.Should().BeNull($"{name} must stay a no-op for the full host start/stop/dispose lifecycle");
    }
}
