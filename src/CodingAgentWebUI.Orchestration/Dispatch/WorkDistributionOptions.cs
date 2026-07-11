namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Configuration options for work distribution dedup behavior.
/// Bound from "WorkDistribution:Dedup" configuration section.
/// </summary>
public sealed class WorkDistributionOptions
{
    /// <summary>
    /// Duration in minutes after a WorkItem reaches terminal state during which
    /// the same issue will not be re-dispatched by the closed-loop.
    /// Prevents restart-induced duplicate WorkItems. Default: 5.
    /// </summary>
    public int RestartCooldownMinutes { get; set; } = 5;
}
