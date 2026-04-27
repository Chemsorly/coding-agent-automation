using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

public interface IConfigurationStore
{
    Task<PipelineConfiguration> LoadPipelineConfigAsync(CancellationToken ct);
    Task SavePipelineConfigAsync(PipelineConfiguration config, CancellationToken ct);

    /// <summary>
    /// Atomically loads the current pipeline configuration, applies the transform function,
    /// and saves the result. Uses a lock to prevent concurrent save races.
    /// If the config file exists but is corrupted, throws <see cref="InvalidOperationException"/>
    /// instead of silently falling back to defaults.
    /// </summary>
    Task UpdatePipelineConfigAsync(Func<PipelineConfiguration, PipelineConfiguration> transform, CancellationToken ct);

    Task<IReadOnlyList<ProviderConfig>> LoadProviderConfigsAsync(ProviderKind kind, CancellationToken ct);
    Task SaveProviderConfigAsync(ProviderConfig config, CancellationToken ct);
    Task DeleteProviderConfigAsync(string id, ProviderKind kind, CancellationToken ct);
}
