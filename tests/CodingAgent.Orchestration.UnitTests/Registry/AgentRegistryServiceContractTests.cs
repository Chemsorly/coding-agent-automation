using System.Globalization;
using AwesomeAssertions;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;
using Serilog;

namespace CodingAgent.Orchestration.UnitTests.Registry;

/// <summary>
/// Contract tests that both <see cref="AgentRegistryService"/> (in-memory) and
/// <see cref="DistributedAgentRegistryService"/> (Redis-backed) must satisfy.
/// Each fact expresses a rule from the <see cref="IAgentRegistryService"/> contract doc.
/// <para>
/// Derived classes provide the concrete instance via <see cref="CreateRegistry"/>.
/// Modelled on <c>ConfigurationStoreContractTests.cs</c>.
/// </para>
/// </summary>
public abstract class AgentRegistryServiceContractTests
{
    /// <summary>Create a fresh, isolated registry instance for each test.</summary>
    protected abstract IAgentRegistryService CreateRegistry();

    /// <summary>
    /// Helper that mirrors <c>DistributedAgentRegistryServiceTests.Msg</c> but uses
    /// <c>IReadOnlyList&lt;string&gt;?</c> so callers can pass a mutable <c>List&lt;string&gt;</c>
    /// (needed by <see cref="ReRegister_StoresCopyOfLabels"/>).
    /// </summary>
    private static AgentRegistrationMessage Msg(string id, IReadOnlyList<string>? labels = null) =>
        new() { AgentId = new AgentId(id), Hostname = "host-1", Labels = labels ?? ["kiro", "dotnet"] };

    // ── GetByAgentId ──────────────────────────────────────────────────────────

    /// <summary>
    /// Covers bug #2144: a just-registered agent must be findable via GetByAgentIdAsync.
    /// For DistributedAgentRegistryService this is guaranteed by FakeRedisStore completing
    /// WriteRegistrationAsync synchronously before Register returns.
    /// </summary>
    [Fact]
    public async Task GetByAgentId_AfterRegister_ReturnsAgent()
    {
        var registry = CreateRegistry();
        registry.Register(Msg("agent-1"), "conn-1");

        var entry = await registry.GetByAgentIdAsync(new AgentId("agent-1"));

        entry.Should().NotBeNull();
        entry!.AgentId.Value.Should().Be("agent-1");
    }

    // ── Labels — re-registration ──────────────────────────────────────────────

    /// <summary>
    /// Rule 1: re-registration replaces the previous label set so GetAgentsByLabel routes
    /// by the labels the agent currently reports (not the ones it reported on first connect).
    /// </summary>
    [Fact]
    public void ReRegister_WithNewLabels_ReplacesLabels()
    {
        var registry = CreateRegistry();
        registry.Register(Msg("agent-1", ["pool=a"]), "conn-1");
        registry.Register(Msg("agent-1", ["pool=b"]), "conn-2");

        registry.GetAgentsByLabel("pool", "b").Should().HaveCount(1,
            "the agent re-registered with pool=b and must now appear under that label");
        registry.GetAgentsByLabel("pool", "a").Should().BeEmpty(
            "the old label pool=a must have been replaced on re-registration");
        // TODO (WARNING): this only verifies the label index, not the stored Labels property.
        // A future bug where the index is updated but entry.Labels is not would go undetected.
        // Add: registry.GetByConnectionId("conn-2")!.Labels.Should().BeEquivalentTo(new[] { "pool=b" });
    }

    /// <summary>
    /// Rule 1: Register stores a defensive copy — mutating the caller's list after the call
    /// must not affect the entry's Labels.
    /// </summary>
    // TODO (WARNING): this test calls Register only once (first registration, add-factory path)
    // so it does not exercise the update-factory re-registration branch. The distributed
    // implementation has separate add/update code paths; a regression where the update factory
    // omits the defensive copy would go undetected. Add a prior Register("agent-1", "conn-1")
    // call before the conn-2 registration to also cover the re-registration path.
    [Fact]
    public void ReRegister_StoresCopyOfLabels()
    {
        var registry = CreateRegistry();
        var labels = new List<string> { "pool=a" };
        registry.Register(Msg("agent-1", labels), "conn-2");

        // Mutate the original list — must not affect the stored entry.
        labels[0] = "pool=changed";

        var entry = registry.GetByConnectionId("conn-2");
        entry.Should().NotBeNull();
        entry!.Labels.Should().BeEquivalentTo(new[] { "pool=a" },
            "Labels must be stored as an independent copy so caller mutations are isolated");
    }

