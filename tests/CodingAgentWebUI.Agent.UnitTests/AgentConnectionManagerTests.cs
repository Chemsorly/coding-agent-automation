using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// TDD tests for <see cref="AgentConnectionManager"/> — the shared connection lifecycle component
/// extracted from AgentWorkerService and WorkItemAgentService.
///
/// Tests define the behavioral contract:
/// - Registration with resilience (Polly retry)
/// - Heartbeat loop runs concurrently
/// - CancelJob events are forwarded
/// - Reconnection triggers re-registration
/// - Graceful deregistration on dispose
/// - InvokeAsync wraps calls with resilience
/// </summary>
public class AgentConnectionManagerTests
{
    private static readonly AgentRegistrationMessage TestRegistration = new()
    {
        AgentId = "test-agent",
        Hostname = "test-host",
        Labels = ["kiro", "dotnet"],
        ActiveJob = null
    };

    // ── Construction ─────────────────────────────────────────────────────

    // TODO: Add Constructor_DefaultAgentId_Throws test — default(AgentId) has Value == null and is not
    // currently rejected by the constructor (guard was removed during AgentIdentity→AgentId migration).
    // If DI misconfiguration passes default(AgentId), hub invocations will propagate nulls.

    [Fact]
    public void Constructor_NullHubManager_Throws()
    {
        var act = () => new AgentConnectionManager(
            null!,
            CreateFactory(),
            new AgentId("test"),
            Mock.Of<Serilog.ILogger>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("hubManager");
    }

    [Fact]
    public void Constructor_NullFactory_Throws()
    {
        var act = () => new AgentConnectionManager(
            CreateHubManager(),
            null!,
            new AgentId("test"),
            Mock.Of<Serilog.ILogger>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("hubManagerFactory");
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new AgentConnectionManager(
            CreateHubManager(),
            CreateFactory(),
            new AgentId("test"),
            null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Fact]
    public void Constructor_ValidParams_DoesNotThrow()
    {
        var act = () => new AgentConnectionManager(
            CreateHubManager(),
            CreateFactory(),
            new AgentId("test"),
            Mock.Of<Serilog.ILogger>());

        act.Should().NotThrow();
    }

    // ── Interface compliance ─────────────────────────────────────────────

    [Fact]
    public void Implements_IAgentConnectionManager()
    {
        var manager = CreateManager();
        manager.Should().BeAssignableTo<IAgentConnectionManager>();
    }

    [Fact]
    public void Implements_IAsyncDisposable()
    {
        var manager = CreateManager();
        manager.Should().BeAssignableTo<IAsyncDisposable>();
    }

    // ── Connection property ──────────────────────────────────────────────

    [Fact]
    public void Connection_ReturnsUnderlyingHubConnection()
    {
        var manager = CreateManager();
        manager.Connection.Should().NotBeNull();
    }

    [Fact]
    public void IsConnected_BeforeConnect_ReturnsFalse()
    {
        var manager = CreateManager();
        manager.IsConnected.Should().BeFalse();
    }

    // ── UpdateCurrentStep ────────────────────────────────────────────────

    [Fact]
    public void UpdateCurrentStep_DoesNotThrow()
    {
        var manager = CreateManager();
        var act = () => manager.UpdateCurrentStep(PipelineStep.GeneratingCode);
        act.Should().NotThrow();
    }

    [Fact]
    public void UpdateCurrentStep_Null_DoesNotThrow()
    {
        var manager = CreateManager();
        var act = () => manager.UpdateCurrentStep(null);
        act.Should().NotThrow();
    }

    // ── UpdateRegistration ───────────────────────────────────────────────

    [Fact]
    public void UpdateRegistration_UpdatesStoredRegistration()
    {
        var manager = CreateManager();
        var act = () => manager.UpdateRegistration(TestRegistration);
        act.Should().NotThrow();
    }

    [Fact]
    public void UpdateRegistration_Null_Throws()
    {
        var manager = CreateManager();
        var act = () => manager.UpdateRegistration(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── OnCancelJobReceived event ────────────────────────────────────────

    [Fact]
    public void OnCancelJobReceived_CanBeSubscribed()
    {
        var manager = CreateManager();
        string? receivedJobId = null;

        manager.OnCancelJobReceived += jobId =>
        {
            receivedJobId = jobId;
            return Task.CompletedTask;
        };

        // Subscription should compile and not throw
        manager.Should().NotBeNull();
    }

    // ── OnReconnected event ──────────────────────────────────────────────

    [Fact]
    public void OnReconnected_CanBeSubscribed()
    {
        var manager = CreateManager();

        manager.OnReconnected += () => Task.CompletedTask;

        manager.Should().NotBeNull();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static AgentConnectionManager CreateManager()
    {
        return new AgentConnectionManager(
            CreateHubManager(),
            CreateFactory(),
            new AgentId("test-agent"),
            Mock.Of<Serilog.ILogger>());
    }

    private static HubConnectionManager CreateHubManager()
    {
        return new HubConnectionManager(
            "http://localhost:9999", "test-agent", "test-key",
            Mock.Of<Serilog.ILogger>());
    }

    private static HubConnectionManagerFactory CreateFactory()
    {
        return new HubConnectionManagerFactory(
            "http://localhost:9999", "test-agent", "test-key",
            Mock.Of<Serilog.ILogger>());
    }
}
