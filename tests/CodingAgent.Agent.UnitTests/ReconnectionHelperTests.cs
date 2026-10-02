using AwesomeAssertions;
using CodingAgent.Agent;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Tests for <see cref="ReconnectionHelper.CalculateReconnectionDelay"/> (exponential backoff math)
/// and <see cref="HubConnectionManagerFactory.Create"/>.
/// Migrated from <c>AgentWorkerServiceReconnectionTests.cs</c> when that file was deleted as
/// part of removing the unused job path from chat-mode agent pods (Issue #3228).
/// </summary>
[Collection("EnvironmentVariables")]
public class ReconnectionHelperTests
{
    // ── ReconnectionHelper.CalculateReconnectionDelay ─────────────────────

    [Fact]
    public void CalculateReconnectionDelay_FirstAttempt_ReturnsBaseDelay()
    {
        // 2^1 = 2 seconds + 0-1s jitter
        var delay = ReconnectionHelper.CalculateReconnectionDelay(1);
        delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2));
        delay.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void CalculateReconnectionDelay_ExponentialIncrease()
    {
        // attempt 2 → 2^2=4s, attempt 3 → 2^3=8s, attempt 4 → 2^4=16s
        var delay2 = ReconnectionHelper.CalculateReconnectionDelay(2);
        var delay3 = ReconnectionHelper.CalculateReconnectionDelay(3);
        var delay4 = ReconnectionHelper.CalculateReconnectionDelay(4);

        // Each delay (minus jitter) should be ~double the previous
        delay2.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(4));
        delay2.Should().BeLessThan(TimeSpan.FromSeconds(5));
        delay3.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(8));
        delay3.Should().BeLessThan(TimeSpan.FromSeconds(9));
        delay4.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(16));
        delay4.Should().BeLessThan(TimeSpan.FromSeconds(17));

        delay3.Should().BeGreaterThan(delay2);
        delay4.Should().BeGreaterThan(delay3);
    }

    [Fact]
    public void CalculateReconnectionDelay_CappedAt120Seconds_PlusJitter()
    {
        // At attempt 8+, delay should be capped at 120s + up to 1s jitter
        var delay = ReconnectionHelper.CalculateReconnectionDelay(20);
        delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(120));
        delay.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(121));
    }

    [Fact]
    public void CalculateReconnectionDelay_NeverExceedsTwoMinutesPlusJitter()
    {
        // Test many attempts — none should exceed 121s
        for (int i = 1; i <= 100; i++)
        {
            var delay = ReconnectionHelper.CalculateReconnectionDelay(i);
            delay.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(121),
                $"attempt {i} should not exceed 2 minutes + 1s jitter");
        }
    }

    // ── HubConnectionManagerFactory ───────────────────────────────────────

    [Fact]
    public void HubConnectionManagerFactory_Create_ReturnsNewInstance()
    {
        var factory = new HubConnectionManagerFactory(
            "http://localhost:9999", "test-agent", "test-key",
            new Mock<Serilog.ILogger>().Object);

        var instance1 = factory.Create();
        var instance2 = factory.Create();

        instance1.Should().NotBeNull();
        instance2.Should().NotBeNull();
        instance1.Should().NotBeSameAs(instance2);
    }

    [Fact]
    public void HubConnectionManagerFactory_Create_ProducesWorkingConnection()
    {
        var factory = new HubConnectionManagerFactory(
            "http://localhost:9999", "test-agent", "test-key",
            new Mock<Serilog.ILogger>().Object);

        var manager = factory.Create();

        manager.Connection.Should().NotBeNull();
        manager.IsConnected.Should().BeFalse(); // Not started yet
    }

    // ── HubConnectionManager event subscription ───────────────────────────

    [Fact]
    public void HubConnectionManager_OnClosed_EventCanBeSubscribed()
    {
        // Verify that the OnClosed event is exposed and can be subscribed to
        var logger = new Mock<Serilog.ILogger>();
        var manager = new HubConnectionManager(
            "http://localhost:9999", "test-agent", "test-key", logger.Object);

        Exception? receivedException = null;
        manager.OnClosed += error =>
        {
            receivedException = error;
            return Task.CompletedTask;
        };

        // We can't easily trigger Closed without a real server,
        // but we verify subscription compiles and the manager is in a valid state
        manager.IsConnected.Should().BeFalse();
    }
}
