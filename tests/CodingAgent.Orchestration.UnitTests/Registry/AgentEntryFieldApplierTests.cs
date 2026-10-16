using System.Globalization;
using AwesomeAssertions;
using CodingAgent.Contracts;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Orchestration.UnitTests.Registry;

/// <summary>
/// Characterization tests for <see cref="AgentEntryFieldApplier.Apply"/> and
/// <see cref="AgentEntryFieldApplier.IsValid"/>.
/// <para>
/// Tests call the static methods directly (via InternalsVisibleTo) because some paths —
/// notably malformed-value handling — are unreachable through
/// <see cref="AgentRegistryService.UpdateAgentFieldAsync"/> / <see cref="DistributedAgentRegistryService.UpdateAgentFieldAsync"/>,
/// which guard with <see cref="AgentEntryFieldApplier.IsValid"/> before calling Apply and
/// return early on invalid input.
/// The malformed-returns-unchanged invariant is a real production guarantee: it is also
/// exercised via DistributedAgentRegistryService.SetLocalSnapshotField (no IsValid guard).
/// </para>
/// </summary>
public sealed class AgentEntryFieldApplierTests
{
    private static readonly DateTimeOffset BaseTime =
        new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Creates a baseline <see cref="AgentEntry"/> with all fields at known values.</summary>
    private static AgentEntry MakeEntry() => new()
    {
        AgentId = new AgentId("agent-1"),
        ConnectionId = "conn-1",
        Hostname = "host-1",
        Labels = [],
        RegisteredAt = BaseTime,
        OrphanRestoredAt = BaseTime,
        LastJobCompletedAt = BaseTime,
        Disabled = true,
        ActiveJobId = "job-1",
        ActiveChatSessionId = "session-1"
    };

    // ── Apply — OrphanRestoredAt ───────────────────────────────────────────────

    [Fact]
    public void Apply_OrphanRestoredAt_ValidIso8601_SetsField()
    {
        var current = MakeEntry();
        var ts = new DateTimeOffset(2026, 6, 1, 8, 0, 0, TimeSpan.FromHours(2));

        var result = AgentEntryFieldApplier.Apply(current, AgentFieldNames.OrphanRestoredAt, ts.ToString("O"));

        result.OrphanRestoredAt.Should().NotBeNull();
        result.OrphanRestoredAt!.Value.Should().Be(ts,
            "a valid ISO-8601 round-trip timestamp must be parsed and stored");
        result.OrphanRestoredAt.Value.Offset.Should().Be(TimeSpan.FromHours(2),
            "DateTimeStyles.RoundtripKind must preserve the original UTC offset");
    }

    [Fact]
    public void Apply_OrphanRestoredAt_EmptyString_ClearsToNull()
    {
        var current = MakeEntry();

        var result = AgentEntryFieldApplier.Apply(current, AgentFieldNames.OrphanRestoredAt, "");

        result.OrphanRestoredAt.Should().BeNull(
            "empty string must clear OrphanRestoredAt to null (rule 4)");
    }

    [Fact]
    public void Apply_OrphanRestoredAt_NullValue_ClearsToNull()
    {
        var current = MakeEntry();

        var result = AgentEntryFieldApplier.Apply(current, AgentFieldNames.OrphanRestoredAt, null);

        result.OrphanRestoredAt.Should().BeNull(
            "null value must clear OrphanRestoredAt to null (rule 4)");
    }

    [Fact]
    public void Apply_OrphanRestoredAt_MalformedTimestamp_ReturnsEntryUnchanged()
    {
        var current = MakeEntry();

        var result = AgentEntryFieldApplier.Apply(current, AgentFieldNames.OrphanRestoredAt, "not-a-date");

        result.Should().BeSameAs(current,
            "a non-parseable timestamp must return the original entry reference unchanged");
        result.OrphanRestoredAt.Should().Be(BaseTime,
            "malformed timestamp must not overwrite the existing OrphanRestoredAt value");
    }

    // ── Apply — LastJobCompletedAt ─────────────────────────────────────────────

