using System.Diagnostics;
using CodingAgent.Pipeline.Telemetry;
using Serilog.Context;

namespace CodingAgent.Pipeline.Services.Steps;

/// <summary>
/// Composes and executes an ordered list of pipeline steps.
/// The step list is explicit and configurable — callers build the list based on run context.
/// </summary>
public static class PipelineStepRunner
{
    /// <summary>
    /// Executes the given steps in order. Stops on the first <see cref="StepResult.Stop"/>.
    /// </summary>
    public static async Task ExecuteAsync(
        IReadOnlyList<IPipelineStep> steps, PipelineStepContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(context);

        foreach (var step in steps)
        {
            using var stepCtx = LogContext.PushProperty("StepName", step.StepName);
            StepResult result;
            try
            {
                result = await step.ExecuteAsync(context, ct);
            }
            catch (Exception ex)
            {
                Activity.Current?.RecordError(ex, ct);
                Serilog.Log.Error(ex, "Pipeline step {StepName} failed for run {RunId}", step.StepName, context.Run.RunId);
                throw;
            }

            if (result == StepResult.Stop)
                return;
        }
    }
}