    // ── Status — re-registration ──────────────────────────────────────────────

    /// <summary>
    /// Rule 2: re-registering an agent with no active job sets status to Idle, regardless of
    /// the previous status (e.g. Busy). This is the critical gap in the old in-memory rule.
    /// </summary>
    [Fact]
    public async Task ReRegister_WithoutActiveJob_SetsIdle()
    {
        var registry = CreateRegistry();
        registry.Register(Msg("agent-1"), "conn-1");
        registry.TransitionStatus(new AgentId("agent-1"), AgentStatus.Busy);

        // Re-register without an active job.
        registry.Register(Msg("agent-1"), "conn-2");

        var entry = await registry.GetByAgentIdAsync(new AgentId("agent-1"));
        entry.Should().NotBeNull();
        entry!.Status.Should().Be(AgentStatus.Idle,
            "re-registration with no ActiveJobId must always result in Idle (rule 2)");
        // TODO (WARNING): rule 2 also states that DisconnectedAt is cleared on re-registration.
        // Add: entry.DisconnectedAt.Should().BeNull("re-registration must clear DisconnectedAt (rule 2)");
        // A regression restoring the old in-memory guard (only clear inside the Disconnected branch)
        // would leave DisconnectedAt set when the previous status was Busy, which is undetected here.
    }

    /// <summary>
    /// Rule 2: re-registering an agent that has an active job keeps status Busy.
    /// </summary>
    [Fact]
    public async Task ReRegister_WithActiveJob_SetsBusy()
    {
        var registry = CreateRegistry();
        registry.Register(Msg("agent-1"), "conn-1");
        await registry.UpdateAgentFieldAsync(new AgentId("agent-1"), "activeJobId", "run-1");

        // Re-register — active job is still set.
        registry.Register(Msg("agent-1"), "conn-2");

        var entry = await registry.GetByAgentIdAsync(new AgentId("agent-1"));
        entry.Should().NotBeNull();
        entry!.Status.Should().Be(AgentStatus.Busy,
            "re-registration with a non-null ActiveJobId must result in Busy (rule 2)");
        // TODO (WARNING): rule 2 also states "when Busy, keep an existing BusySince or set it to now".
        // Add: entry.BusySince.Should().NotBeNull("re-registration with active job must set BusySince (rule 2)");
        // A regression where BusySince is left null when an agent re-registers with an active job
        // would go undetected here.
    }

    // ── UpdateAgentField — field-update rules ─────────────────────────────────

