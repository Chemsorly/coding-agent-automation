using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for HeartbeatMonitorService — validates sweep logic for stale heartbeats,
/// grace period handling, and disconnected agent cleanup.
/// </summary>
public class HeartbeatMonitorServiceTests
{
    private readonly AgentRegistryService _registry;
    private readonly OrchestratorRunService _runService;
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService;
    private readonly Mock<IProviderFactory> _mockProviderFactory;
    private readonly Mock<IConfigurationStore> _mockConfigStore;
    private readonly Mock<ILogger> _mockLogger;
    private readonly HeartbeatMonitorService _monitor;

    public HeartbeatMonitorServiceTests()
    {
        _mockLogger = new Mock<ILogger>();
        _registry = new AgentRegistryService(_mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);
        _mockHistoryService = new Mock<IPipelineRunHistoryService>();
        _mockProviderFactory = new Mock<IProviderFactory>();
        _mockConfigStore = new Mock<IConfigurationStore>();

        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        var dispatcher = new JobDispatcherService(_registry, _mockLogger.Object);

        _monitor = new HeartbeatMonitorService(
            _registry,
            _runService,
            _mockHistoryService.Object,
            dispatcher,
            _mockProviderFactory.Object,
            _mockConfigStore.Object,
            _mockConfigStore.Object,
            _mockLogger.Object);
    }

    [Fact]
    public async Task SweepAsync_NoAgents_CompletesWithoutError()
    {
        await _monitor.SweepAsync(CancellationToken.None);

        // No agents, no errors
        _registry.GetAllAgents().Should().BeEmpty();
    }

    [Fact]
    public async Task SweepAsync_FreshHeartbeat_AgentStaysIdle()
    {
        RegisterAgent("agent-1", "conn-1");

        await _monitor.SweepAsync(CancellationToken.None);

        var agent = _registry.GetByAgentId("agent-1");
        agent.Should().NotBeNull();
        agent!.Status.Should().Be(AgentStatus.Idle);
    }

    [Fact]
    public async Task SweepAsync_StaleHeartbeat_TransitionsToDisconnected()
    {
        var entry = RegisterAgent("agent-1", "conn-1");
        entry.LastHeartbeatAt = DateTimeOffset.UtcNow.AddSeconds(-100); // >90s stale

        await _monitor.SweepAsync(CancellationToken.None);

        var agent = _registry.GetByAgentId("agent-1");
        agent.Should().NotBeNull();
        agent!.Status.Should().Be(AgentStatus.Disconnected);
    }

    [Fact]
    public async Task SweepAsync_DisconnectedWithinGracePeriod_AgentRetained()
    {
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                AgentDisconnectGracePeriod = TimeSpan.FromMinutes(5)
            });

        var entry = RegisterAgent("agent-1", "conn-1");
        _registry.TransitionStatus("agent-1", AgentStatus.Disconnected);
        // DisconnectedAt is set to now by TransitionStatus — within grace period

        await _monitor.SweepAsync(CancellationToken.None);

        _registry.GetByAgentId("agent-1").Should().NotBeNull();
    }

    [Fact]
    public async Task SweepAsync_DisconnectedPastGracePeriod_NoActiveJob_Deregistered()
    {
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                AgentDisconnectGracePeriod = TimeSpan.Zero // Immediate expiry
            });

        var entry = RegisterAgent("agent-1", "conn-1");
        _registry.TransitionStatus("agent-1", AgentStatus.Disconnected);
        entry.DisconnectedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        await _monitor.SweepAsync(CancellationToken.None);

        _registry.GetByAgentId("agent-1").Should().BeNull();
    }

    [Fact]
    public async Task SweepAsync_DisconnectedPastGracePeriod_WithActiveJob_MarksRunFailed()
    {
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                AgentDisconnectGracePeriod = TimeSpan.Zero
            });
        _mockConfigStore
            .Setup(c => c.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());

        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-1";
        _registry.TransitionStatus("agent-1", AgentStatus.Disconnected);
        entry.DisconnectedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1"
        };
        _runService.AddRun(run);

        await _monitor.SweepAsync(CancellationToken.None);

        // Agent should be deregistered
        _registry.GetByAgentId("agent-1").Should().BeNull();

        // Run should be removed from active runs
        _runService.GetRun("job-1").Should().BeNull();

        // History should have been called
        _mockHistoryService.Verify(h => h.AddRunToHistory(It.Is<PipelineRun>(r =>
            r.RunId == "job-1" &&
            r.FailureReason == "Agent disconnected" &&
            r.CurrentStep == PipelineStep.Failed)), Times.Once);
    }

    [Fact]
    public async Task SweepAsync_BusyAgent_FreshHeartbeat_StaysBusy()
    {
        var entry = RegisterAgent("agent-1", "conn-1");
        entry.ActiveJobId = "job-1";
        _registry.TransitionStatus("agent-1", AgentStatus.Busy);

        await _monitor.SweepAsync(CancellationToken.None);

        var agent = _registry.GetByAgentId("agent-1");
        agent!.Status.Should().Be(AgentStatus.Busy);
    }

    [Fact]
    public async Task SweepAsync_MultipleAgents_HandlesEachIndependently()
    {
        var fresh = RegisterAgent("agent-fresh", "conn-1");
        var stale = RegisterAgent("agent-stale", "conn-2");
        stale.LastHeartbeatAt = DateTimeOffset.UtcNow.AddSeconds(-100);

        await _monitor.SweepAsync(CancellationToken.None);

        _registry.GetByAgentId("agent-fresh")!.Status.Should().Be(AgentStatus.Idle);
        _registry.GetByAgentId("agent-stale")!.Status.Should().Be(AgentStatus.Disconnected);
    }

    [Fact]
    public async Task SweepAsync_DisconnectedAgent_NullDisconnectedAt_Skipped()
    {
        _mockConfigStore
            .Setup(c => c.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                AgentDisconnectGracePeriod = TimeSpan.Zero
            });

        var entry = RegisterAgent("agent-1", "conn-1");
        _registry.TransitionStatus("agent-1", AgentStatus.Disconnected);
        // Manually clear DisconnectedAt to test the null check
        entry.DisconnectedAt = null;

        await _monitor.SweepAsync(CancellationToken.None);

        // Agent should still be in registry (null DisconnectedAt is skipped)
        _registry.GetByAgentId("agent-1").Should().NotBeNull();
    }

    private AgentEntry RegisterAgent(string agentId, string connectionId)
    {
        return _registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = $"host-{agentId}",
            AgentType = "kiro-dotnet",
            Labels = new[] { "dotnet" }
        }, connectionId);
    }
}
