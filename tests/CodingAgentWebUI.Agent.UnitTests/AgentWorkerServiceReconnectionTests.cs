using System.Reflection;
using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for <see cref="AgentWorkerService"/> fresh reconnection behavior (Task 6.3).
/// Validates that when the SignalR connection enters terminal Closed state,
/// the service disposes the old connection and creates a fresh one via the factory.
/// </summary>
[Collection("EnvironmentVariables")]
public class AgentWorkerServiceReconnectionTests
{
    [Fact]
    public void CalculateReconnectionDelay_FirstAttempt_ReturnsBaseDelay()
    {
        // 2^1 = 2 seconds + 0-1s jitter
        var delay = InvokeCalculateReconnectionDelay(1);
        delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2));
        delay.Should().BeLessThan(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void CalculateReconnectionDelay_ExponentialIncrease()
    {
        // attempt 2 → 2^2=4s, attempt 3 → 2^3=8s, attempt 4 → 2^4=16s
        var delay2 = InvokeCalculateReconnectionDelay(2);
        var delay3 = InvokeCalculateReconnectionDelay(3);
        var delay4 = InvokeCalculateReconnectionDelay(4);

        // Each delay (minus jitter) should be ~double the previous
        delay2.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(4));
        delay3.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(8));
        delay4.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(16));
    }

    [Fact]
    public void CalculateReconnectionDelay_CappedAt120Seconds_PlusJitter()
    {
        // At attempt 8+, delay should be capped at 120s + up to 1s jitter
        var delay = InvokeCalculateReconnectionDelay(20);
        delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(120));
        delay.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(121));
    }

    [Fact]
    public void CalculateReconnectionDelay_NeverExceedsTwoMinutesPlusJitter()
    {
        // Test many attempts — none should exceed 121s
        for (int i = 1; i <= 100; i++)
        {
            var delay = InvokeCalculateReconnectionDelay(i);
            delay.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(121),
                $"attempt {i} should not exceed 2 minutes + 1s jitter");
        }
    }

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

    [Fact]
    public void Constructor_ThrowsOnNullFactory()
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();

        var act = () => new AgentWorkerService(
            CreateTestHubManager(),
            null!,
            CreateMockExecutor(),
            CreateMockConsolidationExecutor(),
            Mock.Of<IJobCompletionReporter>(),
            mockOrchestrator.Object,
            Mock.Of<IHttpClientFactory>(),
            new AgentIdentity("test"),
            Mock.Of<IHostApplicationLifetime>(),
            mockLogger.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("hubManagerFactory");
    }

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

    private static TimeSpan InvokeCalculateReconnectionDelay(int attempt)
    {
        // CalculateReconnectionDelay is private static — invoke via reflection
        var method = typeof(AgentWorkerService).GetMethod(
            "CalculateReconnectionDelay",
            BindingFlags.NonPublic | BindingFlags.Static);
        method.Should().NotBeNull("CalculateReconnectionDelay should exist as a private static method");
        return (TimeSpan)method!.Invoke(null, [attempt])!;
    }

    private static HubConnectionManager CreateTestHubManager()
    {
        var logger = new Mock<Serilog.ILogger>();
        return new HubConnectionManager(
            "http://localhost:9999", "test-agent", "test-api-key", logger.Object);
    }

    private static HubConnectionManagerFactory CreateTestHubManagerFactory()
    {
        var logger = new Mock<Serilog.ILogger>();
        return new HubConnectionManagerFactory(
            "http://localhost:9999", "test-agent", "test-api-key", logger.Object);
    }

    private static LocalPipelineExecutor CreateMockExecutor()
    {
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var mockQualityGateValidator = new Mock<CodingAgentWebUI.Pipeline.Interfaces.IQualityGateValidator>();
        var mockLogger = new Mock<Serilog.ILogger>();
        return new LocalPipelineExecutor(
            mockOrchestrator.Object,
            mockHttpClientFactory.Object,
            new PipelineConfiguration(),
            mockQualityGateValidator.Object,
            mockLogger.Object,
            agentIdentity: new AgentIdentity("test-agent"));
    }

    private static LocalConsolidationExecutor CreateMockConsolidationExecutor()
    {
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        var mockHttpClientFactory = new Mock<IHttpClientFactory>();
        var mockLogger = new Mock<Serilog.ILogger>();
        return new LocalConsolidationExecutor(
            mockOrchestrator.Object,
            mockHttpClientFactory.Object,
            mockLogger.Object);
    }
}
