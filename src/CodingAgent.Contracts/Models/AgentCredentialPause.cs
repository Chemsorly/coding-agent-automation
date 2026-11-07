namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Spec 056 DD5: the pause of one credential (<see cref="AgentCredentials"/>). Stored as JSON, one key-value entry per
/// credential. A credential without an entry, or with an entry whose <see cref="Until"/> has passed, is Available.
/// </summary>
/// <param name="Credential">One of <see cref="AgentCredentials.All"/>.</param>
/// <param name="Reason">One of <see cref="CredentialPauseReasons"/>.</param>
/// <param name="Source">One of <see cref="CredentialPauseSources"/>.</param>
/// <param name="Since">When the pause began.</param>
/// <param name="Until">When it ends by itself; null = until resumed.</param>
/// <param name="Detail">The provider's line or the operator's note: at most 300 characters, masked and sanitized.</param>
/// <param name="WorkItemId">For an automatic pause: the work item that reported it.</param>
/// <param name="RunId">For an automatic pause: the run that reported it.</param>
/// <param name="PausedBy">For a manual pause: the user who set it.</param>
public sealed record AgentCredentialPause(
    string Credential,
    string Reason,
    string Source,
    DateTimeOffset Since,
    DateTimeOffset? Until,
    string Detail,
    Guid? WorkItemId = null,
    string? RunId = null,
    string? PausedBy = null)
{
    /// <summary>The longest <see cref="Detail"/> stored (req 2.2).</summary>
    public const int MaxDetailLength = 300;

    /// <summary>True while the pause applies: no end time, or an end time after <paramref name="now"/>.</summary>
    public bool IsActive(DateTimeOffset now) => Until is null || Until > now;
}

/// <summary>Spec 056: why a credential is paused.</summary>
public static class CredentialPauseReasons
{
    /// <summary>An agent reported that the provider's quota is used up.</summary>
    public const string QuotaExhausted = "quota_exhausted";

    /// <summary>An agent reported that the provider rejected the login.</summary>
    public const string AuthFailed = "auth_failed";

    /// <summary>An operator paused it.</summary>
    public const string Manual = "manual";

    /// <summary>How a reason reads in texts.</summary>
    public static string Text(string reason) => reason switch
    {
        QuotaExhausted => "quota used up",
        AuthFailed => "login rejected",
        Manual => "paused by an operator",
        _ => reason,
    };
}

/// <summary>Spec 056: who set a pause or ended it.</summary>
public static class CredentialPauseSources
{
    /// <summary>An agent's report.</summary>
    public const string Auto = "auto";

    /// <summary>A user.</summary>
    public const string Manual = "manual";

    /// <summary>A resume because the pause's end time passed (<c>resumes</c> counter only).</summary>
    public const string Reset = "reset";
}