    [Fact]
    public void Apply_LastJobCompletedAt_ValidIso8601_SetsField()
    {
        var current = MakeEntry();
        var ts = new DateTimeOffset(2026, 9, 20, 14, 30, 0, TimeSpan.FromHours(-5));

        var result = AgentEntryFieldApplier.Apply(current, AgentFieldNames.LastJobCompletedAt, ts.ToString("O"));

        result.LastJobCompletedAt.Should().NotBeNull();
        result.LastJobCompletedAt!.Value.Should().Be(ts,
            "a valid ISO-8601 round-trip timestamp must be parsed and stored");
        result.LastJobCompletedAt.Value.Offset.Should().Be(TimeSpan.FromHours(-5),
            "DateTimeStyles.RoundtripKind must preserve the original UTC offset");
    }

    [Fact]
    public void Apply_LastJobCompletedAt_EmptyString_ClearsToNull()
    {
        var current = MakeEntry();

        var result = AgentEntryFieldApplier.Apply(current, AgentFieldNames.LastJobCompletedAt, "");

        result.LastJobCompletedAt.Should().BeNull(
            "empty string must clear LastJobCompletedAt to null (rule 4)");
    }

    [Fact]
    public void Apply_LastJobCompletedAt_NullValue_ClearsToNull()
    {
        var current = MakeEntry();

        var result = AgentEntryFieldApplier.Apply(current, AgentFieldNames.LastJobCompletedAt, null);

        result.LastJobCompletedAt.Should().BeNull(
            "null value must clear LastJobCompletedAt to null (rule 4)");
    }

    [Fact]
    public void Apply_LastJobCompletedAt_MalformedTimestamp_ReturnsEntryUnchanged()
    {
        var current = MakeEntry();

        var result = AgentEntryFieldApplier.Apply(current, AgentFieldNames.LastJobCompletedAt, "not-a-date");

        result.Should().BeSameAs(current,
            "a non-parseable timestamp must return the original entry reference unchanged");
        result.LastJobCompletedAt.Should().Be(BaseTime,
            "malformed timestamp must not overwrite the existing LastJobCompletedAt value");
    }

    // ── Apply — Disabled ──────────────────────────────────────────────────────

    [Fact]
    public void Apply_Disabled_ValidTrue_SetsTrue()
    {
        var current = MakeEntry() with { Disabled = false };

        var result = AgentEntryFieldApplier.Apply(current, AgentFieldNames.Disabled, "True");

        result.Disabled.Should().BeTrue("\"True\" must parse to a Disabled=true entry");
    }

    [Fact]
    public void Apply_Disabled_ValidFalse_SetsFalse()
    {
        var current = MakeEntry() with { Disabled = true };

        var result = AgentEntryFieldApplier.Apply(current, AgentFieldNames.Disabled, "False");

        result.Disabled.Should().BeFalse("\"False\" must parse to a Disabled=false entry");
    }

    [Fact]
    public void Apply_Disabled_EmptyString_ClearsToBoolFalse()
    {
        var current = MakeEntry() with { Disabled = true };

        var result = AgentEntryFieldApplier.Apply(current, AgentFieldNames.Disabled, "");

        result.Disabled.Should().BeFalse(
            "empty string must clear Disabled to its zero value (false) per rule 4");
    }

    [Fact]
    public void Apply_Disabled_MalformedBool_ReturnsEntryUnchanged()
    {
        var current = MakeEntry() with { Disabled = true };

        var result = AgentEntryFieldApplier.Apply(current, AgentFieldNames.Disabled, "not-a-bool");

        result.Should().BeSameAs(current,
            "a non-parseable bool must return the original entry reference unchanged");
        result.Disabled.Should().BeTrue(
            "malformed bool value must not overwrite the existing Disabled=true");
    }

    // ── Apply — ActiveJobId ───────────────────────────────────────────────────

    [Fact]
    public void Apply_ActiveJobId_ValidValue_SetsField()
    {
        var current = MakeEntry() with { ActiveJobId = null };

        var result = AgentEntryFieldApplier.Apply(current, AgentFieldNames.ActiveJobId, "run-99");

        result.ActiveJobId.Should().Be("run-99", "a non-empty string must be stored as ActiveJobId");
    }

