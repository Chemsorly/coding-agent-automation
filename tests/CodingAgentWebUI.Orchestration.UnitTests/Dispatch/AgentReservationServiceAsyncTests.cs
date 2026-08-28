using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.TestUtilities;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Orchestration.UnitTests.Dispatch;

/// <summary>
/// Verifies that <see cref="AgentReservationService.SelectAgent"/> uses the async
/// <see cref="IAgentRegistryService"/> methods on the distributed path, and the sync
/// methods on the in-memory path (lock-safety constraint).
/// </summary>
public sealed class AgentReservationServiceAsyncTests
{
    private readonly FakeRedisStore _store = new();

    private static AgentRegistrationMessage Msg(string id, string[]? labels = null) =>
        new() { AgentId = new AgentId(id), Hostname = "h", Labels = labels ?? ["kiro"] };

    // ── Distributed path: uses async registry methods ─────────────────────

    [Fact]
    public void SelectAgentDistributed_UsesAsyncIdleAgentLookup()
    {
        // Arrange: mock registry with one idle agent
        var mockRegistry = new Mock<IAgentRegistryService>(MockBehavior.Loose);
        var agent = new AgentEntry
        {
            AgentId = new AgentId("agent-1"),
            ConnectionId = "conn-1",
            Hostname = "h",
            Labels = ["kiro"],
            Status = AgentStatus.Idle,
            RegisteredAt = DateTimeOffset.UtcNow
        };

        mockRegistry
            .Setup(r => r.GetIdleAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntry> { agent }.AsReadOnly());

        // Double-check after lock acquisition uses GetByAgentIdAsync
        mockRegistry
            .Setup(r => r.GetByAgentIdAsync(It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        var sut = new AgentReservationService(mockRegistry.Object, Log.Logger, _store);

        // Act: SelectAgent is synchronous (blocks on .GetAwaiter().GetResult() internally).
        // Capture the result first so the async chain completes before any assertions run.
        var selected = sut.SelectAgent(["kiro"]);

        // Assert behavioral correctness — ensures the test cannot pass vacuously if the mock
        // returns a default null (which would indicate GetIdleAgentsAsync was never called or
        // the candidate was rejected before the double-check).
        selected.Should().NotBeNull("a matching idle agent was available");
        selected!.AgentId.Value.Should().Be("agent-1", "the correct agent should be returned");

        // Assert: async method was called (not the sync overload) on the distributed path.
        // These interaction checks are placed AFTER the behavioral assertion so that a vacuous
        // pass (null return + loose mock) is caught by the NotBeNull assertion above first.
        mockRegistry.Verify(r => r.GetIdleAgentsAsync(It.IsAny<CancellationToken>()), Times.Once);
        mockRegistry.Verify(r => r.GetIdleAgents(), Times.Never);
    }

    [Fact]
    public void SelectAgentDistributed_UsesAsyncByAgentIdLookup_ForDoubleCheck()
    {
        // Arrange: one idle agent
        var mockRegistry = new Mock<IAgentRegistryService>(MockBehavior.Loose);
        var agent = new AgentEntry
        {
            AgentId = new AgentId("agent-1"),
            ConnectionId = "conn-1",
            Hostname = "h",
            Labels = ["kiro"],
            Status = AgentStatus.Idle,
            RegisteredAt = DateTimeOffset.UtcNow
        };

        mockRegistry
            .Setup(r => r.GetIdleAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntry> { agent }.AsReadOnly());
        mockRegistry
            .Setup(r => r.GetByAgentIdAsync(It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(agent);

        var sut = new AgentReservationService(mockRegistry.Object, Log.Logger, _store);

        // Act: capture the return value — required to ensure the synchronous SelectAgent call
        // (which blocks on .GetAwaiter().GetResult() internally) has fully completed the async
        // chain before the Verify calls below run. Without capturing the result, a future
        // refactor that makes SelectAgent return a Task could cause Verify to run before the
        // async chain completes, producing a vacuous pass.
        var selected = sut.SelectAgent(["kiro"]);

        // Assert behavioral correctness first — prevents vacuous pass if mock returns null.
        selected.Should().NotBeNull("a matching idle agent was available");
        selected!.AgentId.Value.Should().Be("agent-1", "the correct agent should be returned");

        // Assert: the double-check uses GetByAgentIdAsync, not the sync GetByAgentId
        mockRegistry.Verify(r => r.GetByAgentIdAsync(It.IsAny<AgentId>(), It.IsAny<CancellationToken>()), Times.Once);
        mockRegistry.Verify(r => r.GetByAgentId(It.IsAny<AgentId>()), Times.Never);
    }

    [Fact]
    public void SelectAgentDistributed_SkipsDisabledAgent_ViaAsyncPath()
    {
        var mockRegistry = new Mock<IAgentRegistryService>(MockBehavior.Loose);
        var disabled = new AgentEntry
        {
            AgentId = new AgentId("agent-disabled"),
            ConnectionId = "conn-d",
            Hostname = "h",
            Labels = ["kiro"],
            Status = AgentStatus.Idle,
            Disabled = true,
            RegisteredAt = DateTimeOffset.UtcNow
        };

        mockRegistry
            .Setup(r => r.GetIdleAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntry> { disabled }.AsReadOnly());

        var sut = new AgentReservationService(mockRegistry.Object, Log.Logger, _store);
        var result = sut.SelectAgent(["kiro"]);

        result.Should().BeNull("disabled agents must not be selected");
    }

    // ── In-memory path: uses sync registry methods (lock safety) ─────────────

    [Fact]
    public void SelectAgentInMemory_UsesSyncIdleAgentLookup_NeverCallsAsync()
    {
        // No Redis store — falls back to in-memory path
        var mockRegistry = new Mock<IAgentRegistryService>(MockBehavior.Loose);
        mockRegistry.Setup(r => r.GetIdleAgents()).Returns([]);

        var sut = new AgentReservationService(mockRegistry.Object, Log.Logger, store: null);

        sut.SelectAgent(["kiro"]);

        // In-memory path must use sync GetIdleAgents (called inside lock)
        mockRegistry.Verify(r => r.GetIdleAgents(), Times.Once);
        // Async overload must NOT be called — it would cause CS1996 inside the lock
        mockRegistry.Verify(r => r.GetIdleAgentsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void SelectAgentInMemory_UsesRealRegistry_ReservesAgentCorrectly()
    {
        // Integration test with in-memory AgentRegistryService — no Redis
        var realRegistry = new AgentRegistryService(Log.Logger);
        realRegistry.Register(Msg("agent-1"), "conn-1");

        var sut = new AgentReservationService(realRegistry, Log.Logger, store: null);
        var selected = sut.SelectAgent(["kiro"]);

        selected.Should().NotBeNull();
        selected!.AgentId.Value.Should().Be("agent-1");
        selected.Status.Should().Be(AgentStatus.Busy);
    }
}
