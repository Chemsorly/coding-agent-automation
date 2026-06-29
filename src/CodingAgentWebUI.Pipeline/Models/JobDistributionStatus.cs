namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Status reported by <see cref="Interfaces.IWorkDistributor.GetJobStatusAsync"/>
/// for a distributed work item.
/// </summary>
public enum JobDistributionStatus
{
    /// <summary>Status could not be determined (legacy mode or missing item).</summary>
    Unknown,

    /// <summary>Work item exists but has not been dispatched.</summary>
    Pending,

    /// <summary>Work item has been assigned to an agent/Job.</summary>
    Dispatched,

    /// <summary>Agent has reported execution in progress.</summary>
    Running,

    /// <summary>Work completed successfully.</summary>
    Succeeded,

    /// <summary>Work failed.</summary>
    Failed,

    /// <summary>Work was cancelled.</summary>
    Cancelled
}
