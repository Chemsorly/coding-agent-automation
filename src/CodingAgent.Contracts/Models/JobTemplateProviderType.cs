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
    /// Returns <c>true</c> when the <paramref name="jobTemplateProviderType"/> and
    /// <paramref name="providerConfigType"/> are a known compatible pair, or when either value
    /// is null, empty, or not a known job-template provider type (no opinion = allow).
    /// </summary>
    /// <remarks>
    /// Known accepted pairs (case-insensitive on both sides):
    /// <list type="bullet">
    ///   <item><c>kiro</c> or <c>KiroCli</c> ↔ <c>KiroCli</c></item>
    ///   <item><c>opencode</c> or <c>OpenCode</c> ↔ <c>OpenCode</c></item>
    ///   <item><c>claude</c> or <c>ClaudeCode</c> ↔ <c>ClaudeCode</c></item>
    /// </list>
    /// </remarks>
    public static bool MatchesProviderConfigType(string? jobTemplateProviderType, string? providerConfigType)
    {
        if (string.IsNullOrEmpty(jobTemplateProviderType) || string.IsNullOrEmpty(providerConfigType))
            return true;

        // Map job-template provider type (case-insensitive) to the expected ProviderConfig type.
        string? expected = null;
        if (string.Equals(jobTemplateProviderType, Kiro, StringComparison.OrdinalIgnoreCase)
            || string.Equals(jobTemplateProviderType, ProviderTypes.KiroCli, StringComparison.OrdinalIgnoreCase))
        {
            expected = ProviderTypes.KiroCli;
        }
        else if (string.Equals(jobTemplateProviderType, Opencode, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(jobTemplateProviderType, ProviderTypes.OpenCode, StringComparison.OrdinalIgnoreCase))
        {
            expected = ProviderTypes.OpenCode;
        }
        else if (string.Equals(jobTemplateProviderType, Claude, StringComparison.OrdinalIgnoreCase)
                 || string.Equals(jobTemplateProviderType, ProviderTypes.ClaudeCode, StringComparison.OrdinalIgnoreCase))
        {
            expected = ProviderTypes.ClaudeCode;
        }

        // Unknown job-template provider type → no opinion.
        if (expected is null)
            return true;

        return string.Equals(providerConfigType, expected, StringComparison.OrdinalIgnoreCase);
    }
}
