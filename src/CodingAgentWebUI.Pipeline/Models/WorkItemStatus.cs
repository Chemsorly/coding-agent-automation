namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Represents the lifecycle state of a work item in the distribution queue.
/// </summary>
public enum WorkItemStatus
{
    /// <summary>Work item created, awaiting dispatch.</summary>
    Pending,

    /// <summary>Assigned to an agent/K8s Job, awaiting execution start.</summary>
    Dispatched,

    /// <summary>Agent has reported execution in progress.</summary>
    Running,

    /// <summary>Agent completed work successfully.</summary>
    Succeeded,

    /// <summary>Work item failed (timeout, infrastructure, or agent error).</summary>
    Failed,

    /// <summary>Work item cancelled by user or system.</summary>
    Cancelled
}
