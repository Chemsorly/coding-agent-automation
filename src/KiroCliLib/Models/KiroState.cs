namespace KiroCliLib.Models;

/// <summary>
/// Represents the execution state of Kiro CLI.
/// </summary>
public enum KiroState
{
    Started,
    ResearchPhase,
    PlanPhase,
    ImplementPhase,
    TestPhase,
    Completed,
    Error,
    NeedsInput,
    Timeout
}
