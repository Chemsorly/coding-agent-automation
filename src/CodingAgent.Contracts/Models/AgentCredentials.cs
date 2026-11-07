namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Spec 056 DD4: what a run is billed to. A credential is derived from the routed profile: its job template's tool,
/// plus the auth mode for Claude Code. Every caller maps a tool to a credential through <see cref="For"/>.
/// </summary>
public static class AgentCredentials
{
    /// <summary>The Kiro subscription.</summary>
    public const string Kiro = "kiro";

    /// <summary>The OpenCode provider account.</summary>
    public const string Opencode = "opencode";

    /// <summary>The Claude subscription token (<c>claude setup-token</c>).</summary>
    public const string ClaudeSubscription = "claude-subscription";

    /// <summary>The Anthropic API key.</summary>
    public const string ClaudeApiKey = "claude-api-key";

    /// <summary>Every credential, in the order the UI lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Kiro, ClaudeSubscription, ClaudeApiKey, Opencode];

    /// <summary>True when <paramref name="credential"/> is one of <see cref="All"/> (ordinal).</summary>
    public static bool IsKnown(string? credential) =>
        credential is not null && All.Contains(credential, StringComparer.Ordinal);

    /// <summary>
    /// The credential of <paramref name="tool"/> (a job template's <c>providerType</c>) with a profile's
    /// <paramref name="settings"/>: Claude Code with <c>authMode</c> <c>apiKey</c> → <see cref="ClaudeApiKey"/>, any other
    /// Claude Code auth mode → <see cref="ClaudeSubscription"/> (D9). Null for an empty or unknown tool.
    /// </summary>
    public static string? For(string? tool, IReadOnlyDictionary<string, string>? settings)
    {
        var known = AgentToolCatalog.Find(tool);
        if (known is null)
            return null;
        if (ReferenceEquals(known, AgentToolCatalog.Kiro))
            return Kiro;
        if (ReferenceEquals(known, AgentToolCatalog.OpenCode))
            return Opencode;

        var authMode = settings is not null && settings.TryGetValue(ProviderSettingKeys.AuthMode, out var value)
            ? value?.Trim()
            : null;
        return string.Equals(authMode, ClaudeCodeAuthModes.ApiKey, StringComparison.OrdinalIgnoreCase)
            ? ClaudeApiKey
            : ClaudeSubscription;
    }

    /// <summary>
    /// The credential of <paramref name="profile"/>: the tool of its job template in <paramref name="toolsByKey"/>
    /// (<c>JobTemplateStore.ToolsByKey</c>) and its settings. Null when the template or its tool is unknown.
    /// </summary>
    public static string? For(AgentProfile profile, IReadOnlyDictionary<string, string> toolsByKey)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(toolsByKey);
        return toolsByKey.TryGetValue(profile.JobTemplateKey(), out var tool) ? For(tool, profile.Settings) : null;
    }

    /// <summary>The name of <paramref name="credential"/> in texts; the value itself when unknown.</summary>
    public static string DisplayName(string credential) => credential switch
    {
        Kiro => "Kiro",
        Opencode => "OpenCode",
        ClaudeSubscription => "Claude subscription",
        ClaudeApiKey => "Claude API key",
        _ => credential,
    };

    /// <summary>
    /// The credentials that at least one of <paramref name="profiles"/> uses, in the order of <see cref="All"/>.
    /// A profile whose job template is missing or has no known tool uses none.
    /// </summary>
    public static IReadOnlyList<string> InUse(IEnumerable<AgentProfile> profiles, IReadOnlyDictionary<string, string> toolsByKey)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(toolsByKey);
        var used = profiles.Select(p => For(p, toolsByKey)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return All.Where(used.Contains).ToList();
    }
}