    /// <summary>
    /// Rule 5: a malformed (non-parseable) value for a typed field must be silently ignored —
    /// no exception is thrown and the previous value is preserved.
    /// </summary>
    [Fact]
    public async Task UpdateAgentField_MalformedValue_IsIgnored()
    {
        var registry = CreateRegistry();
        registry.Register(Msg("agent-1"), "conn-1");

        var ts = DateTimeOffset.UtcNow.ToString("O");
        await registry.UpdateAgentFieldAsync(new AgentId("agent-1"), "disabled", "true");
        await registry.UpdateAgentFieldAsync(new AgentId("agent-1"), "orphanRestoredAt", ts);

        // Act: send malformed values — must not throw and must not change the stored values.
        var act = async () =>
        {
            await registry.UpdateAgentFieldAsync(new AgentId("agent-1"), "disabled", "not-a-bool");
            await registry.UpdateAgentFieldAsync(new AgentId("agent-1"), "orphanRestoredAt", "not-a-date");
        };

        await act.Should().NotThrowAsync();

        var entry = await registry.GetByAgentIdAsync(new AgentId("agent-1"));
        entry.Should().NotBeNull();
        entry!.Disabled.Should().BeTrue("malformed disabled value must not overwrite the existing true");
        entry.OrphanRestoredAt.Should().Be(
            DateTimeOffset.Parse(ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            "malformed orphanRestoredAt value must not overwrite the existing timestamp");
    }

    /// <summary>
    /// Rule 4: an empty (or null) value clears the field to its zero value.
    /// </summary>
    [Fact]
    public async Task UpdateAgentField_EmptyValue_ClearsField()
    {
        var registry = CreateRegistry();
        registry.Register(Msg("agent-1"), "conn-1");
        await registry.UpdateAgentFieldAsync(new AgentId("agent-1"), "activeJobId", "run-1");

        await registry.UpdateAgentFieldAsync(new AgentId("agent-1"), "activeJobId", "");

        var entry = await registry.GetByAgentIdAsync(new AgentId("agent-1"));
        entry.Should().NotBeNull();
        entry!.ActiveJobId.Should().BeNull("empty value must clear the activeJobId field (rule 4)");
        // TODO (WARNING): this only tests activeJobId with an empty string "". It does not test:
        // - clearing with null (also documented as a clear operation in rule 4)
        // - clearing the disabled field (zero value is false, not null)
        // - clearing timestamp fields (orphanRestoredAt, lastJobCompletedAt)
        // A regression where the in-memory implementation handles null vs "" differently for typed
        // fields, or where disabled ignores null but not "", would go undetected.
    }

    /// <summary>
    /// Rule 6: an unknown field name must be silently ignored — no exception is thrown and
    /// existing fields are unaffected.
    /// </summary>
    [Fact]
    public async Task UpdateAgentField_UnknownField_IsIgnored()
    {
        var registry = CreateRegistry();
        registry.Register(Msg("agent-1"), "conn-1");
        await registry.UpdateAgentFieldAsync(new AgentId("agent-1"), "activeJobId", "run-1");

        var act = async () =>
            await registry.UpdateAgentFieldAsync(new AgentId("agent-1"), "nonExistentField", "x");

        await act.Should().NotThrowAsync();

        var entry = await registry.GetByAgentIdAsync(new AgentId("agent-1"));
        entry.Should().NotBeNull();
        entry!.ActiveJobId.Should().Be("run-1",
            "an unknown field must not corrupt existing fields (rule 6)");
    }

    // ── UpdateAgentField — field-update rules (cross-implementation parity) ──────

    /// <summary>
    /// AC: WithAgentField must apply LastJobCompletedAt (DistributedAgentRegistryService bug fix).
    /// Uses a non-UTC offset (+05:30) to also verify DateTimeStyles.RoundtripKind is used.
    /// </summary>
    [Fact]
    public async Task UpdateAgentField_LastJobCompletedAt_SetsProperty()
    {
        var registry = CreateRegistry();
        registry.Register(Msg("agent-1"), "conn-1");
        var agentId = new AgentId("agent-1");
        // Use an explicit non-UTC offset to verify RoundtripKind preserves the offset.
        var timestamp = new DateTimeOffset(2026, 3, 15, 10, 30, 0, TimeSpan.FromHours(5) + TimeSpan.FromMinutes(30));

        await registry.UpdateAgentFieldAsync(agentId, "lastJobCompletedAt", timestamp.ToString("O"));

        var entry = await registry.GetByAgentIdAsync(agentId);
        entry.Should().NotBeNull();
        entry!.LastJobCompletedAt.Should().NotBeNull(
            "lastJobCompletedAt must be stored by both implementations");
        entry.LastJobCompletedAt!.Value.Should().Be(timestamp,
            "the stored value must equal the input timestamp including offset (RoundtripKind)");
        // TODO (WARNING): DateTimeOffset.Be() compares the UTC instant, not the Offset property.
        // A regression where DateTimeStyles.RoundtripKind is absent and the parse converts +05:30 to UTC
        // (same instant, different .Offset) would still satisfy the assertion above. Add:
        //   entry.LastJobCompletedAt.Value.Offset.Should().Be(TimeSpan.FromHours(5) + TimeSpan.FromMinutes(30),
        //       "the stored Offset must exactly match the input offset, not be normalized to UTC");
        // (cf. UpdateAgentField_OrphanRestoredAt_WithExplicitOffset_PreservesOffset which has this assertion)
    }

    /// <summary>
    /// AC: empty value clears LastJobCompletedAt to null in both implementations.
    /// </summary>
    [Fact]
    public async Task UpdateAgentField_LastJobCompletedAt_ClearsOnEmpty()
    {
        var registry = CreateRegistry();
        registry.Register(Msg("agent-1"), "conn-1");
        var agentId = new AgentId("agent-1");
        var ts = DateTimeOffset.UtcNow.ToString("O");
        await registry.UpdateAgentFieldAsync(agentId, "lastJobCompletedAt", ts);

        await registry.UpdateAgentFieldAsync(agentId, "lastJobCompletedAt", "");

        var entry = await registry.GetByAgentIdAsync(agentId);
        entry.Should().NotBeNull();
        entry!.LastJobCompletedAt.Should().BeNull(
            "empty value must clear lastJobCompletedAt to null (rule 4)");
    }

    /// <summary>
    /// AC: empty string clears Disabled to false in both implementations.
    /// Previously broken in DistributedAgentRegistryService.WithAgentField where
    /// bool.TryParse("") returns false (parse failure) leaving the snapshot unchanged.
    /// </summary>
    [Fact]
    public async Task UpdateAgentField_Disabled_ClearsOnEmpty()
    {
        var registry = CreateRegistry();
        registry.Register(Msg("agent-1"), "conn-1");
        var agentId = new AgentId("agent-1");
        await registry.UpdateAgentFieldAsync(agentId, "disabled", "true");

        await registry.UpdateAgentFieldAsync(agentId, "disabled", "");

        var entry = await registry.GetByAgentIdAsync(agentId);
        entry.Should().NotBeNull();
        entry!.Disabled.Should().BeFalse(
            "empty value must clear disabled to false (rule 4); bool.TryParse(\"\") must not leave the old value");
    }

    /// <summary>
    /// AC: OrphanRestoredAt with an explicit non-UTC offset is stored with the original offset
    /// preserved, verifying DateTimeStyles.RoundtripKind is used in the apply path.
    /// Without RoundtripKind, DateTimeOffset.TryParse may produce a local-time-adjusted value
    /// whose .Offset differs from the original.
    /// </summary>
    [Fact]
    public async Task UpdateAgentField_OrphanRestoredAt_WithExplicitOffset_PreservesOffset()
    {
        var registry = CreateRegistry();
        registry.Register(Msg("agent-1"), "conn-1");
        var agentId = new AgentId("agent-1");
        // +05:30 is a real offset that differs from UTC, ensuring the round-trip is tested.
        var timestamp = new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.FromHours(5) + TimeSpan.FromMinutes(30));

        await registry.UpdateAgentFieldAsync(agentId, "orphanRestoredAt", timestamp.ToString("O"));

        var entry = await registry.GetByAgentIdAsync(agentId);
        entry.Should().NotBeNull();
        entry!.OrphanRestoredAt.Should().NotBeNull();
        entry.OrphanRestoredAt!.Value.Should().Be(timestamp,
            "OrphanRestoredAt must preserve the explicit UTC offset (+05:30) via DateTimeStyles.RoundtripKind");
        entry.OrphanRestoredAt.Value.Offset.Should().Be(TimeSpan.FromHours(5) + TimeSpan.FromMinutes(30),
            "the stored Offset must exactly match the input offset, not be normalized to UTC");
    }

