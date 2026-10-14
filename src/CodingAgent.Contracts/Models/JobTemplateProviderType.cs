namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Provider-type discriminator string constants and predicates for <see cref="CodingAgent.Pipeline.Models.JobTemplate.ProviderType"/>.
/// Centralises the <c>"kiro"</c> and <c>"opencode"</c> string literals so every assembly
/// that reads <c>JobTemplate.ProviderType</c> compares against the same constant with the
/// same casing semantics.
/// </summary>
/// <remarks>
/// This is distinct from <see cref="ProviderTypes"/>, which holds <c>ProviderConfig.ProviderType</c>
/// discriminators (e.g. <c>KiroCli</c>), and from <c>CodingAgent.Pipeline.Interfaces.AgentProviderType</c>,
/// which is an enum used inside the agent process.
/// </remarks>
public static class JobTemplateProviderType
{
    /// <summary>The <c>JobTemplate.ProviderType</c> value for Kiro CLI agent pods.</summary>
    public const string Kiro = "kiro";

    /// <summary>The <c>JobTemplate.ProviderType</c> value for OpenCode agent pods.</summary>
    public const string Opencode = "opencode";

    /// <summary>The <c>JobTemplate.ProviderType</c> value for Claude Code agent pods.</summary>
    public const string Claude = "claude";

    /// <summary>
    /// Returns <c>true</c> when <paramref name="providerType"/> identifies a Kiro CLI agent
    /// (case-insensitive match against <see cref="Kiro"/>).
    /// </summary>
    public static bool IsKiro(string? providerType)
        => string.Equals(providerType, Kiro, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="providerType"/> identifies an OpenCode agent
    /// (case-insensitive match against <see cref="Opencode"/>).
    /// </summary>
    public static bool IsOpencode(string? providerType)
        => string.Equals(providerType, Opencode, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <c>true</c> when the job template <paramref name="jobTemplateProviderType"/> and
    /// the agent provider config <paramref name="providerConfigType"/> are a valid pair, or when
    /// either value is null, empty, or unknown (no opinion, no check).
    /// <para>
    /// Accepted pairs (case-insensitive):
    /// <list type="table">
    ///   <item><term>kiro / KiroCli</term><description>KiroCli provider on a kiro template</description></item>
    ///   <item><term>opencode / OpenCode</term><description>OpenCode provider on an opencode template</description></item>
    ///   <item><term>claude / ClaudeCode</term><description>ClaudeCode provider on a claude template</description></item>
    /// </list>
    /// </para>
    /// </summary>
    public static bool MatchesProviderConfigType(string? jobTemplateProviderType, string? providerConfigType)
    {
        if (string.IsNullOrEmpty(jobTemplateProviderType) || string.IsNullOrEmpty(providerConfigType))
            return true;

        var jt = jobTemplateProviderType;
        var pc = providerConfigType;

        // Map job template type to canonical provider type
        string? canonicalTemplate;
        if (jt.Equals(Kiro, StringComparison.OrdinalIgnoreCase) ||
            jt.Equals(CodingAgent.Pipeline.Models.ProviderTypes.KiroCli, StringComparison.OrdinalIgnoreCase))
            canonicalTemplate = CodingAgent.Pipeline.Models.ProviderTypes.KiroCli;
        else if (jt.Equals(Opencode, StringComparison.OrdinalIgnoreCase) ||
                 jt.Equals(CodingAgent.Pipeline.Models.ProviderTypes.OpenCode, StringComparison.OrdinalIgnoreCase))
            canonicalTemplate = CodingAgent.Pipeline.Models.ProviderTypes.OpenCode;
        else if (jt.Equals(Claude, StringComparison.OrdinalIgnoreCase) ||
                 jt.Equals(CodingAgent.Pipeline.Models.ProviderTypes.ClaudeCode, StringComparison.OrdinalIgnoreCase))
            canonicalTemplate = CodingAgent.Pipeline.Models.ProviderTypes.ClaudeCode;
        else
            return true; // Unknown job template type — no opinion

        // Map provider config type to canonical provider type
        string? canonicalProvider;
        if (pc.Equals(CodingAgent.Pipeline.Models.ProviderTypes.KiroCli, StringComparison.OrdinalIgnoreCase))
            canonicalProvider = CodingAgent.Pipeline.Models.ProviderTypes.KiroCli;
        else if (pc.Equals(CodingAgent.Pipeline.Models.ProviderTypes.OpenCode, StringComparison.OrdinalIgnoreCase))
            canonicalProvider = CodingAgent.Pipeline.Models.ProviderTypes.OpenCode;
        else if (pc.Equals(CodingAgent.Pipeline.Models.ProviderTypes.ClaudeCode, StringComparison.OrdinalIgnoreCase))
            canonicalProvider = CodingAgent.Pipeline.Models.ProviderTypes.ClaudeCode;
        else
            return true; // Unknown provider config type — no opinion

        return string.Equals(canonicalTemplate, canonicalProvider, StringComparison.Ordinal);
    }
}
