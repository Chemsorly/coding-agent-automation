namespace KiroWebUI.Pipeline.Models;

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
    /// Populated by the provider, consumed by the orchestration layer to write to disk.
    /// Not included in prompt text — the agent reads the file directly.
    /// </summary>
    public string? LogContent { get; set; }

    /// <summary>
    /// Path where the log was written to disk (relative to workspace root).
    /// Set by the orchestration layer after writing <see cref="LogContent"/> to the workspace.
    /// </summary>
    public string? LogFilePath { get; set; }
}
