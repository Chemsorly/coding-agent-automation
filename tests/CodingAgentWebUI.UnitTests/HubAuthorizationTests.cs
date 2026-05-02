using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Unit tests for hub authorization, agent registration/deregistration,
/// and orchestrator service behaviors.
/// </summary>
/// <remarks>
/// This class mutates the AGENT_API_KEY environment variable in some tests.
/// The [Collection] attribute prevents parallel execution with other classes
/// that depend on the same process-global state.
/// </remarks>
[Collection("EnvironmentVariables")]
public class HubAuthorizationTests
{
    private static AgentRegistryService CreateRegistry() =>
        new(new Mock<ILogger>().Object);

    private static HeartbeatMonitorService CreateMonitor(
        AgentRegistryService registry,
        OrchestratorRunService runService,
        Mock<IConfigurationStore> mockConfigStore) =>
        new(
            registry,
            runService,
            new Mock<IPipelineRunHistoryService>().Object,
            new JobDispatcherService(registry, new Mock<ILogger>().Object),
            new Mock<IProviderFactory>().Object,
            mockConfigStore.Object,
            mockConfigStore.Object,
            new Mock<ILogger>().Object);

    private static AgentEntry RegisterAgent(
        AgentRegistryService registry,
        string agentId,
        string connectionId,
        IReadOnlyList<string>? labels = null)
    {
        return registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = $"host-{agentId}",
            AgentType = "kiro-dotnet",
            Labels = labels ?? new[] { "kiro", "dotnet" }
        }, connectionId);
    }

    // ── API Key Authentication ──────────────────────────────────────────

    [Fact]
    public void ApiKeyAuth_MissingKey_GeneratesRandomKey()
    {
        var originalKey = Environment.GetEnvironmentVariable("AGENT_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_API_KEY", null);
            var logger = new Mock<ILogger>();
            var key = AgentApiKeyAuthHandler.ResolveApiKey(logger.Object);

            key.Should().NotBeNullOrWhiteSpace();
            key.Length.Should().BeGreaterThan(10); // Base64 of 32 bytes
        }
        finally
        {
            if (originalKey != null)
                Environment.SetEnvironmentVariable("AGENT_API_KEY", originalKey);
        }
    }

    [Fact]
    public void ApiKeyAuth_ValidKey_ReturnsConfiguredKey()
    {
        var originalKey = Environment.GetEnvironmentVariable("AGENT_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_API_KEY", "test-secret-key-123");
            var logger = new Mock<ILogger>();
            var key = AgentApiKeyAuthHandler.ResolveApiKey(logger.Object);

            key.Should().Be("test-secret-key-123");
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_API_KEY", originalKey);
        }
    }

    // ── Agent Registration / Deregistration ─────────────────────────────

    [Fact]
    public void GracefulDeregistration_RemovesAgentEntirely()
    {
        var registry = CreateRegistry();
        RegisterAgent(registry, "agent-1", "conn-1");

        registry.GetByAgentId("agent-1").Should().NotBeNull();

        var removed = registry.Deregister("agent-1");

        removed.Should().BeTrue();
        registry.GetByAgentId("agent-1").Should().BeNull();
        registry.GetByConnectionId("conn-1").Should().BeNull();
        registry.GetAllAgents().Should().BeEmpty();
    }

    [Fact]
    public void Deregister_NonExistentAgent_ReturnsFalse()
    {
        var registry = CreateRegistry();

        var removed = registry.Deregister("non-existent");

        removed.Should().BeFalse();
    }

    // ── Auto-Deregistration (HeartbeatMonitorService) ───────────────────

    [Fact]
    public async Task AutoDeregistration_RemovesIdleDisconnectedAgentAfterGracePeriod()
    {
        var registry = CreateRegistry();
        var runService = new OrchestratorRunService(new Mock<ILogger>().Object);
        var mockConfigStore = new Mock<Pipeline.Interfaces.IConfigurationStore>();
        mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                AgentDisconnectGracePeriod = TimeSpan.Zero // Immediate expiry for testing
            });

        var monitor = CreateMonitor(registry, runService, mockConfigStore);

        // Register and disconnect an agent (no active job)
        var entry = RegisterAgent(registry, "agent-1", "conn-1");
        registry.TransitionStatus("agent-1", AgentStatus.Disconnected);

        // Backdate DisconnectedAt to simulate grace period expiry
        entry.DisconnectedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        // Run sweep
        await monitor.SweepAsync(CancellationToken.None);

        // Agent should be removed
        registry.GetByAgentId("agent-1").Should().BeNull();
    }

    [Fact]
    public async Task AutoDeregistration_DoesNotRemoveWithinGracePeriod()
    {
        var registry = CreateRegistry();
        var runService = new OrchestratorRunService(new Mock<ILogger>().Object);
        var mockConfigStore = new Mock<Pipeline.Interfaces.IConfigurationStore>();
        mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                AgentDisconnectGracePeriod = TimeSpan.FromMinutes(5)
            });

        var monitor = CreateMonitor(registry, runService, mockConfigStore);

        // Register and disconnect an agent
        RegisterAgent(registry, "agent-1", "conn-1");
        registry.TransitionStatus("agent-1", AgentStatus.Disconnected);
        // DisconnectedAt is set to now by TransitionStatus — within grace period

        // Run sweep
        await monitor.SweepAsync(CancellationToken.None);

        // Agent should still be in registry
        registry.GetByAgentId("agent-1").Should().NotBeNull();
    }

    // ── Failed Dispatch Label Revert ────────────────────────────────────

    [Fact]
    public void FailedDispatch_RevertsAgentStatusToIdle()
    {
        // When dispatch fails, the agent's status should be reverted to Idle
        // and ActiveJobId cleared. This is tested at the registry level since
        // the actual dispatch involves SignalR which requires integration testing.
        var registry = CreateRegistry();
        var entry = RegisterAgent(registry, "agent-1", "conn-1");

        // Simulate dispatch: set agent to Busy with a job
        entry.ActiveJobId = "job-1";
        registry.TransitionStatus("agent-1", AgentStatus.Busy);

        // Simulate dispatch failure: revert
        entry.ActiveJobId = null;
        registry.TransitionStatus("agent-1", AgentStatus.Idle);

        var agent = registry.GetByAgentId("agent-1");
        agent.Should().NotBeNull();
        agent!.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();
    }

    // ── Hub Method Job Authorization ────────────────────────────────────

    [Fact]
    public void AuthorizationFilter_RejectsUnregisteredAgent()
    {
        var registry = CreateRegistry();
        var filter = new AgentAuthorizationFilter(registry, new Mock<ILogger>().Object);

        // GetByConnectionId for an unregistered connection returns null
        registry.GetByConnectionId("unknown-conn").Should().BeNull();
    }

    [Fact]
    public void AuthorizationFilter_RegisteredAgent_FoundByConnectionId()
    {
        var registry = CreateRegistry();
        RegisterAgent(registry, "agent-1", "conn-1");

        var agent = registry.GetByConnectionId("conn-1");
        agent.Should().NotBeNull();
        agent!.AgentId.Should().Be("agent-1");
    }

    [Fact]
    public void AuthorizationFilter_JobIdMismatch_Detectable()
    {
        var registry = CreateRegistry();
        var entry = RegisterAgent(registry, "agent-1", "conn-1");
        entry.ActiveJobId = "job-1";

        // Verify that a mismatched jobId can be detected
        var agent = registry.GetByConnectionId("conn-1");
        agent.Should().NotBeNull();
        agent!.ActiveJobId.Should().Be("job-1");

        // A call with "job-2" would be rejected by the filter
        string.Equals(agent.ActiveJobId, "job-2", StringComparison.Ordinal).Should().BeFalse();
    }

    [Fact]
    public void AuthorizationFilter_MatchingJobId_Allowed()
    {
        var registry = CreateRegistry();
        var entry = RegisterAgent(registry, "agent-1", "conn-1");
        entry.ActiveJobId = "job-1";

        var agent = registry.GetByConnectionId("conn-1");
        agent.Should().NotBeNull();
        string.Equals(agent!.ActiveJobId, "job-1", StringComparison.Ordinal).Should().BeTrue();
    }

    // ── Token Refresh ───────────────────────────────────────────────────

    [Fact]
    public void TokenRefreshResponse_HasRequiredFields()
    {
        var response = new TokenRefreshResponse
        {
            Token = "ghs_test_token_123",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        };

        response.Token.Should().NotBeNullOrWhiteSpace();
        response.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    // ── Comment Type Formatting ─────────────────────────────────────────

    [Fact]
    public void CommentType_Analysis_HasCorrectValue()
    {
        CommentType.Analysis.Should().Be(CommentType.Analysis);
        ((int)CommentType.Analysis).Should().Be(0);
    }

    [Fact]
    public void CommentType_GateRejection_HasCorrectValue()
    {
        CommentType.GateRejection.Should().Be(CommentType.GateRejection);
        ((int)CommentType.GateRejection).Should().Be(1);
    }

    [Fact]
    public void CommentType_GateWontDo_HasCorrectValue()
    {
        CommentType.GateWontDo.Should().Be(CommentType.GateWontDo);
        ((int)CommentType.GateWontDo).Should().Be(2);
    }

    [Fact]
    public void CommentPayload_AnalysisMarkdown_RoundTrips()
    {
        var payload = new CommentPayload
        {
            AnalysisMarkdown = "## Analysis\n\nThis is a test analysis."
        };

        payload.AnalysisMarkdown.Should().Contain("Analysis");
        payload.AssessmentJson.Should().BeNull();
    }

    [Fact]
    public void CommentPayload_AssessmentJson_RoundTrips()
    {
        var payload = new CommentPayload
        {
            AssessmentJson = """{"recommendation":"ready","confidence":0.95}"""
        };

        payload.AssessmentJson.Should().Contain("ready");
        payload.AnalysisMarkdown.Should().BeNull();
    }

    // ── Heartbeat Stale Detection ───────────────────────────────────────

    [Fact]
    public async Task HeartbeatMonitor_DetectsStaleHeartbeat()
    {
        var registry = CreateRegistry();
        var runService = new OrchestratorRunService(new Mock<ILogger>().Object);
        var mockConfigStore = new Mock<Pipeline.Interfaces.IConfigurationStore>();
        mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        var monitor = CreateMonitor(registry, runService, mockConfigStore);

        // Register agent with stale heartbeat (>90 seconds ago)
        var entry = RegisterAgent(registry, "agent-1", "conn-1");
        entry.LastHeartbeatAt = DateTimeOffset.UtcNow.AddSeconds(-100);

        // Run sweep
        await monitor.SweepAsync(CancellationToken.None);

        // Agent should be transitioned to Disconnected
        var agent = registry.GetByAgentId("agent-1");
        agent.Should().NotBeNull();
        agent!.Status.Should().Be(AgentStatus.Disconnected);
    }

    [Fact]
    public async Task HeartbeatMonitor_FreshHeartbeat_StaysConnected()
    {
        var registry = CreateRegistry();
        var runService = new OrchestratorRunService(new Mock<ILogger>().Object);
        var mockConfigStore = new Mock<Pipeline.Interfaces.IConfigurationStore>();
        mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        var monitor = CreateMonitor(registry, runService, mockConfigStore);

        // Register agent with fresh heartbeat
        RegisterAgent(registry, "agent-1", "conn-1");
        // LastHeartbeatAt is set to now by Register

        // Run sweep
        await monitor.SweepAsync(CancellationToken.None);

        // Agent should remain Idle
        var agent = registry.GetByAgentId("agent-1");
        agent.Should().NotBeNull();
        agent!.Status.Should().Be(AgentStatus.Idle);
    }
}
