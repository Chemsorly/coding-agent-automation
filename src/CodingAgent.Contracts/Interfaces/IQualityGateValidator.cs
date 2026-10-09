using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

public interface IQualityGateValidator
{
    /// <summary>
    /// Validates quality gates using a list of Quality Gate Configurations.
    /// Iterates QGCs in list order, stopping on first failure.
    /// When <paramref name="reportEvent"/> is provided, a <c>process_timeout</c> stall event is
    /// forwarded to the API whenever a QGC process exceeds its timeout (issue #2979).
    /// </summary>
    Task<QualityGateReport> ValidateAsync(
        WorkspacePath workspacePath,
        IReadOnlyList<QualityGateConfiguration> qualityGateConfigs,
        CancellationToken ct,
        Action<PipelineRunEventReport>? reportEvent = null);
}
