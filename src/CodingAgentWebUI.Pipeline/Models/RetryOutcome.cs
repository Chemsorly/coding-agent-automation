namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Discrete outcome of a single retry-loop iteration, returned by
/// <see cref="Services.QualityGateExecutor.ClassifyRetryOutcome"/>.
/// </summary>
public enum RetryOutcome
{
    /// <summary>
    /// Normal attempt — proceed to QG validation and consume retry budget.
    /// </summary>
    Retry,

    /// <summary>
    /// Provider throttle/overload or absorbed agent exception — do NOT consume retry budget,
    /// delay and continue. Increments the consecutive-transient counter so the cap can fire.
    /// </summary>
    TransientWait,

    /// <summary>
    /// Permanent authentication failure — break the retry loop immediately.
    /// </summary>
    AbortAuth,

    /// <summary>
    /// Dead/exhausted session (agent returned success with zero tokens and no output) —
    /// clear session affinity and continue without running QG.
    /// </summary>
    RestartSession,
}
