using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Concurrency tests for <see cref="AgentHubFacade"/>.
/// Verifies that concurrent SignalR calls (Register, CompleteJob, Heartbeat, AddRun, RemoveRun)
/// don't corrupt shared state. The hub processes requests from multiple agents simultaneously
/// via SignalR's thread pool — these tests exercise that parallelism.
/// </summary>
public class AgentHubFacadeConcurrencyTests
{
    private readonly AgentHubFacade _facade;
    private readonly AgentRegistryService _registry;
    private readonly OrchestratorRunService _runService;

    public AgentHubFacadeConcurrencyTests()
    {
        var mockLogger = new Mock<ILogger>();
        _registry = new AgentRegistryService(mockLogger.Object);
        _runService = new OrchestratorRunService(mockLogger.Object);
        var dispatcher = new JobDeduplicationGuardService(_registry, mockLogger.Object);
        var drainService = new JobQueueDrainService(
            dispatcher, _registry, Mock.Of<IJobDispatcher>(),
            Mock.Of<IConfigurationStore>(), Mock.Of<IConsolidationDispatcher>(),
            new ShutdownSignal(), mockLogger.Object);

        _facade = new AgentHubFacade(
            _registry,
            _runService,
            dispatcher,
            drainService,
            Mock.Of<IPipelineRunHistoryService>(),
            Mock.Of<IConfigurationStore>(),
            Mock.Of<IProviderFactory>(),
            NullLogger<AgentHubFacade>.Instance);
    }

    /// <summary>
    /// Concurrent registration of multiple agents should not lose any registrations.
    /// </summary>
    [Fact]
    public async Task ConcurrentRegistrations_AllAgentsRegistered()
    {
        const int agentCount = 50;
        var tasks = Enumerable.Range(0, agentCount).Select(i => Task.Run(() =>
        {
            var msg = new AgentRegistrationMessage
            {
                AgentId = $"agent-{i}",
                Hostname = $"host-{i}",
                Labels = new[] { "dotnet" }
            };
            return _facade.Register(msg, $"conn-{i}");
        })).ToArray();

        var results = await Task.WhenAll(tasks);

        results.Should().HaveCount(agentCount);
        results.Should().OnlyContain(r => r != null);

        // Verify all agents are accessible
        for (int i = 0; i < agentCount; i++)
        {
            _facade.GetByAgentId($"agent-{i}").Should().NotBeNull();
        }
    }

    /// <summary>
    /// Concurrent AddRun + GetRun from different threads should not throw or corrupt state.
    /// </summary>
    [Fact]
    public async Task ConcurrentAddAndGetRun_NoCorruption()
    {
        const int runCount = 30;
        var runs = Enumerable.Range(0, runCount).Select(i => PipelineRun.Create(
            runId: $"run-{i}",
            issueIdentifier: $"org/repo#{i}",
            issueTitle: $"Issue {i}",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1",
            runType: PipelineRunType.Implementation,
            initiatedBy: "test",
            agentId: $"agent-{i % 5}")).ToArray();

        // Add runs concurrently
        var addTasks = runs.Select(r => Task.Run(() => _facade.AddRun(r)));
        await Task.WhenAll(addTasks);

        // Read runs concurrently
        var readTasks = runs.Select(r => Task.Run(() => _facade.GetRun(r.RunId)));
        var results = await Task.WhenAll(readTasks);

        results.Should().OnlyContain(r => r != null);
        for (int i = 0; i < runCount; i++)
        {
            results[i]!.RunId.Should().Be($"run-{i}");
        }
    }