    /// <summary>
    /// AC (snapshot path): after UpdateAgentFieldAsync sets lastJobCompletedAt, a TTL expiry
    /// followed by a heartbeat re-registers from the local snapshot. The re-registered entry
    /// must carry the updated lastJobCompletedAt value.
    /// This catches the DistributedAgentRegistryService bug where WithAgentField fell through
    /// to '_ => current' for LastJobCompletedAt, leaving the snapshot stale.
    /// Note: for the in-memory implementation this test is a no-op (Register is already live).
    /// </summary>
    // TODO (WARNING): For DistributedAgentRegistryService this test is also effectively a no-op for the
    // snapshot regression it claims to guard. Register(Msg("agent-1"), "conn-2") reads LastJobCompletedAt
    // directly from the live Redis hash (GetAgentRaw → existing?.LastJobCompletedAt), so the test passes
    // regardless of whether _localSnapshot was updated correctly. The actual regression path
    // (TTL expiry → UpdateHeartbeatAsync → re-register from _localSnapshot) is only exercised by
    // DistributedAgentRegistryServiceTests.UpdateHeartbeat_AfterTtlExpiry_ReRegistersWithUpdatedLastJobCompletedAt
    // (uses FakeRedisStore.ForceExpire). Do not rely on this contract test as the distributed regression guard.
    [Fact]
    public async Task UpdateAgentField_LastJobCompletedAt_SurvivesTtlExpiry()
    {
        var registry = CreateRegistry();
        registry.Register(Msg("agent-1"), "conn-1");
        var agentId = new AgentId("agent-1");
        var timestamp = new DateTimeOffset(2026, 4, 10, 8, 0, 0, TimeSpan.Zero);

        await registry.UpdateAgentFieldAsync(agentId, "lastJobCompletedAt", timestamp.ToString("O"));

        // Simulate TTL expiry: call Register again (re-registration from snapshot).
        // For DistributedAgentRegistryService, if _localSnapshot has the stale value,
        // the re-registration will lose the lastJobCompletedAt update.
        // For AgentRegistryService, re-registration reads from the live mutable entry — always correct.
        registry.Register(Msg("agent-1"), "conn-2");

        var entry = await registry.GetByAgentIdAsync(agentId);
        entry.Should().NotBeNull();
        entry!.LastJobCompletedAt.Should().Be(timestamp,
            "lastJobCompletedAt must survive a re-registration (TTL expiry path) in both implementations");
    }