    [Fact]
    public void Apply_ActiveJobId_EmptyString_ClearsToNull()
    {
        var current = MakeEntry();

        var result = AgentEntryFieldApplier.Apply(current, AgentFieldNames.ActiveJobId, "");

        result.ActiveJobId.Should().BeNull(
            "empty string must clear ActiveJobId to null (rule 4)");
    }

    [Fact]
    public void Apply_ActiveJobId_NullValue_ClearsToNull()
    {
        var current = MakeEntry();

        var result = AgentEntryFieldApplier.Apply(current, AgentFieldNames.ActiveJobId, null);

        result.ActiveJobId.Should().BeNull(
            "null value must clear ActiveJobId to null (rule 4)");
    }

    // ── Apply — ActiveChatSessionId ───────────────────────────────────────────

    [Fact]
    public void Apply_ActiveChatSessionId_ValidValue_SetsField()
    {
        var current = MakeEntry() with { ActiveChatSessionId = null };

        var result = AgentEntryFieldApplier.Apply(current, AgentFieldNames.ActiveChatSessionId, "session-42");

        result.ActiveChatSessionId.Should().Be("session-42",
            "a non-empty string must be stored as ActiveChatSessionId");
    }

    [Fact]
    public void Apply_ActiveChatSessionId_EmptyString_ClearsToNull()
    {
        var current = MakeEntry();

        var result = AgentEntryFieldApplier.Apply(current, AgentFieldNames.ActiveChatSessionId, "");

        result.ActiveChatSessionId.Should().BeNull(
            "empty string must clear ActiveChatSessionId to null (rule 4)");
    }

    // ── Apply — unknown field ─────────────────────────────────────────────────

    [Fact]
    public void Apply_UnknownField_ReturnsEntryUnchanged()
    {
        var current = MakeEntry();

        var result = AgentEntryFieldApplier.Apply(current, "nonExistentFieldXyz", "value");

        result.Should().BeSameAs(current,
            "an unknown field name must return the original entry reference unchanged");
    }

    // ── Apply — other fields not mutated ─────────────────────────────────────

    [Fact]
    public void Apply_OrphanRestoredAt_DoesNotMutateOtherFields()
    {
        var current = MakeEntry();
        var ts = DateTimeOffset.UtcNow;

        var result = AgentEntryFieldApplier.Apply(current, AgentFieldNames.OrphanRestoredAt, ts.ToString("O"));

        result.LastJobCompletedAt.Should().Be(current.LastJobCompletedAt,
            "updating OrphanRestoredAt must not change LastJobCompletedAt");
        result.ActiveJobId.Should().Be(current.ActiveJobId,
            "updating OrphanRestoredAt must not change ActiveJobId");
        result.Disabled.Should().Be(current.Disabled,
            "updating OrphanRestoredAt must not change Disabled");
    }

    [Fact]
    public void Apply_LastJobCompletedAt_DoesNotMutateOtherFields()
    {
        var current = MakeEntry();
        var ts = DateTimeOffset.UtcNow;

        var result = AgentEntryFieldApplier.Apply(current, AgentFieldNames.LastJobCompletedAt, ts.ToString("O"));

        result.OrphanRestoredAt.Should().Be(current.OrphanRestoredAt,
            "updating LastJobCompletedAt must not change OrphanRestoredAt");
        result.ActiveJobId.Should().Be(current.ActiveJobId,
            "updating LastJobCompletedAt must not change ActiveJobId");
        result.Disabled.Should().Be(current.Disabled,
            "updating LastJobCompletedAt must not change Disabled");
    }

    // ── IsValid — empty / null ─────────────────────────────────────────────────

    [Theory]
    [InlineData(AgentFieldNames.OrphanRestoredAt)]
    [InlineData(AgentFieldNames.LastJobCompletedAt)]
    [InlineData(AgentFieldNames.Disabled)]
    [InlineData(AgentFieldNames.ActiveJobId)]
    [InlineData(AgentFieldNames.ActiveChatSessionId)]
    public void IsValid_EmptyString_AlwaysReturnsTrue(string field)
    {
        AgentEntryFieldApplier.IsValid(field, "").Should().BeTrue(
            "empty string is always valid — it represents a clear operation (rule 4)");
    }

