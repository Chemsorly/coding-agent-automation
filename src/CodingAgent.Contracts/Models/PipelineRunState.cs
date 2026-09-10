namespace CodingAgent.Pipeline.Models;

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
    /// Sentinel state returned by <c>PollCiWithNotStartedRetryAsync</c> when the PR branch
    /// is detected as conflicted (dirty) with main. Not a real CI pipeline state — used
    /// internally to signal <see cref="QualityGateExecutor"/> to skip further CI polling
    /// and set <c>run.FinalLabel = agent:next</c> for automatic re-dispatch.
    /// </summary>
    ConflictRestart
}
