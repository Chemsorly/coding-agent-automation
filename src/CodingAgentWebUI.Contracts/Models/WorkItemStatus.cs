namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Represents the lifecycle state of a work item in the distribution queue.
/// <para>
/// ⚠️ DB CONTRACT: These ordinal values are used in a raw SQL partial unique index filter
/// in PipelineDbContext.cs: "Status" NOT IN (3, 4, 5). Do NOT reorder, rename with
/// different values, or insert new members mid-enum. Always append new members at the end
/// with the next sequential value.
/// </para>
/// </summary>
public enum WorkItemStatus
{
    /// <summary>Work item created, awaiting dispatch.</summary>
    Pending = 0,

    /// <summary>Assigned to an agent/K8s Job, awaiting execution start.</summary>
    Dispatched = 1,

    /// <summary>Agent has reported execution in progress.</summary>
    Running = 2,

    /// <summary>Agent completed work successfully.</summary>
    Succeeded = 3,

    /// <summary>Work item failed (timeout, infrastructure, or agent error).</summary>
    Failed = 4,

    /// <summary>Work item cancelled by user or system.</summary>
    Cancelled = 5
}
