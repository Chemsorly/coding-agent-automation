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
}
