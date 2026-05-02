namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Composes and executes an ordered list of pipeline steps.
/// The step list is explicit and configurable — callers build the list based on run context.
/// </summary>
internal static class PipelineStepRunner
{
    /// <summary>
    /// Executes the given steps in order. Stops on the first <see cref="StepResult.Stop"/>.
    /// </summary>
    public static async Task ExecuteAsync(
        IReadOnlyList<IPipelineStep> steps, PipelineStepContext context, CancellationToken ct)
    {
        foreach (var step in steps)
        {
            var result = await step.ExecuteAsync(context, ct);
            if (result == StepResult.Stop)
                return;
        }
    }
}
