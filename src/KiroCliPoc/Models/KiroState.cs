namespace KiroCliPoc.Models;

/// <summary>
/// Represents the execution state of Kiro CLI.
/// </summary>
public enum KiroState
{
    /// <summary>
    /// Kiro CLI process has started.
    /// </summary>
    Started,

    /// <summary>
    /// Kiro CLI is in the research phase.
    /// </summary>
    ResearchPhase,

    /// <summary>
    /// Kiro CLI is in the planning phase.
    /// </summary>
    PlanPhase,

    /// <summary>
    /// Kiro CLI is in the implementation phase.
    /// </summary>
    ImplementPhase,

    /// <summary>
    /// Kiro CLI is in the testing phase.
    /// </summary>
    TestPhase,

    /// <summary>
    /// Kiro CLI has completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// Kiro CLI encountered an error.
    /// </summary>
    Error,

    /// <summary>
    /// Kiro CLI is requesting human input.
    /// </summary>
    NeedsInput,

    /// <summary>
    /// Kiro CLI execution timed out.
    /// </summary>
    Timeout
}
