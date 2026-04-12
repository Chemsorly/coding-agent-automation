using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Interfaces;

public interface IConfigurationStore
{
    Task<PipelineConfiguration> LoadPipelineConfigAsync(CancellationToken ct);
    Task SavePipelineConfigAsync(PipelineConfiguration config, CancellationToken ct);

    Task<IReadOnlyList<ProviderConfig>> LoadProviderConfigsAsync(ProviderKind kind, CancellationToken ct);
    Task SaveProviderConfigAsync(ProviderConfig config, CancellationToken ct);
    Task DeleteProviderConfigAsync(string id, ProviderKind kind, CancellationToken ct);
}
