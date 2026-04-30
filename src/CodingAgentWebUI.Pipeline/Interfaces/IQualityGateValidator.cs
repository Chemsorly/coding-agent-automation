using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

public interface IQualityGateValidator
{
    /// <summary>
    /// Validates quality gates using a list of Quality Gate Configurations.
    /// Iterates QGCs in list order, stopping on first failure.
    /// </summary>
    Task<QualityGateReport> ValidateAsync(string workspacePath, IReadOnlyList<QualityGateConfiguration> qualityGateConfigs, CancellationToken ct);
}
