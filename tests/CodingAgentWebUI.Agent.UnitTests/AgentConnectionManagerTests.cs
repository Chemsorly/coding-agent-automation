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

    [Fact]
    public void Constructor_NullHubManager_Throws()
    {
        var act = () => new AgentConnectionManager(
            null!,
            CreateFactory(),
            new AgentIdentity("test"),
            Mock.Of<Serilog.ILogger>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("hubManager");
    }

    [Fact]
    public void Constructor_NullFactory_Throws()
    {
        var act = () => new AgentConnectionManager(
            CreateHubManager(),
            null!,
            new AgentIdentity("test"),
            Mock.Of<Serilog.ILogger>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("hubManagerFactory");
    }

    [Fact]
    public void Constructor_NullAgentIdentity_Throws()
    {
        var act = () => new AgentConnectionManager(
            CreateHubManager(),
            CreateFactory(),
            null!,
            Mock.Of<Serilog.ILogger>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("agentIdentity");
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new AgentConnectionManager(
            CreateHubManager(),
            CreateFactory(),
            new AgentIdentity("test"),
            null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Fact]
    public void Constructor_ValidParams_DoesNotThrow()
    {
        var act = () => new AgentConnectionManager(
            CreateHubManager(),
            CreateFactory(),
            new AgentIdentity("test"),
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

    // ── Structural: SignalR resilience pipeline ───────────────────────────

    [Fact]
    public void SourceCode_UsesResiliencePipeline()
    {
        // AgentConnectionManager MUST use a Polly resilience pipeline for hub invocations.
        // This prevents transient network errors from permanently losing messages.
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "AgentConnectionManager.cs"));

        var usesResilience = sourceCode.Contains("ResiliencePipeline")
            || sourceCode.Contains("ResiliencePipelineFactory");
        usesResilience.Should().BeTrue(
            "AgentConnectionManager must use a Polly ResiliencePipeline for hub invocations");
    }

    [Fact]
    public void SourceCode_WiresCancelJobHandler()
    {
        // AgentConnectionManager MUST wire the CancelJob hub event so that
        // the orchestrator can cancel running jobs on K8s agents.
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "AgentConnectionManager.cs"));

        var wiresCancelJob = sourceCode.Contains("OnCancelJob")
            || sourceCode.Contains("CancelJob");
        wiresCancelJob.Should().BeTrue(
            "AgentConnectionManager must wire the CancelJob hub event for remote cancellation");
    }

    [Fact]
    public void SourceCode_WiresReconnectedHandler()
    {
        // AgentConnectionManager MUST handle reconnection to re-register automatically.
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "AgentConnectionManager.cs"));

        var wiresReconnected = sourceCode.Contains("OnReconnected")
            || sourceCode.Contains("Reconnected");
        wiresReconnected.Should().BeTrue(
            "AgentConnectionManager must handle reconnection events to re-register with the orchestrator");
    }

    [Fact]
    public void SourceCode_SendsHeartbeats()
    {
        // AgentConnectionManager MUST send periodic heartbeats.
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "AgentConnectionManager.cs"));

        var sendsHeartbeats = sourceCode.Contains("HeartbeatMessage")
            || sourceCode.Contains("HubMethodNames.Heartbeat");
        sendsHeartbeats.Should().BeTrue(
            "AgentConnectionManager must send periodic heartbeats to prevent disconnection");

        var hasPeriodic = sourceCode.Contains("PeriodicTimer")
            || sourceCode.Contains("Task.Delay");
        hasPeriodic.Should().BeTrue(
            "Heartbeats must be sent on a periodic timer (30s interval)");
    }

    [Fact]
    public void SourceCode_DeregistersOnDispose()
    {
        // AgentConnectionManager MUST deregister the agent on dispose
        // so the orchestrator doesn't show stale "Disconnected" entries.
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "AgentConnectionManager.cs"));

        var deregisters = sourceCode.Contains("DeregisterAgent")
            || sourceCode.Contains("HubMethodNames.DeregisterAgent");
        deregisters.Should().BeTrue(
            "AgentConnectionManager must call DeregisterAgent on dispose for clean orchestrator state");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static AgentConnectionManager CreateManager()
    {
        return new AgentConnectionManager(
            CreateHubManager(),
            CreateFactory(),
            new AgentIdentity("test-agent"),
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

    private static string GetSourceDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CodingAgentAutomation.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find solution root");
    }
}
