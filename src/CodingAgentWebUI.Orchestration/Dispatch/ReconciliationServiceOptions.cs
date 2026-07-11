namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Configuration options for ReconciliationService.
/// Bound from "WorkDistribution:Reconciliation" section.
/// </summary>
public sealed class ReconciliationServiceOptions
{
    /// <summary>Interval between safety-net poll cycles in seconds. Default: 30.</summary>
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>Retention period for terminal work items in days. Default: 7.</summary>
    public int StaleRetentionDays { get; set; } = 7;

    /// <summary>Retention period for PipelineRuns in days. Default: 90.</summary>
    public int PipelineRunRetentionDays { get; set; } = 90;

    /// <summary>
    /// Proactive Watch reconnection interval in minutes.
    /// Avoids silent K8s API server timeouts. Default: 30.
    /// </summary>
    public int WatchReconnectIntervalMinutes { get; set; } = 30;

    /// <summary>K8s namespace to watch for Jobs. Defaults to POD_NAMESPACE or "default".</summary>
    public string Namespace { get; set; } = "default";

    /// <summary>
    /// Duration in seconds after Dispatched before warning about pod not Ready. Default: 60.
    /// </summary>
    public int PodStartupWarningSeconds { get; set; } = 60;

    /// <summary>
    /// Cooldown period in minutes for recently-terminal work items.
    /// Issues with WorkItems that reached terminal status within this window are treated as
    /// still-distributed (preventing re-dispatch on pod restart). Default: 5.
    /// </summary>
    public int RecentTerminalCooldownMinutes { get; set; } = 5;
}
