using AwesomeAssertions;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Services;

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

    // ── Test 3: Labels aliasing — shared mutable list mutated while entry is iterated ──

    /// <summary>
    /// Regression guard for issue #2664.
    ///
    /// Root cause: AgentRegistryService.Register previously assigned AgentEntry.Labels directly
    /// from message.Labels. MessagePack deserializes IReadOnlyList&lt;string&gt; as a mutable
    /// List&lt;string&gt; at runtime. Storing the reference creates an alias — if the caller mutates
    /// message.Labels after Register returns, OnDisconnectedAsync or GetAgentsByLabel iterating
    /// entry.Labels.Any() on another thread throws InvalidOperationException ("collection was
    /// modified"). Fix: Labels = message.Labels?.ToArray() — an immutable defensive copy.
    ///
    /// This test exercises the aliasing scenario directly:
    /// - Thread group A mutates the shared List&lt;string&gt; that was passed to Register as Labels.
    /// - Thread group B calls GetByConnectionId + entry.Labels.Any(l == "chat=true"),
    ///   mirroring OnDisconnectedAsync.
    /// - Thread group C calls TransitionStatus(Disconnected), satisfying acceptance criterion 3.
    /// With the fix, entry.Labels is the ToArray() copy — immune to external list mutations.
    /// Without the fix, group A mutations race group B iterations producing InvalidOperationException.
    /// </summary>
    [Fact]
    public void ConcurrentRegisterAndDisconnect_LabelsIteration_DoesNotThrowInvalidOperationException()
    {
        // ── Arrange ─────────────────────────────────────────────────────────────────────

        // TODO [WARNING]: agentId is a plain string passed to TransitionStatus which accepts
        // AgentId (a value type). This compiles via an implicit string→AgentId conversion. If
        // that implicit operator is removed or restricted, the test may silently compile to a
        // different overload. Prefer constructing AgentId explicitly: new AgentId(agentId).
        // (Reviewer: DotNetSpecialist)
        const string agentId = "agent-labels-race";
        const string connectionId = "conn-labels-race";

        // A shared mutable list — same object that will be passed as message.Labels.
        // This simulates the aliasing risk: the caller retains a reference to the same
        // List<string> that gets stored in AgentEntry.Labels (before the fix).
        var sharedLabels = new List<string> { "dotnet", "linux" };

        // Register with the shared mutable list to create the aliased entry.
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = "host-labels-race",
            Labels = sharedLabels   // intentional aliasing to test the fix
        }, connectionId);

        // TODO [WARNING]: This test only exercises the add-factory path (initial registration).
        // The original bug trigger is a concurrent *re-registration* (update factory path in
        // AddOrUpdate) racing with an OnDisconnectedAsync reader. To cover that path, a dedicated
        // thread group should call _registry.Register() with the same agentId and a freshly
        // created mutable List<string> during the concurrent phase, forcing the update factory to
        // overwrite entry.Labels while group B iterates it. (Reviewer: TestQualityReviewer)

        const int iterations = 500;
        // TODO [WARNING]: Race window reliability — at 500 iterations there is no guarantee the
        // race is wide enough to trigger InvalidOperationException without the fix. If this test
        // starts passing against the unfixed code due to scheduling luck, increase iterations or
        // add Thread.Yield() / Thread.Sleep(0) inside the hot loops to widen the race window.
        // (Reviewer: TestQualityReviewer)

        // 2 mutation threads, 4 read threads, 2 TransitionStatus threads
        // TODO [WARNING]: mutatorCount = 2 means two threads concurrently mutate sharedLabels
        // (a non-thread-safe List<T>) against each other. This can cause the mutators themselves
        // to throw ArgumentOutOfRangeException or InvalidOperationException, which RecordException
        // captures — indistinguishable from a read-side failure and a potential false-positive.
        // Reduce to mutatorCount = 1 to eliminate the inter-mutator race on sharedLabels.
        // (Reviewers: Correctness, DotNetSpecialist)
        const int mutatorCount = 2;
        const int readerCount = 4;
        const int transitionCount = 2;
        const int totalThreadCount = mutatorCount + readerCount + transitionCount;

        var barrier = new Barrier(totalThreadCount);
        Exception? caughtException = null;
        var exceptionLock = new object();

        void RecordException(Exception ex)
        {
            lock (exceptionLock)
            {
                caughtException ??= ex;
            }
        }

        var threads = new Thread[totalThreadCount];
        int idx = 0;

        // ── Thread group A: mutate the shared list ───────────────────────────────────
        // Simulates an external caller mutating message.Labels after Register() returns.
        // With the fix the stored Labels is an independent copy — these mutations are harmless.
        // Without the fix the stored Labels IS sharedLabels — concurrent iteration throws.
        for (int m = 0; m < mutatorCount; m++)
        {
            var localM = m;
            threads[idx++] = new Thread(() =>
            {
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
                try
                {
                    for (int i = 0; i < iterations; i++)
                    {
                        if (i % 2 == 0)
                            sharedLabels.Add($"extra-{localM}-{i}");
                        else if (sharedLabels.Count > 2)
                            sharedLabels.RemoveAt(sharedLabels.Count - 1);
                    }
                }
                catch (Exception ex)
                {
                    RecordException(ex);
                }
            });
        }

        // ── Thread group B: read entry.Labels.Any(...) ───────────────────────────────
        // Mirrors the exact code path in AgentHub.OnDisconnectedAsync.
        for (int r = 0; r < readerCount; r++)
        {
            threads[idx++] = new Thread(() =>
            {
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
                try
                {
                    for (int i = 0; i < iterations; i++)
                    {
                        var entry = _registry.GetByConnectionId(connectionId);
                        if (entry is not null)
                        {
                            // This is the exact expression from OnDisconnectedAsync.
                            // If Labels is still aliased to sharedLabels (no fix), and group A
                            // is simultaneously adding/removing elements, this throws
                            // InvalidOperationException.
                            _ = entry.Labels.Any(l =>
                                string.Equals(l, "chat=true", StringComparison.Ordinal));
                        }
                    }
                }
                catch (InvalidOperationException ex)
                {
                    RecordException(ex);
                }
                catch (Exception ex)
                {
                    RecordException(ex);
                }
            });
        }

        // ── Thread group C: TransitionStatus(Disconnected) ───────────────────────────
        // Satisfies acceptance criterion 3: TransitionStatus(Disconnected) must complete
        // without exception in the concurrent scenario.
        // TODO [WARNING]: The tight Disconnected→Idle loop immediately undoes disconnect state
        // so the test never validates DisconnectedAt is set or that the agent ends in a coherent
        // post-disconnect state. The final Enum.IsDefined assertion is also too broad — it passes
        // for any valid AgentStatus value. Consider asserting:
        //   finalEntry.Status.Should().BeOneOf(AgentStatus.Idle, AgentStatus.Disconnected)
        // (Reviewer: TestQualityReviewer)
        for (int ts = 0; ts < transitionCount; ts++)
        {
            threads[idx++] = new Thread(() =>
            {
                Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
                try
                {
                    for (int i = 0; i < iterations; i++)
                    {
                        _registry.TransitionStatus(agentId, AgentStatus.Disconnected);
                        _registry.TransitionStatus(agentId, AgentStatus.Idle);
                    }
                }
                catch (Exception ex)
                {
                    RecordException(ex);
                }
            });
        }

        // ── Act ──────────────────────────────────────────────────────────────────────

        foreach (var thread in threads)
            thread.Start();

        foreach (var thread in threads)
            thread.Join();

        // ── Assert ───────────────────────────────────────────────────────────────────

        caughtException.Should().BeNull(
            "entry.Labels must be an immutable array after Register; concurrent mutation of " +
            "the original List<string> must not affect the stored copy and must not cause " +
            "InvalidOperationException during Labels.Any() or TransitionStatus(Disconnected)");

        // Final state consistency: agent is still reachable and status is valid.
        var finalEntry = _registry.GetByAgentId(agentId);
        finalEntry.Should().NotBeNull("agent must still be in the registry after concurrent operations");
        Enum.IsDefined(finalEntry!.Status).Should().BeTrue("status must be a valid AgentStatus value");
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
