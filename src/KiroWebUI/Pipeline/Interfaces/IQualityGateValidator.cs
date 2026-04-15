using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Interfaces;

public interface IQualityGateValidator
{
    Task<QualityGateReport> ValidateAsync(string workspacePath, PipelineConfiguration config, CancellationToken ct);
}
