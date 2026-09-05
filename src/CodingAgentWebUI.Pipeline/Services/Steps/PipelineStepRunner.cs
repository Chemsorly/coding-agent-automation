using System.Diagnostics;
using System.Diagnostics.Metrics;
using CodingAgentWebUI.Pipeline.Telemetry;
using Serilog.Context;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Composes and executes an ordered list of pipeline steps.
/// The step list is explicit and configurable — callers build the list based on run context.
/// </summary>
public static class PipelineStepRunner
{
    /// <summary>
    /// Executes the given steps in order. Stops on the first <see cref="StepResult.Stop"/>.
    /// Records per-step duration and count metrics via <see cref="PipelineTelemetry"/>,
    /// or via the provided <paramref name="meter"/> when non-null (for test isolation).
    /// </summary>
    public static async Task ExecuteAsync(
        IReadOnlyList<IPipelineStep> steps, PipelineStepContext context, CancellationToken ct,
        Meter? meter = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(context);

        Histogram<double> stepDuration = meter is not null
            ? meter.CreateHistogram<double>("pipeline.step.duration", "s", "Duration of individual pipeline steps",
                advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [5, 15, 30, 60, 120, 300, 600, 900, 1200, 1800, 2700, 3600, 5400, 7200, 10800, 14400, 18000, 21600] })
            : PipelineTelemetry.StepDuration;

        Counter<long> stepCount = meter is not null
            ? meter.CreateCounter<long>("pipeline.step.count", "{step}", "Pipeline step execution count")
            : PipelineTelemetry.StepCount;

        foreach (var step in steps)
        {
            using var stepCtx = LogContext.PushProperty("StepName", step.StepName);
            var tags = PipelineTelemetry.BuildStepTags(step.StepName, context.Run.RunType, context.Run.ProjectId, context.Run.ProjectName);
            var sw = System.Diagnostics.Stopwatch.StartNew();
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
            finally
            {
                sw.Stop();
                stepDuration.Record(sw.Elapsed.TotalSeconds, tags);
                stepCount.Add(1, tags);
            }

            if (result == StepResult.Stop)
                return;
        }
    }
}
