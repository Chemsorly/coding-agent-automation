using System.Globalization;
using CodingAgent.Contracts;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Orchestration.Registry;

/// <summary>
/// Canonical field-apply and field-validate logic for <see cref="AgentEntry"/> fields
/// that are written through the <see cref="IAgentRegistryService.UpdateAgentFieldAsync"/> contract.
/// <para>
/// Both <see cref="AgentRegistryService"/> and <see cref="DistributedAgentRegistryService"/>
/// delegate to this helper so that the parse rules, clear-on-empty semantics, and
/// <see cref="DateTimeStyles.RoundtripKind"/> usage are identical across implementations.
/// </para>
/// </summary>
internal static class AgentEntryFieldApplier
{
    /// <summary>
    /// Returns a new <see cref="AgentEntry"/> with <paramref name="field"/> updated to
    /// <paramref name="value"/>.
    /// <para>
    /// Rules:
    /// <list type="bullet">
    ///   <item>Null or empty value → clears the field to its zero value (rule 4).</item>
    ///   <item>Malformed (non-parseable) value for a typed field → returns <paramref name="current"/> unchanged.</item>
    ///   <item>Unknown field → returns <paramref name="current"/> unchanged.</item>
    /// </list>
    /// </para>
    /// </summary>
    internal static AgentEntry Apply(AgentEntry current, string field, string? value) => field switch
    {
        AgentFieldNames.ActiveJobId =>
            current with { ActiveJobId = string.IsNullOrEmpty(value) ? null : value },

        AgentFieldNames.ActiveChatSessionId =>
            current with { ActiveChatSessionId = string.IsNullOrEmpty(value) ? null : value },

        AgentFieldNames.Disabled =>
            string.IsNullOrEmpty(value)
                ? current with { Disabled = false }
                : bool.TryParse(value, out var d)
                    ? current with { Disabled = d }
                    : current,

        AgentFieldNames.OrphanRestoredAt =>
            string.IsNullOrEmpty(value)
                ? current with { OrphanRestoredAt = null }
                : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ora)
                    ? current with { OrphanRestoredAt = ora }
                    : current,

        AgentFieldNames.LastJobCompletedAt =>
            string.IsNullOrEmpty(value)
                ? current with { LastJobCompletedAt = null }
                : DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ljc)
                    ? current with { LastJobCompletedAt = ljc }
                    : current,

        _ => current
    };

    /// <summary>
    /// Returns <c>true</c> when <paramref name="value"/> is valid for the given
    /// <paramref name="field"/>.
    /// <para>
    /// Null or empty is always valid (rule 4: clears the field).
    /// For timestamp fields a non-empty value must parse as an ISO-8601 round-trip date.
    /// For <c>disabled</c> a non-empty value must parse as a <see cref="bool"/>.
    /// String ID fields accept any non-empty value.
    /// </para>
    /// </summary>
    internal static bool IsValid(string field, string? value)
    {
        // Empty = clear operation — always valid (rule 4).
        if (string.IsNullOrEmpty(value)) return true;

        return field switch
        {
            AgentFieldNames.OrphanRestoredAt or AgentFieldNames.LastJobCompletedAt =>
                DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _),
            AgentFieldNames.Disabled =>
                bool.TryParse(value, out _),
            _ => true // string ID fields (activeJobId, activeChatSessionId) accept any non-empty string
        };
    }
}
