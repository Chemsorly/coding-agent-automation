using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Orchestration.UnitTests.Registry;

/// <summary>
/// Unit tests for <see cref="AgentRegistryService.ClearAgentState"/>.
/// Verifies: null-safety, unknown-agent safety, field clearing (ActiveJobId, OrphanRestoredAt),
/// status transition to Idle, and SyncRoot lock acquisition.
/// Acceptance criteria: null-check, lock acquisition, and field clearing are all covered.
/// </summary>
public class AgentRegistryServiceClearAgentStateTests
{
    private static AgentRegistryService CreateRegistry() =>
        new AgentRegistryService(Mock.Of<ILogger>());

    private static AgentEntry RegisterAgent(
        AgentRegistryService registry,
        string agentId = "agent-1",
        AgentStatus status = AgentStatus.Idle)
    {
        var message = new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = "host-1",
            Labels = ["dotnet"],
            ActiveJob = null
        };
        var entry = registry.Register(message, connectionId: $"conn-{agentId}");
        if (status != AgentStatus.Idle)
            registry.TransitionStatus(agentId, status);
        return entry;
    }

    // ── Null-safety ───────────────────────────────────────────────────────────

    [Fact]
    public void ClearAgentState_NullAgentId_DoesNothing()
    {
        var registry = CreateRegistry();
        RegisterAgent(registry);

        // Must not throw
        registry.ClearAgentState(null);

        // Registry unchanged
        registry.GetAllAgents().Should().HaveCount(1);
    }

    [Fact]
    public void ClearAgentState_EmptyStringAgentId_DoesNothing()
    {
        var registry = CreateRegistry();
        RegisterAgent(registry);

        // Must not throw
        registry.ClearAgentState(string.Empty);

        // Registry unchanged
        registry.GetAllAgents().Should().HaveCount(1);
    }

    [Fact]
    public void ClearAgentState_UnknownAgentId_DoesNothing()
    {
        var registry = CreateRegistry();
        // No agents registered — must not throw
        registry.ClearAgentState("non-existent-agent");

        registry.GetAllAgents().Should().BeEmpty();
    }

    // ── Field clearing ────────────────────────────────────────────────────────

    [Fact]
    public void ClearAgentState_ActiveAgent_ClearsActiveJobId()
    {
        var registry = CreateRegistry();
        var agent = RegisterAgent(registry);
        agent.ActiveJobId = "job-1";

        registry.ClearAgentState("agent-1");

        agent.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public void ClearAgentState_ActiveAgent_ClearsOrphanRestoredAt()
    {
        var registry = CreateRegistry();
        var agent = RegisterAgent(registry);
        agent.OrphanRestoredAt = DateTimeOffset.UtcNow;

        registry.ClearAgentState("agent-1");

        agent.OrphanRestoredAt.Should().BeNull();
    }

    [Fact]
    public void ClearAgentState_ActiveAgent_TransitionsToIdle()
    {
        var registry = CreateRegistry();
        RegisterAgent(registry, status: AgentStatus.Busy);

        registry.ClearAgentState("agent-1");

        var agent = registry.GetByAgentId("agent-1");
        agent!.Status.Should().Be(AgentStatus.Idle);
    }

    [Fact]
    public void ClearAgentState_AlreadyIdleAgent_RemainsIdle()
    {
        var registry = CreateRegistry();
        var agent = RegisterAgent(registry, status: AgentStatus.Idle);
        agent.ActiveJobId = "stale-job";

        registry.ClearAgentState("agent-1");

        agent.ActiveJobId.Should().BeNull();
        registry.GetByAgentId("agent-1")!.Status.Should().Be(AgentStatus.Idle);
    }

    [Fact]
    public void ClearAgentState_ClearsAllFieldsAndTransitions_Atomically()
    {
        var registry = CreateRegistry();
        var agent = RegisterAgent(registry, status: AgentStatus.Busy);
        agent.ActiveJobId = "job-99";
        agent.OrphanRestoredAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        registry.ClearAgentState("agent-1");

        agent.ActiveJobId.Should().BeNull();
        agent.OrphanRestoredAt.Should().BeNull();
        agent.Status.Should().Be(AgentStatus.Idle);
    }

    // ── Lock acquisition (behavioral verification) ───────────────────────────

    /// <summary>
    /// Verifies that ClearAgentState acquires SyncRoot before clearing fields.
    /// A background thread holds SyncRoot; the main thread calls ClearAgentState.
    /// If the implementation acquires SyncRoot, it must block until the background
    /// thread releases it. After release, fields must be correctly cleared.
    /// This is a behavioral test: if ClearAgentState did NOT acquire the lock,
    /// the background thread's write would race with the clear and the final
    /// state would be indeterminate.
    /// </summary>
    [Fact]
    public async Task ClearAgentState_AcquiresSyncRootLock_BlocksUntilReleased()
    {
        var registry = CreateRegistry();
        var agent = RegisterAgent(registry, status: AgentStatus.Busy);
        agent.ActiveJobId = "job-hold";
        agent.OrphanRestoredAt = DateTimeOffset.UtcNow;

        var lockHeld = new TaskCompletionSource<bool>();
        var releaseLock = new TaskCompletionSource<bool>();

        // Background thread acquires SyncRoot and signals it, then waits for permission to release
        var lockTask = Task.Run(() =>
        {
            lock (agent.SyncRoot)
            {
                lockHeld.SetResult(true);
                // Hold the lock until the test tells us to release
                releaseLock.Task.GetAwaiter().GetResult();
                // While still holding lock, mutate a field — ClearAgentState should overwrite this after lock release
                agent.ActiveJobId = "written-under-lock";
            }
        });

        // Wait until background thread holds the lock
        await lockHeld.Task;

        // Start ClearAgentState on a separate thread — it should block on SyncRoot
        var clearTask = Task.Run(() => registry.ClearAgentState("agent-1"));

        // Give clearTask a moment to reach the lock (it should be blocked)
        // TODO: The 50ms delay is timing-sensitive on heavily loaded CI runners. A more deterministic
        // approach would use a ManualResetEventSlim signaled from within ClearAgentState's lock
        // attempt, but that requires a test seam. If this assertion starts producing spurious passes
        // in CI, increase to 200-500ms or remove the intermediate "is blocked" check (the final
        // assertions after Task.WhenAll are reliable regardless).
        await Task.Delay(50);
        clearTask.IsCompleted.Should().BeFalse("ClearAgentState should be blocked waiting for SyncRoot");

        // Release the lock — ClearAgentState should now proceed
        releaseLock.SetResult(true);
        await Task.WhenAll(lockTask, clearTask);

        // ClearAgentState must have cleared both fields after acquiring the lock
        agent.ActiveJobId.Should().BeNull("ClearAgentState must clear ActiveJobId after acquiring lock");
        agent.OrphanRestoredAt.Should().BeNull("ClearAgentState must clear OrphanRestoredAt after acquiring lock");
        agent.Status.Should().Be(AgentStatus.Idle);
    }

    // ── Does not touch LastJobCompletedAt ─────────────────────────────────────

    [Fact]
    public void ClearAgentState_DoesNotClearLastJobCompletedAt()
    {
        var registry = CreateRegistry();
        var agent = RegisterAgent(registry);
        var completedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        agent.LastJobCompletedAt = completedAt;

        registry.ClearAgentState("agent-1");

        agent.LastJobCompletedAt.Should().Be(completedAt,
            "ClearAgentState must not touch LastJobCompletedAt — callers set it separately for FIFO ordering");
    }
}
