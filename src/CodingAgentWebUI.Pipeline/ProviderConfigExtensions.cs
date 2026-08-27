using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline;

/// <summary>
/// Extension methods for <see cref="IReadOnlyList{ProviderConfig}"/> providing
/// centralized provider config lookup patterns, replacing duplicated
/// <c>FirstOrDefault(c =&gt; c.Id == someId)</c> call sites across Agent, Orchestration, and Hub.
/// </summary>
public static class ProviderConfigExtensions
{
    /// <summary>
    /// Finds a <see cref="ProviderConfig"/> by ID, returning <c>null</c> if not found.
    /// Use for optional provider configs where a missing entry is a valid scenario.
    /// </summary>
    /// <param name="configs">The list to search.</param>
    /// <param name="id">The provider config ID to look up.</param>
    /// <returns>The matching config, or <c>null</c> if no entry has the given ID.</returns>
    public static ProviderConfig? TryGetProviderConfig(this IReadOnlyList<ProviderConfig> configs, string id)
    {
        for (var i = 0; i < configs.Count; i++)
        {
            if (configs[i].Id == id)
                return configs[i];
        }

        return null;
    }

    /// <summary>
    /// Finds a <see cref="ProviderConfig"/> by ID, throwing <see cref="InvalidOperationException"/>
    /// if not found. Use for required provider configs where a missing entry is a programming error.
    /// </summary>
    /// <param name="configs">The list to search.</param>
    /// <param name="id">The provider config ID to look up.</param>
    /// <param name="configName">
    /// Human-readable name of this config type (e.g. <c>"Repository provider config"</c>),
    /// included in the exception message for diagnosability.
    /// </param>
    /// <returns>The matching config.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no entry with the given <paramref name="id"/> is found.
    /// </exception>
    public static ProviderConfig GetRequiredProviderConfig(
        this IReadOnlyList<ProviderConfig> configs, string id, string configName)
    {
        for (var i = 0; i < configs.Count; i++)
        {
            if (configs[i].Id == id)
                return configs[i];
        }

        throw new InvalidOperationException($"{configName} '{id}' not found in provider config list");
    }
}
