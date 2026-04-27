using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

public interface IQualityGateValidator
{
    Task<QualityGateReport> ValidateAsync(string workspacePath, PipelineConfiguration config, CancellationToken ct);
}
