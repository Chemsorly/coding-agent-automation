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
    ConflictRestart,

    /// <summary>
    /// Sentinel state: the PR associated with this run was merged while the run was active.
    /// Run ends as <see cref="CodingAgent.Pipeline.Models.WorkItemStatus.Succeeded"/> — the
    /// work is done. No further commits, no LLM invocations.
    /// </summary>
    PrMerged = 6,

    /// <summary>
    /// Sentinel state: the PR associated with this run was closed without merging.
    /// Run ends as <see cref="CodingAgent.Pipeline.Models.WorkItemStatus.Cancelled"/>.
    /// </summary>
    PrClosed = 7
}
