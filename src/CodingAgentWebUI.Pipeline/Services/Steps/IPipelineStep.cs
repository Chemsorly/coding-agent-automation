namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Represents a discrete step in the pipeline execution flow.
/// Each step is independently testable and composable.
/// </summary>
internal interface IPipelineStep
{
    /// <summary>
    /// A stable identifier for this step, used as a metric tag value.
    /// Must be a constant PascalCase string that does not change across refactors.
    /// </summary>
    string StepName { get; }

    /// <summary>
    /// Executes this pipeline step.
    /// </summary>
    /// <returns><see cref="StepResult.Continue"/> to proceed to the next step,
    /// or <see cref="StepResult.Stop"/> to end the pipeline (graceful stop or failure handled internally).</returns>
    Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct);
}

/// <summary>
/// Result of a pipeline step execution.
/// </summary>
internal enum StepResult
{
    /// <summary>Proceed to the next step.</summary>
    Continue,

    /// <summary>Stop the pipeline (step handled the terminal state internally).</summary>
    Stop
}
