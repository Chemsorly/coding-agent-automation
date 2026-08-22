namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Configuration options for <see cref="DatabaseMaintenanceService"/> retention sweeps.
/// Binds from <c>WorkDistribution:Reconciliation</c> — the same config section previously
/// used by <c>ReconciliationServiceOptions</c> (deleted in arch-audit 2026-08-22).
/// </summary>
public sealed class DatabaseMaintenanceOptions
{
    /// <summary>
    /// Number of days after completion before terminal WorkItems are deleted.
    /// Default: 7 days.
    /// </summary>
    public int StaleRetentionDays { get; set; } = 7;

    /// <summary>
    /// Number of days after completion before PipelineRun records are deleted.
    /// Default: 30 days.
    /// </summary>
    public int PipelineRunRetentionDays { get; set; } = 30;

    /// <summary>
    /// Number of days after completion before ConsolidationRun records are deleted.
    /// Default: 30 days.
    /// </summary>
    public int ConsolidationRunRetentionDays { get; set; } = 30;
}
