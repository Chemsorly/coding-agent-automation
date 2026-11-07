using MessagePack;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Spec 056 DD11: the agent's report that its credential cannot be used — the quota is used up or the login was
/// rejected. Sent on a Failed completion (<see cref="JobCompletionPayload.ProviderFailure"/>,
/// <see cref="ConsolidationJobResult.ProviderFailure"/>); the API pauses the credential and queues the work again.
/// </summary>
/// <param name="Category"><see cref="QuotaExhausted"/> or <see cref="AuthFailed"/>.</param>
/// <param name="ResetsAt">When the provider said the quota resets; null when unknown.</param>
/// <param name="Detail">The provider's line, masked and cut to 300 characters.</param>
[MessagePackObject]
public sealed record AgentProviderFailure(
    [property: Key(0)] string Category,
    [property: Key(1)] DateTimeOffset? ResetsAt,
    [property: Key(2)] string Detail)
{
    /// <summary>The provider's quota is used up.</summary>
    public const string QuotaExhausted = CredentialPauseReasons.QuotaExhausted;

    /// <summary>The provider rejected the credential, or no credential is configured.</summary>
    public const string AuthFailed = CredentialPauseReasons.AuthFailed;

    /// <summary>The longest <see cref="Detail"/> an agent sends.</summary>
    public const int MaxDetailLength = AgentCredentialPause.MaxDetailLength;

    /// <summary>A failure of <paramref name="category"/> with <paramref name="detail"/> cut to <see cref="MaxDetailLength"/>.</summary>
    public static AgentProviderFailure Create(string category, DateTimeOffset? resetsAt, string? detail)
    {
        var text = (detail ?? string.Empty).Trim();
        return new AgentProviderFailure(
            category, resetsAt, text.Length <= MaxDetailLength ? text : text[..MaxDetailLength]);
    }
}