    [Theory]
    [InlineData(AgentFieldNames.OrphanRestoredAt)]
    [InlineData(AgentFieldNames.LastJobCompletedAt)]
    [InlineData(AgentFieldNames.Disabled)]
    [InlineData(AgentFieldNames.ActiveJobId)]
    [InlineData(AgentFieldNames.ActiveChatSessionId)]
    public void IsValid_NullValue_AlwaysReturnsTrue(string field)
    {
        AgentEntryFieldApplier.IsValid(field, null).Should().BeTrue(
            "null is always valid — it represents a clear operation (rule 4)");
    }

    // ── IsValid — OrphanRestoredAt ─────────────────────────────────────────────

    [Fact]
    public void IsValid_OrphanRestoredAt_ValidTimestamp_ReturnsTrue()
    {
        var ts = DateTimeOffset.UtcNow.ToString("O");

        AgentEntryFieldApplier.IsValid(AgentFieldNames.OrphanRestoredAt, ts).Should().BeTrue(
            "a valid ISO-8601 round-trip timestamp must be accepted");
    }

    [Fact]
    public void IsValid_OrphanRestoredAt_MalformedTimestamp_ReturnsFalse()
    {
        AgentEntryFieldApplier.IsValid(AgentFieldNames.OrphanRestoredAt, "not-a-date").Should().BeFalse(
            "a non-parseable value must be rejected for OrphanRestoredAt");
    }

    // ── IsValid — LastJobCompletedAt ───────────────────────────────────────────

    [Fact]
    public void IsValid_LastJobCompletedAt_ValidTimestamp_ReturnsTrue()
    {
        var ts = DateTimeOffset.UtcNow.ToString("O");

        AgentEntryFieldApplier.IsValid(AgentFieldNames.LastJobCompletedAt, ts).Should().BeTrue(
            "a valid ISO-8601 round-trip timestamp must be accepted");
    }

    [Fact]
    public void IsValid_LastJobCompletedAt_MalformedTimestamp_ReturnsFalse()
    {
        AgentEntryFieldApplier.IsValid(AgentFieldNames.LastJobCompletedAt, "not-a-date").Should().BeFalse(
            "a non-parseable value must be rejected for LastJobCompletedAt");
    }

    // ── IsValid — Disabled ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("True")]
    [InlineData("False")]
    [InlineData("true")]
    [InlineData("false")]
    public void IsValid_Disabled_ValidBool_ReturnsTrue(string value)
    {
        AgentEntryFieldApplier.IsValid(AgentFieldNames.Disabled, value).Should().BeTrue(
            "a parseable bool must be accepted for the Disabled field");
    }

    [Fact]
    public void IsValid_Disabled_MalformedBool_ReturnsFalse()
    {
        AgentEntryFieldApplier.IsValid(AgentFieldNames.Disabled, "yes").Should().BeFalse(
            "\"yes\" is not a valid bool and must be rejected for the Disabled field");
    }

    // ── IsValid — string ID fields ─────────────────────────────────────────────

    [Theory]
    [InlineData(AgentFieldNames.ActiveJobId, "run-1")]
    [InlineData(AgentFieldNames.ActiveChatSessionId, "session-1")]
    public void IsValid_StringIdField_AnyNonEmptyValue_ReturnsTrue(string field, string value)
    {
        AgentEntryFieldApplier.IsValid(field, value).Should().BeTrue(
            "string ID fields accept any non-empty value");
    }

    // ── IsValid — unknown field ────────────────────────────────────────────────

    [Fact]
    public void IsValid_UnknownField_AnyNonEmptyValue_ReturnsTrue()
    {
        AgentEntryFieldApplier.IsValid("nonExistentFieldXyz", "any-value").Should().BeTrue(
            "unknown fields fall through to the default arm which always returns true");
    }
}
