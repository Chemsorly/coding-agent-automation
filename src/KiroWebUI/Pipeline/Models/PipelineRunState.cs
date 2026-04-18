namespace KiroWebUI.Pipeline.Models;

/// <summary>
/// Represents the state of an external CI/CD pipeline run.
/// </summary>
public enum PipelineRunState
{
    Pending,
    Running,
    Passed,
    Failed,
    Cancelled
}
