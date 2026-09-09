using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Concurrent mutation tests for AgentEntry thread safety (Req 4.5).
/// Exercises the lock-based synchronization added to AgentEntry.SyncRoot
/// and AgentRegistryService mutations.
/// </summary>
public class AgentEntryConcurrencyTests
{
    private readonly AgentRegistryService _registry;
    private readonly Mock<ILogger> _mockLogger;

    public AgentEntryConcurrencyTests()
    {
        _mockLogger = new Mock<ILogger>();
        _registry = new AgentRegistryService(_mockLogger.Object);
    }

    // ── Test 1: Multiple threads mutating same AgentEntry don't produce torn reads ──

    [Fact]
    public void ConcurrentMutations_SameAgentEntry_NoTornReads()
    {
        // Arrange: register a single agent
        var entry = RegisterAgent("agent-concurrent", "conn-1");
        const int iterations = 1_000;
        const int threadCount = 8;
        var barrier = new Barrier(threadCount);
        var tornReadDetected = false;

        // Act: spawn multiple threads performing different mutations concurrently
        var threads = new Thread[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            var threadIndex = t;
            threads[t] = new Thread(() =>
            {
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10))); // synchronize start for maximum contention

                for (int i = 0; i < iterations; i++)
                {
                    switch (threadIndex % 4)
                    {
                        case 0:
                            // Heartbeat updates
                            _registry.UpdateHeartbeat("agent-concurrent", DateTimeOffset.UtcNow);
                            break;
                        case 1:
                            // Status transitions: Idle → Busy → Idle cycle
                            _registry.TransitionStatus("agent-concurrent", AgentStatus.Busy);
                            _registry.TransitionStatus("agent-concurrent", AgentStatus.Idle);
                            break;
                        case 2:
                            // Re-registration (updates ConnectionId and status)
                            _registry.Register(new AgentRegistrationMessage
                            {
                                AgentId = "agent-concurrent",
                                Hostname = "host-concurrent",
                                Labels = ["dotnet", "linux"]
                            }, $"conn-{threadIndex}-{i}");
                            break;
                        case 3:
                            // Read status and verify consistency — status should be a valid enum
                            var status = entry.Status;
                            var connId = entry.ConnectionId;
                            var heartbeat = entry.LastHeartbeatAt;

                            if (!Enum.IsDefined(status))
                            {
                                Volatile.Write(ref tornReadDetected, true);
                            }
                            if (connId is null)
                            {
                                Volatile.Write(ref tornReadDetected, true);
                            }
                            if (heartbeat == default)
                            {
                                Volatile.Write(ref tornReadDetected, true);
                            }
                            break;
                    }
                }
            });
            threads[t].Start();
        }

        foreach (var thread in threads)
            thread.Join();

        // Assert: no torn reads detected
        tornReadDetected.Should().BeFalse("concurrent mutations should not produce torn/inconsistent state");

        // Additional consistency check: agent should still be in registry with valid state
        var finalEntry = _registry.GetByAgentId("agent-concurrent");
        finalEntry.Should().NotBeNull();
        Enum.IsDefined(finalEntry!.Status).Should().BeTrue();
        finalEntry.ConnectionId.Should().NotBeNull();
    }

    [Fact]
    public void ConcurrentHeartbeatUpdates_NeverProduceDefaultTimestamp()
    {
        // Arrange: register agent, then hammer heartbeat from multiple threads
        var entry = RegisterAgent("agent-hb", "conn-hb");
        const int iterations = 5_000;
        const int threadCount = 4;
        var barrier = new Barrier(threadCount + 1); // +1 for reader thread
        var invalidTimestampSeen = false;

        // Writer threads update heartbeat
        var writers = new Thread[threadCount];
        for (int t = 0; t < threadCount; t++)
        {
            writers[t] = new Thread(() =>
            {
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
                for (int i = 0; i < iterations; i++)
                {
                    _registry.UpdateHeartbeat("agent-hb", DateTimeOffset.UtcNow);
                }
            });
            writers[t].Start();
        }

        // Reader thread continuously checks the heartbeat is never default/zeroed
        var reader = new Thread(() =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
            for (int i = 0; i < iterations * 2; i++)
            {
                var ts = entry.LastHeartbeatAt;
                if (ts == default)
                {
                    Volatile.Write(ref invalidTimestampSeen, true);
                    break;
                }
            }
        });
        reader.Start();

        foreach (var w in writers)
            w.Join();
        reader.Join();

        // Assert
        invalidTimestampSeen.Should().BeFalse(
            "heartbeat timestamp should never be observed as default under concurrent writes");
    }

    // ── Helpers ──

    private AgentEntry RegisterAgent(string agentId, string connectionId)
    {
        return _registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = $"host-{agentId}",
            Labels = ["dotnet", "linux"]
        }, connectionId);
    }
}
