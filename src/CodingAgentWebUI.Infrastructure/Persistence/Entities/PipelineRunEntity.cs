using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Infrastructure.Persistence.Entities;

/// <summary>
/// Persistent record of a pipeline execution. Maps to the "PipelineRuns" table.
/// </summary>
public class PipelineRunEntity
{
    public Guid RunId { get; set; }

    /// <summary>Associated work item ID, null for legacy/migrated runs.</summary>
    public Guid? WorkItemId { get; set; }

    public string IssueIdentifier { get; set; } = "";
    public string? IssueTitle { get; set; }

    /// <summary>
    /// Issue provider config ID captured at run-creation time.
    /// Used as a column-level consolidation discriminant in the read-time filter fallback path:
    /// when SummaryJson is null or corrupt, this column allows DeserializeSummary to set
    /// InitiatedBy correctly for consolidation ghost rows.
    /// Null for rows written before this column was added.
    /// </summary>
    public string? IssueProviderConfigId { get; set; }

    public PipelineStep FinalStep { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int RetryCount { get; set; }
    public string? PullRequestUrl { get; set; }
    public string? ModelName { get; set; }
    public string? AgentId { get; set; }
    public string? ProjectId { get; set; }
    public string? ProjectName { get; set; }
    public PipelineRunType RunType { get; set; }

    /// <summary>Full serialized PipelineRunSummary as JSONB for lossless round-trip.</summary>
    public string? SummaryJson { get; set; }

    /// <summary>Concurrency token mapped to PostgreSQL xmin system column.</summary>
    public uint RowVersion { get; set; }
}
