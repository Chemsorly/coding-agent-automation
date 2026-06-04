using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Composes and executes an ordered list of pipeline steps.
/// The step list is explicit and configurable — callers build the list based on run context.
/// </summary>
internal static class PipelineStepRunner
{
    /// <summary>
    /// Executes the given steps in order. Stops on the first <see cref="StepResult.Stop"/>.
    /// Records per-step duration and count metrics via <see cref="PipelineTelemetry"/>.
    /// </summary>
    public static async Task ExecuteAsync(
        IReadOnlyList<IPipelineStep> steps, PipelineStepContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(context);

        foreach (var step in steps)
        {
            var tags = PipelineTelemetry.BuildStepTags(step.StepName, context.Run);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            StepResult result;
            try
            {
                result = await step.ExecuteAsync(context, ct);
            }
            finally
            {
                sw.Stop();
                PipelineTelemetry.StepDuration.Record(sw.Elapsed.TotalSeconds, tags);
                PipelineTelemetry.StepCount.Add(1, tags);
            }

            if (result == StepResult.Stop)
                return;
        }
    }
}
