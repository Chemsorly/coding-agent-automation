namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Represents the lifecycle state of a work item in the distribution queue.
/// <para>
/// ⚠️ DB CONTRACT: Ordinal values are used in a raw SQL partial unique index filter in PipelineDbContext:
/// <c>"Status" NOT IN (3, 4, 5)</c> corresponding to terminal statuses (Succeeded, Failed, Cancelled).
/// Do NOT reorder, rename, or insert members mid-enum — append new members at the end with the next sequential value.
/// Changing an existing ordinal silently breaks the DB index, allowing duplicate active work items.
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
