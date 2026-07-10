using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using ILogger = Serilog.ILogger;
using Moq;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Tests for <see cref="SignalRWorkDistributorAgentResolver"/>, covering the thread-safe
/// <see cref="ISignalRWorkDistributorAgentResolver.ResolveAgent"/>,
/// <see cref="ISignalRWorkDistributorAgentResolver.ReleaseAgent"/>, and
/// <see cref="ISignalRWorkDistributorAgentResolver.AssignJob"/> methods.
/// </summary>
public class SignalRWorkDistributorAgentResolverTests
{
    private readonly AgentRegistryService _registry;
    private readonly JobDispatcherService _dispatcher;
    private readonly SignalRWorkDistributorAgentResolver _resolver;

    public SignalRWorkDistributorAgentResolverTests()
    {
        var logger = new Mock<ILogger>().Object;
        _registry = new AgentRegistryService(logger);
        _dispatcher = new JobDispatcherService(_registry, logger);
        _resolver = new SignalRWorkDistributorAgentResolver(_registry, _dispatcher);
    }

    [Fact]
    public void ResolveAgent_WithIdleAgent_ReturnsResultAndMarksAgentBusy()
    {
        // Arrange: register an idle agent
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Labels = ["dotnet"],
            Hostname = "host-1"
        }, "conn-abc");

        // Act
        var result = _resolver.ResolveAgent("dotnet");

        // Assert
        result.Should().NotBeNull();
        result!.ConnectionId.Should().Be("conn-abc");
        result.AgentId.Should().Be("agent-1");
        var agent = _registry.GetByAgentId("agent-1");
        agent!.Status.Should().Be(AgentStatus.Busy);
    }

    [Fact]
    public void ResolveAgent_NoMatchingAgent_ReturnsNull()
    {
        // Arrange: agent with different labels
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-2",
            Labels = ["java"],
            Hostname = "host-2"
        }, "conn-def");

        // Act
        var result = _resolver.ResolveAgent("dotnet");

        // Assert
        result.Should().BeNull();
    }

    // TODO: Add idempotency test — ReleaseAgent called twice on the same agent should be a no-op
    //       on the second call (agent already Idle). Verifies no spurious Idle→Idle transition issues.

    [Fact]
    public void ReleaseAgent_RevertsAgentToIdle()
    {
        // Arrange: register and resolve (marks Busy)
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-3",
            Labels = ["kiro"],
            Hostname = "host-3"
        }, "conn-ghi");
        var result = _resolver.ResolveAgent("kiro");
        result.Should().NotBeNull();

        var agent = _registry.GetByAgentId("agent-3");
        agent!.Status.Should().Be(AgentStatus.Busy); // precondition

        // Act
        _resolver.ReleaseAgent(result!.AgentId);

        // Assert
        agent.Status.Should().Be(AgentStatus.Idle);
    }

    [Fact]
    public void ReleaseAgent_ClearsActiveJobId()
    {
        // Arrange: register, resolve, and assign a job
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-4",
            Labels = ["dotnet"],
            Hostname = "host-4"
        }, "conn-jkl");
        var result = _resolver.ResolveAgent("dotnet");
        result.Should().NotBeNull();
        _resolver.AssignJob(result!.AgentId, "run-999");

        var agent = _registry.GetByAgentId("agent-4");
        agent!.ActiveJobId.Should().Be("run-999"); // precondition

        // Act
        _resolver.ReleaseAgent(result.AgentId);

        // Assert
        agent.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public void AssignJob_SetsActiveJobIdOnAgentEntry()
    {
        // Arrange: register an idle agent, resolve to mark Busy
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-5",
            Labels = ["dotnet"],
            Hostname = "host-5"
        }, "conn-mno");
        _resolver.ResolveAgent("dotnet");

        var agent = _registry.GetByAgentId("agent-5");
        agent!.Status.Should().Be(AgentStatus.Busy);
        agent.ActiveJobId.Should().BeNull(); // precondition: not set yet

        // Act
        _resolver.AssignJob("agent-5", "run-123");

        // Assert
        agent.ActiveJobId.Should().Be("run-123");
    }

    [Fact]
    public void AssignJob_UnknownAgent_DoesNotThrow()
    {
        var act = () => _resolver.AssignJob("nonexistent-agent", "run-456");
        act.Should().NotThrow();
    }

    [Fact]
    public async Task ConcurrentDispatch_TenSimultaneousResolves_NoAgentLeakedInBusyState()
    {
        // Arrange: register 10 idle agents
        const int agentCount = 10;
        for (var i = 0; i < agentCount; i++)
        {
            _registry.Register(new AgentRegistrationMessage
            {
                AgentId = $"agent-concurrent-{i}",
                Labels = ["dotnet"],
                Hostname = $"host-concurrent-{i}"
            }, $"conn-concurrent-{i}");
        }

        // Two barriers: one to synchronize resolve, one to synchronize release.
        // This ensures all agents are reserved (Busy) before any are released,
        // proving that each thread gets a unique agent and no agent is leaked.
        using var resolveBarrier = new Barrier(agentCount);
        using var releaseBarrier = new Barrier(agentCount);
        var results = new AgentResolveResult?[agentCount];

        // Act: 10 parallel tasks each resolve an agent, wait for all to resolve, then release
        var tasks = Enumerable.Range(0, agentCount).Select(i => Task.Run(() =>
        {
            resolveBarrier.SignalAndWait(); // ensure all threads start ResolveAgent at the same time
            var result = _resolver.ResolveAgent("dotnet");
            results[i] = result;

            releaseBarrier.SignalAndWait(); // wait for all threads to finish resolving before releasing

            // Simulate dispatch failure — release the agent we resolved
            if (result is not null)
            {
                _resolver.ReleaseAgent(result.AgentId);
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // Assert: all 10 agents resolved successfully (no null results)
        results.Should().NotContainNulls();

        // Assert: all 10 agents resolved to distinct agent IDs (no double-dispatch)
        var resolvedIds = results.Select(r => r!.AgentId).Distinct().ToList();
        resolvedIds.Should().HaveCount(agentCount);

        // Assert: no agent is stuck in Busy state — all are back to Idle
        for (var i = 0; i < agentCount; i++)
        {
            var agent = _registry.GetByAgentId($"agent-concurrent-{i}");
            agent!.Status.Should().Be(AgentStatus.Idle,
                $"agent-concurrent-{i} should be Idle after release, not stuck in Busy");
            agent.ActiveJobId.Should().BeNull(
                $"agent-concurrent-{i} should have no active job after release");
        }
    }
}
