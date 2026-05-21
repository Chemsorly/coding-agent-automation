using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Executes quality gate validation with retry logic and external CI integration.
/// Abstraction over <c>QualityGateExecutor</c> for testability.
/// </summary>
public interface IQualityGateExecutor
{
    /// <summary>
    /// Runs quality gate validation with retry logic and PR creation.
    /// </summary>
    Task ProceedToQualityGatesAsync(QualityGateContext context, CancellationToken ct);

    /// <summary>
    /// Appends an external CI gate result to the quality gate report if external CI is enabled.
    /// </summary>
    Task<QualityGateReport> AppendExternalCiIfNeededAsync(
        QualityGateContext context,
        QualityGateReport report,
        bool allowEmptyCommit,
        CancellationToken ct,
        bool skipCiIfNoChanges = false);
}
