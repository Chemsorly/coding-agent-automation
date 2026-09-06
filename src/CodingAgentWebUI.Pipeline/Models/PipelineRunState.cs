namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Represents the state of an external CI/CD pipeline run.
/// </summary>
public enum PipelineRunState
{
    Pending,
    Running,
    Passed,
    Failed,
    Cancelled,
    /// <summary>
    /// The PR branch became conflicted with main while waiting for CI to start.
    /// The pipeline terminates without consuming a retry slot and re-queues the issue
    /// as <c>agent:next</c> so a fresh run can rebase and resolve the conflict.
    /// </summary>
    ConflictRestart
}
