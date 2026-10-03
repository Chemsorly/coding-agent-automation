namespace CodingAgent.Pipeline.Models;

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
    /// Full raw log content fetched from the CI provider for unsuccessful jobs
    /// (see <see cref="EndedUnsuccessfully"/>).
    /// Populated by the provider during enrichment. Written to disk by
    /// <see cref="CodingAgent.Pipeline.Services.CiLogWriter"/>; file paths are
    /// tracked externally in an <c>IReadOnlyDictionary&lt;long, string&gt;</c>.
    /// </summary>
    public string? LogContent { get; init; }

    /// <summary>
    /// True when the job finished without succeeding: it failed, or it was cancelled before it
    /// could finish. GitHub reports a job that exceeds its <c>timeout-minutes</c> as cancelled,
    /// so a cancelled job can carry real test failures or a hung test in its log. When a newer
    /// commit superseded the run, the CI coordinator re-polls the new HEAD instead of reporting
    /// these jobs. A method rather than a property so serializers do not emit it.
    /// </summary>
    public bool EndedUnsuccessfully() => State is PipelineRunState.Failed or PipelineRunState.Cancelled;
}