    // ── TransitionStatus ──────────────────────────────────────────────────────

    /// <summary>
    /// A Disconnected → Busy transition must be rejected; the agent must re-register first.
    /// Both implementations already enforce this; this test guards against regression.
    /// </summary>
    [Fact]
    public void TransitionStatus_DisconnectedToBusy_IsRejected()
    {
        var registry = CreateRegistry();
        registry.Register(Msg("agent-1"), "conn-1");
        registry.TransitionStatus(new AgentId("agent-1"), AgentStatus.Disconnected);

        registry.TransitionStatus(new AgentId("agent-1"), AgentStatus.Busy);

        var entry = registry.GetByAgentId(new AgentId("agent-1"));
        entry.Should().NotBeNull();
        entry!.Status.Should().Be(AgentStatus.Disconnected,
            "Disconnected → Busy must be rejected; agent must re-register to resume Busy");
    }

    // ── GetAgentsByLabel ──────────────────────────────────────────────────────

    /// <summary>
    /// Label matching is case-insensitive: an agent registered with "Pool=A" must be found
    /// by GetAgentsByLabel("pool", "a").
    /// </summary>
    [Fact]
    public void GetAgentsByLabel_IgnoresCase()
    {
        var registry = CreateRegistry();
        registry.Register(Msg("agent-1", ["Pool=A"]), "conn-1");

        var result = registry.GetAgentsByLabel("pool", "a");

        result.Should().HaveCount(1,
            "label matching must be case-insensitive (Pool=A matches pool=a)");
    }
}

/// <summary>
/// Runs <see cref="AgentRegistryServiceContractTests"/> against the in-memory
/// <see cref="AgentRegistryService"/>.
/// </summary>
public sealed class InMemoryAgentRegistryServiceContractTests : AgentRegistryServiceContractTests
{
    protected override IAgentRegistryService CreateRegistry() =>
        new AgentRegistryService(Log.Logger);
}

/// <summary>
/// Runs <see cref="AgentRegistryServiceContractTests"/> against
/// <see cref="DistributedAgentRegistryService"/> backed by <see cref="FakeRedisStore"/>.
/// <para>
/// <see cref="FakeRedisStore"/> completes all async operations synchronously, so the
/// fire-and-forget <c>WriteRegistrationAsync</c> finishes before <c>Register</c> returns.
/// This makes async assertions deterministic without any sleep or retry logic.
/// </para>
/// </summary>
public sealed class DistributedAgentRegistryServiceContractTests : AgentRegistryServiceContractTests
{
    protected override IAgentRegistryService CreateRegistry() =>
        new DistributedAgentRegistryService(new FakeRedisStore(), Log.Logger);
}
