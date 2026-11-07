using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Spec 056 DD6 (req 2.5): how a new pause combines with the stored one. Pure.
/// </summary>
public static class CredentialPauseRules
{
    /// <summary>
    /// The entry to store when <paramref name="incoming"/> arrives while <paramref name="existing"/> is stored, or null
    /// for "no change". An expired or missing entry counts as none. A manual pause replaces anything; an automatic pause
    /// never replaces a manual one; an automatic pause over an automatic one keeps the stored entry and takes the later
    /// end time, where no end time ("until resumed") is the latest.
    /// </summary>
    public static AgentCredentialPause? Apply(AgentCredentialPause? existing, AgentCredentialPause incoming, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        if (existing is null || !existing.IsActive(now))
            return incoming;
        if (incoming.Source == CredentialPauseSources.Manual)
            return incoming;
        if (existing.Source == CredentialPauseSources.Manual)
            return null;

        // Auto over auto: only a later end time changes the stored entry.
        if (existing.Until is null)
            return null;
        if (incoming.Until is null || incoming.Until > existing.Until)
            return existing with { Until = incoming.Until };
        return null;
    }
}
