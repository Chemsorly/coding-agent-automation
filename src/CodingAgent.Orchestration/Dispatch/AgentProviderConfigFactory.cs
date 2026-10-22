using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Orchestration.Dispatch;

/// <summary>
/// Spec 053 DD5, req 4.7: builds the agent provider config an agent receives from an agent profile and its route. The
/// shape is today's, so agents built before spec 053 run it unchanged: kind Agent, the tool's provider type, the
/// profile's tool settings plus <c>model</c> and <c>effort</c>, and the profile's Id as the config Id.
/// </summary>
public static class AgentProviderConfigFactory
{
    /// <param name="profile">The routed agent profile.</param>
    /// <param name="tool">The tool of the profile's job template (its <c>providerType</c>).</param>
    /// <param name="model">The route's model; <c>auto</c> lets the tool choose.</param>
    /// <param name="effort">The route's effort; <c>auto</c> is the tool's default.</param>
    /// <exception cref="ArgumentException">The tool is empty or unknown; the caller refuses before building.</exception>
    public static ProviderConfig Build(AgentProfile profile, string tool, string model, string effort)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var agentTool = AgentToolCatalog.Find(tool)
            ?? throw new ArgumentException($"'{tool}' is not a known agent tool.", nameof(tool));

        var settings = new Dictionary<string, string>(profile.Settings ?? new Dictionary<string, string>(), StringComparer.Ordinal)
        {
            [ProviderSettingKeys.Model] = model,
            [ProviderSettingKeys.Effort] = effort,
        };

        return new ProviderConfig
        {
            Id = profile.Id,
            DisplayName = profile.DisplayName,
            Kind = ProviderKind.Agent,
            ProviderType = agentTool.ProviderConfigType,
            Settings = settings,
        };
    }
}
