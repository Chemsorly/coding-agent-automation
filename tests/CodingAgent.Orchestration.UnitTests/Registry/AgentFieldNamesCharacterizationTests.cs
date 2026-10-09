using AwesomeAssertions;
using CodingAgent.Contracts;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration.UnitTests.Registry;

/// <summary>
/// Characterization tests for <see cref="AgentRegistryService.UpdateAgentFieldAsync"/> switch
/// and <see cref="DistributedAgentRegistryService.UpdateAgentFieldAsync"/> field mapping.
///
/// Purpose: lock in the wire-contract field key strings before the #3440 refactor replaces
/// inline string literals with <see cref="AgentFieldNames"/> constants. If a constant value
/// is accidentally changed, these tests will catch the drift.
/// </summary>
public sealed class AgentFieldNamesCharacterizationTests
{
    // ── AgentRegistryService switch characterization ──────────────────────────

    private static AgentRegistryService CreateInMemoryRegistry() =>
        new(Mock.Of<ILogger>());

    private static AgentRegistrationMessage Msg(string id) =>
        new() { AgentId = new AgentId(id), Hostname = "host-1", Labels = ["kiro", "dotnet"] };

    [Fact]
    public async Task AgentRegistryService_UpdateAgentFieldAsync_ActiveJobId_SetsProperty()
    {
        var registry = CreateInMemoryRegistry();
        registry.Register(Msg("agent-1"), "conn-1");
        var agentId = new AgentId("agent-1");

        await registry.UpdateAgentFieldAsync(agentId, "activeJobId", "run-abc");

        var entry = registry.GetByAgentId(agentId);
        entry.Should().NotBeNull();
        entry!.ActiveJobId.Should().Be("run-abc",
            "the switch case \"activeJobId\" must map to AgentEntry.ActiveJobId");
    }

    [Fact]
    public async Task AgentRegistryService_UpdateAgentFieldAsync_ActiveJobId_ClearsOnNull()
    {
        var registry = CreateInMemoryRegistry();
        var entry = registry.Register(Msg("agent-1"), "conn-1");
        lock (entry.SyncRoot) entry.ActiveJobId = "existing-run";
        var agentId = new AgentId("agent-1");

        await registry.UpdateAgentFieldAsync(agentId, "activeJobId", null);

        var result = registry.GetByAgentId(agentId);
        result!.ActiveJobId.Should().BeNull("null value must clear ActiveJobId");
    }

    [Fact]
    public async Task AgentRegistryService_UpdateAgentFieldAsync_OrphanRestoredAt_SetsProperty()
    {
        var registry = CreateInMemoryRegistry();
        registry.Register(Msg("agent-1"), "conn-1");
        var agentId = new AgentId("agent-1");
        var timestamp = new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

        await registry.UpdateAgentFieldAsync(agentId, "orphanRestoredAt", timestamp.ToString("O"));

        var entry = registry.GetByAgentId(agentId);
        entry!.OrphanRestoredAt.Should().BeCloseTo(timestamp, TimeSpan.FromMilliseconds(1),
            "the switch case \"orphanRestoredAt\" must map to AgentEntry.OrphanRestoredAt");
    }

    [Fact]
    public async Task AgentRegistryService_UpdateAgentFieldAsync_ActiveChatSessionId_SetsProperty()
    {
        var registry = CreateInMemoryRegistry();
        registry.Register(Msg("agent-1"), "conn-1");
        var agentId = new AgentId("agent-1");

        await registry.UpdateAgentFieldAsync(agentId, "activeChatSessionId", "session-xyz");

        var entry = registry.GetByAgentId(agentId);
        entry!.ActiveChatSessionId.Should().Be("session-xyz",
            "the switch case \"activeChatSessionId\" must map to AgentEntry.ActiveChatSessionId");
    }

