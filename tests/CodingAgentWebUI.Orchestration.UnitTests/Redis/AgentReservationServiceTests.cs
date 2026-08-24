using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.TestUtilities;
using Serilog;

namespace CodingAgentWebUI.Orchestration.UnitTests.Redis;

public sealed class AgentReservationServiceTests
{
    private readonly FakeRedisStore _store = new();

    private DistributedAgentRegistryService MakeRegistry() =>
        new(_store, Log.Logger);

    private AgentReservationService MakeSut(IAgentRegistryService registry) =>
        new(registry, Log.Logger, _store);

    private static AgentRegistrationMessage Msg(string id, string[]? labels = null) =>
        new() { AgentId = new AgentId(id), Hostname = "h", Labels = labels ?? ["kiro"] };

    // ── SelectAgent ───────────────────────────────────────────────────────────

    [Fact]
    public void SelectAgent_ReservesAgent_WithDistributedLock()
    {
        var registry = MakeRegistry();
        registry.Register(Msg("agent-1"), "conn-1");
        var sut = MakeSut(registry);

        var selected = sut.SelectAgent(["kiro"]);

        selected.Should().NotBeNull();
        selected!.AgentId.Value.Should().Be("agent-1");
        // Agent should now be Busy in Redis
        var hash = _store.GetHash("agent:agent-1");
        hash!["status"].Should().Be("Busy");
        _store.GetSet("agents:idle").Should().NotContain("agent-1");
    }

    [Fact]
    public void SelectAgent_ReturnsNull_WhenNoIdleAgents()
    {
        var registry = MakeRegistry();
        var sut = MakeSut(registry);

        sut.SelectAgent(["kiro"]).Should().BeNull();
    }

    [Fact]
    public void SelectAgent_ReturnsNull_WhenNoLabelMatch()
    {
        var registry = MakeRegistry();
        registry.Register(Msg("agent-1", ["dotnet"]), "conn-1");
        var sut = MakeSut(registry);

        sut.SelectAgent(["java"]).Should().BeNull();
    }

    [Fact]
    public async Task SelectAgent_TwoInstances_SameStore_DoNotDoubleBook()
    {
        // Simulate two replicas with separate registry instances sharing the same Redis
        var registry1 = MakeRegistry();
        var registry2 = MakeRegistry();
        registry1.Register(Msg("agent-1"), "conn-1");

        var sut1 = MakeSut(registry1);
        var sut2 = MakeSut(registry2);

        // Both attempt to select concurrently — only one should succeed
        var t1 = Task.Run(() => sut1.SelectAgent(["kiro"]));
        var t2 = Task.Run(() => sut2.SelectAgent(["kiro"]));
        await Task.WhenAll(t1, t2);

        var result1 = await t1;
        var result2 = await t2;
        var results = new[] { result1, result2 };
        results.Count(r => r is not null).Should().Be(1);
        results.Count(r => r is null).Should().Be(1);

        // The agent must be Busy in Redis — not double-booked
        _store.GetHash("agent:agent-1")!["status"].Should().Be("Busy");
    }

    [Fact]
    public async Task SelectAgent_DoubleCheck_SkipsCandidate_WhenStatusChangedBeforeReservation()
    {
        var registry = MakeRegistry();
        registry.Register(Msg("agent-1"), "conn-1");

        // Manually transition agent to Busy before SelectAgent runs its double-check
        await _store.HashSetFieldAsync("agent:agent-1", "status", "Busy");
        await _store.SetRemoveAsync("agents:idle", "agent-1");

        var sut = MakeSut(registry);
        var selected = sut.SelectAgent(["kiro"]);

        selected.Should().BeNull();
    }

    [Fact]
    public void SelectAgent_LockReleased_AfterSuccessfulReservation()
    {
        var registry = MakeRegistry();
        registry.Register(Msg("agent-1"), "conn-1");
        var sut = MakeSut(registry);

        sut.SelectAgent(["kiro"]);

        // Lock key should be deleted after reservation
        _store.GetSet("lock:agent:agent-1").Should().BeEmpty();
    }
}
