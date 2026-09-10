using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

public interface IProviderConfigStore
{
    Task<IReadOnlyList<ProviderConfig>> LoadProviderConfigsAsync(ProviderKind kind, CancellationToken ct);
    Task<ProviderConfig?> GetProviderConfigByIdAsync(string id, ProviderKind kind, CancellationToken ct);
    Task SaveProviderConfigAsync(ProviderConfig config, CancellationToken ct);
    Task DeleteProviderConfigAsync(string id, ProviderKind kind, CancellationToken ct);
}