    [Fact]
    public async Task AgentRegistryService_UpdateAgentFieldAsync_LastJobCompletedAt_SetsProperty()
    {
        var registry = CreateInMemoryRegistry();
        registry.Register(Msg("agent-1"), "conn-1");
        var agentId = new AgentId("agent-1");
        var timestamp = new DateTimeOffset(2026, 2, 20, 10, 30, 0, TimeSpan.Zero);

        await registry.UpdateAgentFieldAsync(agentId, "lastJobCompletedAt", timestamp.ToString("O"));

        var entry = registry.GetByAgentId(agentId);
        entry!.LastJobCompletedAt.Should().BeCloseTo(timestamp, TimeSpan.FromMilliseconds(1),
            "the switch case \"lastJobCompletedAt\" must map to AgentEntry.LastJobCompletedAt");
    }

    [Fact]
    public async Task AgentRegistryService_UpdateAgentFieldAsync_Disabled_SetsProperty()
    {
        var registry = CreateInMemoryRegistry();
        registry.Register(Msg("agent-1"), "conn-1");
        var agentId = new AgentId("agent-1");

        await registry.UpdateAgentFieldAsync(agentId, "disabled", "True");

        var entry = registry.GetByAgentId(agentId);
        entry!.Disabled.Should().BeTrue(
            "the switch case \"disabled\" must map to AgentEntry.Disabled");
    }

    [Fact]
    public async Task AgentRegistryService_UpdateAgentFieldAsync_UnknownField_DoesNotThrow()
    {
        var registry = CreateInMemoryRegistry();
        registry.Register(Msg("agent-1"), "conn-1");
        var agentId = new AgentId("agent-1");

        // The default branch logs a warning but must not throw.
        var act = async () => await registry.UpdateAgentFieldAsync(agentId, "unknownFieldXyz", "value");

        await act.Should().NotThrowAsync("unknown fields must be silently ignored (logged at Warning)");
    }

    // ── DistributedAgentRegistryService field key characterization ────────────

    private static DistributedAgentRegistryService CreateDistributedRegistry(FakeRedisStore store) =>
        new(store, Mock.Of<ILogger>());

    [Fact]
    public async Task DistributedRegistryService_UpdateAgentFieldAsync_ActiveJobId_WritesCorrectHashField()
    {
        var store = new FakeRedisStore();
        var sut = CreateDistributedRegistry(store);
        sut.Register(Msg("agent-1"), "conn-1");
        var agentId = new AgentId("agent-1");

        await sut.UpdateAgentFieldAsync(agentId, "activeJobId", "run-42");

        var hash = store.GetHash("agent:agent-1");
        hash.Should().NotBeNull();
        hash!["activeJobId"].Should().Be("run-42",
            "the Redis hash field name for ActiveJobId must be \"activeJobId\"");
    }

