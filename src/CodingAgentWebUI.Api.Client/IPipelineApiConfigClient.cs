using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// Typed HTTP client for the /api/config endpoint group.
/// Method names are consumed verbatim by Specs 043 and 045.
/// </summary>
public interface IPipelineApiConfigClient
{
    // Pipeline config
    Task<PipelineConfiguration> GetPipelineConfigAsync(CancellationToken ct = default);
    Task SavePipelineConfigAsync(PipelineConfiguration config, CancellationToken ct = default);

    // Provider configs
    Task<IReadOnlyList<ProviderConfig>> GetProviderConfigsAsync(ProviderKind kind, CancellationToken ct = default);
    Task SaveProviderConfigAsync(ProviderConfig config, CancellationToken ct = default);
    Task DeleteProviderConfigAsync(string id, ProviderKind kind, CancellationToken ct = default);

    // Agent profiles
    Task<IReadOnlyList<AgentProfile>> GetAgentProfilesAsync(CancellationToken ct = default);
    Task SaveAgentProfileAsync(AgentProfile profile, CancellationToken ct = default);

    // Key-value store
    Task<string?> GetKeyValueAsync(string key, CancellationToken ct = default);
    Task SetKeyValueAsync(string key, string value, CancellationToken ct = default);
}
