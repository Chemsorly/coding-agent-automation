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
}