    [Fact]
    public async Task DistributedRegistryService_UpdateAgentFieldAsync_OrphanRestoredAt_WritesCorrectHashField()
    {
        var store = new FakeRedisStore();
        var sut = CreateDistributedRegistry(store);
        sut.Register(Msg("agent-1"), "conn-1");
        var agentId = new AgentId("agent-1");
        var ts = new DateTimeOffset(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

        await sut.UpdateAgentFieldAsync(agentId, "orphanRestoredAt", ts.ToString("O"));

        var hash = store.GetHash("agent:agent-1");
        hash!["orphanRestoredAt"].Should().Be(ts.ToString("O"),
            "the Redis hash field name for OrphanRestoredAt must be \"orphanRestoredAt\" and the value must be the round-trip ISO-8601 timestamp");
    }

    [Fact]
    public async Task DistributedRegistryService_UpdateAgentFieldAsync_ActiveChatSessionId_WritesCorrectHashField()
    {
        var store = new FakeRedisStore();
        var sut = CreateDistributedRegistry(store);
        sut.Register(Msg("agent-1"), "conn-1");
        var agentId = new AgentId("agent-1");

        await sut.UpdateAgentFieldAsync(agentId, "activeChatSessionId", "sess-99");

        var hash = store.GetHash("agent:agent-1");
        hash!["activeChatSessionId"].Should().Be("sess-99",
            "the Redis hash field name for ActiveChatSessionId must be \"activeChatSessionId\"");
    }

    [Fact]
    public async Task DistributedRegistryService_UpdateAgentFieldAsync_Disabled_WritesCorrectHashField()
    {
        var store = new FakeRedisStore();
        var sut = CreateDistributedRegistry(store);
        sut.Register(Msg("agent-1"), "conn-1");
        var agentId = new AgentId("agent-1");

        await sut.UpdateAgentFieldAsync(agentId, "disabled", "True");

        var hash = store.GetHash("agent:agent-1");
        hash!["disabled"].Should().Be("True",
            "the Redis hash field name for Disabled must be \"disabled\"");
    }

    [Fact]
    public void DistributedRegistryService_Register_WritesAllInScopeFieldKeys()
    {
        // Characterization test: guards that the initial hash written by Register uses the
        // correct field key strings. If a constant value changes, this test catches it.
        var store = new FakeRedisStore();
        var sut = CreateDistributedRegistry(store);
        sut.Register(Msg("agent-1"), "conn-1");

        var hash = store.GetHash("agent:agent-1");
        hash.Should().NotBeNull();

        // In-scope field names from acceptance criteria scope query
        hash!.Keys.Should().Contain("activeJobId", "Register must write activeJobId field");
        hash.Keys.Should().Contain("orphanRestoredAt", "Register must write orphanRestoredAt field");
        hash.Keys.Should().Contain("activeChatSessionId", "Register must write activeChatSessionId field");
        hash.Keys.Should().Contain("disabled", "Register must write disabled field");
        hash.Keys.Should().Contain("busySince", "Register must write busySince field");
        hash.Keys.Should().Contain("disconnectedAt", "Register must write disconnectedAt field");
        hash.Keys.Should().Contain("lastHeartbeatAt", "Register must write lastHeartbeatAt field");
        hash.Keys.Should().Contain("connectionId", "Register must write connectionId field");
        hash.Keys.Should().Contain("registeredAt", "Register must write registeredAt field");
        hash.Keys.Should().Contain("lastJobCompletedAt", "Register must write lastJobCompletedAt field");
    }

    // ── AgentFieldNames constant values (wire-contract guard) ────────────────

    // TODO: This test is tautological for C# const values — a const cannot change at runtime, so
    // asserting that AgentFieldNames.ActiveJobId == "activeJobId" will always pass unless the constant's
    // initializer is edited in source. The real wire-contract guards are the AgentRegistryService switch
    // tests and DistributedRegistryService_Register_WritesAllInScopeFieldKeys above, which use raw string
    // literals and would catch a constant being renamed to a new wire value. This test is not harmful, but
    // it does not add detection capability beyond a compilation check. If a constant is intentionally
    // renamed, update both this test and the raw-literal characterization tests. See review-findings
    // (TestQualityReviewer WARNING).
    [Fact]
    public void AgentFieldNames_ConstantValues_MatchExpectedWireNames()
    {
        // Explicit wire-contract assertions: if a constant value is accidentally changed,
        // this test will fail before any Redis data migration is done, preventing silent drift.
        AgentFieldNames.ActiveJobId.Should().Be("activeJobId");
        AgentFieldNames.LastJobCompletedAt.Should().Be("lastJobCompletedAt");
        AgentFieldNames.OrphanRestoredAt.Should().Be("orphanRestoredAt");
        AgentFieldNames.ActiveChatSessionId.Should().Be("activeChatSessionId");
        AgentFieldNames.Disabled.Should().Be("disabled");
        AgentFieldNames.BusySince.Should().Be("busySince");
        AgentFieldNames.DisconnectedAt.Should().Be("disconnectedAt");
        AgentFieldNames.LastHeartbeatAt.Should().Be("lastHeartbeatAt");
        AgentFieldNames.ConnectionId.Should().Be("connectionId");
        AgentFieldNames.RegisteredAt.Should().Be("registeredAt");
    }
}
