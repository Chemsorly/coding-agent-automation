using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

public interface IProviderConfigStore
{
    Task<IReadOnlyList<ProviderConfig>> LoadProviderConfigsAsync(ProviderKind kind, CancellationToken ct);
    Task SaveProviderConfigAsync(ProviderConfig config, CancellationToken ct);
    Task DeleteProviderConfigAsync(string id, ProviderKind kind, CancellationToken ct);
}
