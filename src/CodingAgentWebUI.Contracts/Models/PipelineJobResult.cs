namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Result of a single CI/CD pipeline job within a run.
/// </summary>
public sealed class PipelineJobResult
{
    public required string Name { get; init; }
    public required PipelineRunState State { get; init; }
    public string? FailureReason { get; init; }
    public string? LogUrl { get; init; }

    /// <summary>Unique job identifier used to fetch logs from the CI provider.</summary>
    public long JobId { get; init; }

    /// <summary>
    /// Full raw log content fetched from the CI provider for failed jobs.
    /// Populated by the provider during enrichment. Written to disk by
    /// <see cref="CodingAgentWebUI.Pipeline.Services.CiLogWriter"/>; file paths are
    /// tracked externally in an <c>IReadOnlyDictionary&lt;long, string&gt;</c>.
    /// </summary>
    public string? LogContent { get; init; }
}
