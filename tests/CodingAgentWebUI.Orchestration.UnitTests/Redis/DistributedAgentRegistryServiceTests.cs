using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.TestUtilities;
using Serilog;

namespace CodingAgentWebUI.Orchestration.UnitTests.Redis;

public sealed class DistributedAgentRegistryServiceTests
{
    private readonly FakeRedisStore _store = new();
    private readonly DistributedAgentRegistryService _sut;

    public DistributedAgentRegistryServiceTests()
    {
        _sut = new DistributedAgentRegistryService(_store, Log.Logger);
    }

    private static AgentRegistrationMessage Msg(string id, string[]? labels = null) =>
        new() { AgentId = new AgentId(id), Hostname = "host-1", Labels = labels ?? ["kiro", "dotnet"] };

    // ── Register ──────────────────────────────────────────────────────────────

    [Fact]
    public void Register_SetsAllHashFields_And_AddsToBothSets()
    {
        _sut.Register(Msg("agent-1"), "conn-1");

        var hash = _store.GetHash("agent:agent-1");
        hash.Should().NotBeNull();
        hash!["agentId"].Should().Be("agent-1");
        hash["connectionId"].Should().Be("conn-1");
        hash["hostname"].Should().Be("host-1");
        hash["status"].Should().Be("Idle");
        hash["disabled"].Should().Be("False");
        hash["orphanRestoredAt"].Should().BeEmpty(); // cleared on new registration

        _store.GetSet("agents:all").Should().Contain("agent-1");
        _store.GetSet("agents:idle").Should().Contain("agent-1");
    }

    [Fact]
    public void Register_ReRegistration_RestoresIdleWhenNoActiveJob()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        // Simulate disconnect
        _sut.TransitionStatus(new AgentId("agent-1"), AgentStatus.Disconnected);
        // Re-register
        _sut.Register(Msg("agent-1"), "conn-2");

        var hash = _store.GetHash("agent:agent-1");
        hash!["status"].Should().Be("Idle");
        hash["connectionId"].Should().Be("conn-2");
    }

    [Fact]
    public async Task Register_ReRegistration_RestoresBusyWhenActiveJobPresent()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        // Set activeJobId directly in store to simulate a running job
        await _store.HashSetFieldAsync("agent:agent-1", "activeJobId", "run-abc");
        // Re-register (e.g. after container restart)
        _sut.Register(Msg("agent-1"), "conn-2");

        var hash = _store.GetHash("agent:agent-1");
        hash!["status"].Should().Be("Busy");
    }

    [Fact]
    public async Task Register_ReRegistration_PreservesDisabledFlag()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        // Manually disable the agent
        await _store.HashSetFieldAsync("agent:agent-1", "disabled", "True");
        // Re-register — disabled must not reset to False
        _sut.Register(Msg("agent-1"), "conn-2");

        var hash = _store.GetHash("agent:agent-1");
        hash!["disabled"].Should().Be("True");
    }

    // ── TransitionStatus ──────────────────────────────────────────────────────

    [Fact]
    public void TransitionStatus_Busy_RemovesFromIdleSet()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        _sut.TransitionStatus(new AgentId("agent-1"), AgentStatus.Busy);

        _store.GetSet("agents:idle").Should().NotContain("agent-1");
        _store.GetHash("agent:agent-1")!["status"].Should().Be("Busy");
    }

    [Fact]
    public void TransitionStatus_Idle_AddsToIdleSet()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        _sut.TransitionStatus(new AgentId("agent-1"), AgentStatus.Busy);
        _sut.TransitionStatus(new AgentId("agent-1"), AgentStatus.Idle);

        _store.GetSet("agents:idle").Should().Contain("agent-1");
        _store.GetHash("agent:agent-1")!["status"].Should().Be("Idle");
    }

    // ── UpdateHeartbeat ───────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateHeartbeat_RefreshesTtl_AndSelfHealsSetMembership()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        // Simulate cleanup sweep removing from set
        await _store.SetRemoveAsync("agents:all", "agent-1");
        _store.GetSet("agents:all").Should().NotContain("agent-1");

        // Heartbeat should restore membership
        _sut.UpdateHeartbeat(new AgentId("agent-1"), DateTimeOffset.UtcNow);
        // Give async fire-and-forget time to execute
        System.Threading.Thread.Sleep(50);

        _store.GetSet("agents:all").Should().Contain("agent-1");
    }

    [Fact]
    public void UpdateHeartbeat_DoesNotCreateGhostEntry_WhenHashExpired()
    {
        // Hash never existed — UpdateHeartbeat should be a no-op
        _sut.UpdateHeartbeat(new AgentId("agent-ghost"), DateTimeOffset.UtcNow);
        System.Threading.Thread.Sleep(50);

        _store.GetHash("agent:agent-ghost").Should().BeNull();
        _store.GetSet("agents:all").Should().NotContain("agent-ghost");
    }

    // ── GetIdleAgents ─────────────────────────────────────────────────────────

    [Fact]
    public void GetIdleAgents_SkipsMembersWhoseHashExpired()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        _sut.Register(Msg("agent-2"), "conn-2");

        // Simulate agent-1 hash expiry
        _store.ForceExpire("agent:agent-1");

        var idle = _sut.GetIdleAgents();
        idle.Should().HaveCount(1);
        idle[0].AgentId.Value.Should().Be("agent-2");
    }

    // ── GetByConnectionId ─────────────────────────────────────────────────────

    [Fact]
    public void GetByConnectionId_ReturnsFromLocalMap_NoRedisCall()
    {
        _sut.Register(Msg("agent-1"), "conn-xyz");

        var entry = _sut.GetByConnectionId("conn-xyz");
        entry.Should().NotBeNull();
        entry!.AgentId.Value.Should().Be("agent-1");
    }

    [Fact]
    public void GetByConnectionId_ReturnsNull_WhenNotRegistered()
    {
        _sut.GetByConnectionId("conn-unknown").Should().BeNull();
    }

    // ── UpdateAgentFieldAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAgentFieldAsync_WritesOnlySpecifiedField()
    {
        _sut.Register(Msg("agent-1"), "conn-1");

        await _sut.UpdateAgentFieldAsync(new AgentId("agent-1"), "activeJobId", "run-123");

        var hash = _store.GetHash("agent:agent-1")!;
        hash["activeJobId"].Should().Be("run-123");
        // Other fields untouched
        hash["status"].Should().Be("Idle");
        hash["hostname"].Should().Be("host-1");
    }

    [Fact]
    public async Task UpdateAgentFieldAsync_ClearsField_WhenValueNull()
    {
        _sut.Register(Msg("agent-1"), "conn-1");
        await _sut.UpdateAgentFieldAsync(new AgentId("agent-1"), "activeJobId", "run-123");
        await _sut.UpdateAgentFieldAsync(new AgentId("agent-1"), "activeJobId", null);

        _store.GetHash("agent:agent-1")!["activeJobId"].Should().BeEmpty();
    }
}
