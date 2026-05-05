using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

public interface IQualityGateConfigStore
{
    Task<IReadOnlyList<QualityGateConfiguration>> LoadQualityGateConfigsAsync(CancellationToken ct);
    Task SaveQualityGateConfigAsync(QualityGateConfiguration config, CancellationToken ct);
    Task DeleteQualityGateConfigAsync(string id, CancellationToken ct);
}
