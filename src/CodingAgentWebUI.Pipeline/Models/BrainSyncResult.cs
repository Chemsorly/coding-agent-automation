namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Result of committing and pushing brain repository changes after a pipeline run.
/// </summary>
public sealed class BrainSyncResult
{
    public bool Success { get; init; }
    public int FilesCommitted { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>True when no changes were detected and the commit was skipped.</summary>
    public bool WasSkipped { get; init; }
}
