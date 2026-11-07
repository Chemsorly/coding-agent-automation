using CodingAgent.Pipeline.Models;

namespace CodingAgent.Agent.OpenCode;

/// <summary>
/// Spec 056 req 5.4: the error codes and texts that mean the OpenCode provider account's quota or credit is used up.
/// OpenCode turns a stream <c>insufficient_quota</c> into "Quota exceeded. Check your plan and billing details." with
/// no status code, so the message counts as much as the response body. No reset time is read.
/// </summary>
internal static class OpenCodeQuotaText
{
    private static readonly string[] Markers =
    [
        "insufficient_quota",
        "credit_balance_exhausted",
        "organization_spend_limit_exceeded",
        "project_spend_limit_exceeded",
        "organization_usage_limit_exceeded",
        "enforced_spend_limit_reached",
        "quota exceeded",
        "credit balance is too low",
    ];

    /// <summary>True when <paramref name="text"/> carries one of the quota codes or texts (case-insensitive).</summary>
    public static bool IsQuota(string? text) =>
        !string.IsNullOrEmpty(text) && Markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The provider failure the agent reports for <paramref name="category"/> (unmasked; the recording decorator masks
    /// and cuts it), or null when the category pauses nothing.
    /// </summary>
    public static AgentProviderFailure? Failure(AgentErrorCategory category, string detail) => category switch
    {
        AgentErrorCategory.ProviderQuotaExhausted => new AgentProviderFailure(AgentProviderFailure.QuotaExhausted, null, detail),
        AgentErrorCategory.PermanentAuthFailure => new AgentProviderFailure(AgentProviderFailure.AuthFailed, null, detail),
        _ => null,
    };
}
