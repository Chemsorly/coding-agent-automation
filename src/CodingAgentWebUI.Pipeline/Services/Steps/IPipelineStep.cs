namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Represents a discrete step in the pipeline execution flow.
/// Each step is independently testable and composable.
/// </summary>
internal interface IPipelineStep
{
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
