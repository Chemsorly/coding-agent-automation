namespace CodingAgent.Contracts.Ui;

/// <summary>
/// Spec 056: one credential in use (<c>GET /api/ui/agent-credentials</c>): its pause, if any, the Pending items held
/// for it and the profiles that use it.
/// </summary>
/// <param name="Credential">One of <c>AgentCredentials.All</c>.</param>
/// <param name="DisplayName">Its name in the UI.</param>
/// <param name="Paused">True while a pause applies.</param>
/// <param name="Reason">The pause reason (<c>quota_exhausted</c>, <c>auth_failed</c>, <c>manual</c>); null when available.</param>
/// <param name="Source">Who paused it (<c>auto</c>, <c>manual</c>); null when available.</param>
/// <param name="Since">When the pause began.</param>
/// <param name="Until">When the pause ends by itself; null = until resumed.</param>
/// <param name="Detail">The provider's masked line or the operator's note.</param>
/// <param name="PausedBy">The user who paused it by hand.</param>
/// <param name="Text">The pause text the holds show; null when available.</param>
/// <param name="HeldItems">Pending items held because this credential is paused.</param>
/// <param name="Profiles">The display names of the profiles that use it.</param>
public sealed record UiAgentCredential(
    string Credential,
    string DisplayName,
    bool Paused,
    string? Reason,
    string? Source,
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    string? Detail,
    string? PausedBy,
    string? Text,
    int HeldItems,
    IReadOnlyList<string> Profiles);

/// <summary>Spec 056: the credentials page and banners (<c>GET /api/ui/agent-credentials</c>).</summary>
/// <param name="Credentials">Every credential at least one profile uses, plus any paused one.</param>
/// <param name="HeldByReason">Pending items with a stored hold, per hold reason.</param>
public sealed record UiAgentCredentialsPage(
    IReadOnlyList<UiAgentCredential> Credentials,
    IReadOnlyDictionary<string, int> HeldByReason);

/// <summary>
/// Spec 056: body of <c>POST /api/ui/agent-credentials/{credential}/pause</c>. <paramref name="Until"/> null pauses until
/// resumed; <paramref name="Note"/> is at most 200 characters.
/// </summary>
public sealed record UiCredentialPauseRequest(DateTimeOffset? Until, string? Note);
