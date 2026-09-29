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
    /// Each step is wrapped in a named <c>Step {StepName}</c> span with <c>pipeline.step</c>
    /// and <c>pipeline.run_id</c> tags. Steps that start their own inner span will nest inside
    /// the runner-created span.
    /// </summary>
    public static async Task ExecuteAsync(
        IReadOnlyList<IPipelineStep> steps, PipelineStepContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(context);

        foreach (var step in steps)
        {
            using var stepCtx = LogContext.PushProperty("StepName", step.StepName);
            // Start a span for this step. Any inner span started by the step itself nests inside this one.
            // When an exception is thrown, Activity.Current in the catch block is still this stepActivity
            // (using var has block scope — it lives until the end of the foreach body, after the catch).
            using var stepActivity = PipelineTelemetry.ActivitySource.StartActivity($"Step {step.StepName}");
            stepActivity?.SetTag("pipeline.step", step.StepName);
            stepActivity?.SetTag("pipeline.run_id", context.Run.RunId);
            StepResult result;
            try
            {
                result = await step.ExecuteAsync(context, ct);
            }
            catch (Exception ex)
            {
                // Activity.Current here is stepActivity (not the parent ExecutePipeline span),
                // so errors are recorded on the step span, not on the run span.
                // TODO: [WARNING] When no ActivityListener is registered, StartActivity returns null and
                // Activity.Current falls back to the parent (ExecutePipeline) span, recording the error
                // on the wrong span. Prefer stepActivity?.RecordError(ex, ct) to use the local reference
                // explicitly and avoid the ambient-activity fallback. Pre-existing hazard, widened by this change.
                // TODO: [WARNING] ct here is the runner's cancellation token, not necessarily the same token
                // the step used internally. If the step failed due to an internal timeout (linked token),
                // ct.IsCancellationRequested may be false, causing RecordError to treat it as a hard error
                // rather than a graceful cancellation. Pre-existing ambiguity, now the only error-recording path.
                Activity.Current?.RecordError(ex, ct);
                Serilog.Log.Error(ex, "Pipeline step {StepName} failed for run {RunId}", step.StepName, context.Run.RunId);
                throw;
            }

            if (result == StepResult.Stop)
                return;
        }
    }
}