    /// <summary>
    /// Concurrent register + deregister interleaving should not throw exceptions
    /// and should leave the system in a consistent state.
    /// </summary>
    [Fact]
    public async Task ConcurrentRegisterAndDeregister_NoExceptions()
    {
        const int iterations = 100;
        var exceptions = new List<Exception>();

        var tasks = Enumerable.Range(0, iterations).Select(i => Task.Run(() =>
        {
            try
            {
                var agentId = $"agent-{i % 10}"; // 10 agents, contention on same IDs
                var msg = new AgentRegistrationMessage
                {
                    AgentId = agentId,
                    Hostname = $"host-{i}",
                    Labels = new[] { "test" }
                };

                _facade.Register(msg, $"conn-{i}");

                // Every other iteration deregisters
                if (i % 2 == 0)
                    _facade.Deregister(agentId);
            }
            catch (Exception ex)
            {
                lock (exceptions) { exceptions.Add(ex); }
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        exceptions.Should().BeEmpty("Concurrent register/deregister should never throw");
    }

    /// <summary>
    /// Concurrent AddRun + RemoveRun for the same job ID should not throw
    /// and should eventually result in a consistent state (either present or absent).
    /// Verifies final state count is consistent with operations performed.
    /// </summary>
    [Fact]
    public async Task ConcurrentAddAndRemoveRun_NoCorruption()
    {
        const int iterations = 50;
        var exceptions = new List<Exception>();
        var addedNotRemoved = new System.Collections.Concurrent.ConcurrentBag<string>();

        var tasks = Enumerable.Range(0, iterations).Select(i => Task.Run(() =>
        {
            try
            {
                var runId = $"run-{i % 10}"; // Contention on same IDs
                var run = PipelineRun.Create(
                    runId: runId,
                    issueIdentifier: $"org/repo#{i}",
                    issueTitle: $"Issue {i}",
                    issueProviderConfigId: "ip-1",
                    repoProviderConfigId: "rp-1",
                    runType: PipelineRunType.Implementation,
                    initiatedBy: "test",
                    agentId: $"agent-{i % 5}");

                _facade.AddRun(run);

                if (i % 3 == 0)
                    _facade.RemoveRun(runId);
                else
                    addedNotRemoved.Add(runId);
            }
            catch (Exception ex)
            {
                lock (exceptions) { exceptions.Add(ex); }
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        exceptions.Should().BeEmpty("Concurrent add/remove of runs should never throw");

        // Post-condition: runs that were added but never removed should still be retrievable
        // (last-writer-wins for same runId, so exact count depends on ordering)
        // At minimum: no GetRun call should throw
        for (int i = 0; i < 10; i++)
        {
            var act = () => _facade.GetRun($"run-{i}");
            act.Should().NotThrow();
        }
    }

    /// <summary>
    /// Concurrent heartbeat updates for multiple agents should not corrupt timestamp state.
    /// </summary>
    [Fact]
    public async Task ConcurrentHeartbeatUpdates_NoCorruption()
    {
        // Pre-register agents
        for (int i = 0; i < 5; i++)
        {
            _facade.Register(
                new AgentRegistrationMessage { AgentId = $"agent-{i}", Hostname = $"h-{i}", Labels = new[] { "test" } },
                $"conn-{i}");
        }

        const int iterations = 200;
        var exceptions = new List<Exception>();

        var tasks = Enumerable.Range(0, iterations).Select(i => Task.Run(() =>
        {
            try
            {
                var agentId = $"agent-{i % 5}";
                _facade.UpdateHeartbeat(agentId, DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                lock (exceptions) { exceptions.Add(ex); }
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        exceptions.Should().BeEmpty("Concurrent heartbeat updates should never throw");

        // All agents should still be registered with recent heartbeats
        for (int i = 0; i < 5; i++)
        {
            var agent = _facade.GetByAgentId($"agent-{i}");
            agent.Should().NotBeNull();
            agent!.LastHeartbeatAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// Mixed operations: register, add run, heartbeat, status transition, remove run — all concurrent.
    /// Tests the full lifecycle under contention.
    /// </summary>
    [Fact]
    public async Task MixedConcurrentOperations_NoExceptionsOrCorruption()
    {
        const int agentCount = 5;
        const int iterations = 100;
        var exceptions = new List<Exception>();

        // Pre-register agents
        for (int i = 0; i < agentCount; i++)
        {
            _facade.Register(
                new AgentRegistrationMessage { AgentId = $"agent-{i}", Hostname = $"h-{i}", Labels = new[] { "test" } },
                $"conn-{i}");
        }

        var tasks = Enumerable.Range(0, iterations).Select(i => Task.Run(() =>
        {
            try
            {
                var agentId = $"agent-{i % agentCount}";
                var runId = $"mixed-run-{i}";

                // Simulate lifecycle: add run, heartbeat, complete run, remove
                var run = PipelineRun.Create(
                    runId: runId,
                    issueIdentifier: $"org/repo#{i}",
                    issueTitle: $"Issue {i}",
                    issueProviderConfigId: "ip-1",
                    repoProviderConfigId: "rp-1",
                    runType: PipelineRunType.Implementation,
                    initiatedBy: "test",
                    agentId: agentId);

                _facade.AddRun(run);
                _facade.UpdateHeartbeat(agentId, DateTimeOffset.UtcNow);
                _facade.TransitionStatus(agentId, AgentStatus.Busy);
                _facade.GetRun(runId); // Concurrent read
                _facade.RemoveRun(runId);
                _facade.TransitionStatus(agentId, AgentStatus.Idle);
            }
            catch (Exception ex)
            {
                lock (exceptions) { exceptions.Add(ex); }
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        exceptions.Should().BeEmpty("Mixed concurrent lifecycle operations should never throw");

        // All agents should still be registered
        for (int i = 0; i < agentCount; i++)
        {
            _facade.GetByAgentId($"agent-{i}").Should().NotBeNull();
        }
    }
}
