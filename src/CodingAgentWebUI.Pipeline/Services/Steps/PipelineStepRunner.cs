using System.Diagnostics;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Composes and executes an ordered list of pipeline steps.
/// The step list is explicit and configurable — callers build the list based on run context.
/// </summary>
internal static class PipelineStepRunner
{
    private static readonly Dictionary<string, string> StepSpanNames = new()
    {
        ["AnalyzeCodeStep"] = "AnalyzeIssue",
        ["GenerateCodeStep"] = "GenerateCode",
        ["RunQualityGatesStep"] = "RunQualityGates",
        ["ReviewCodeStep"] = "ReviewCode",
        ["CloneRepositoryStep"] = "CloneRepository",
        ["CreateBranchStep"] = "CreateBranch",
        ["SyncBrainPreRunStep"] = "SyncBrainPreRun",
        ["FetchIssueStep"] = "FetchIssue",
        ["DetectReworkStep"] = "DetectRework",
        ["BrainPullBeforeWriteStep"] = "BrainPullBeforeWrite",
        ["WriteMcpConfigStep"] = "WriteMcpConfig"
    };

    /// <summary>
    /// Executes the given steps in order. Stops on the first <see cref="StepResult.Stop"/>.
    /// Each step is wrapped in an OpenTelemetry activity span.
    /// </summary>
    public static async Task ExecuteAsync(
        IReadOnlyList<IPipelineStep> steps, PipelineStepContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(context);

        foreach (var step in steps)
        {
            var typeName = step.GetType().Name;
            var spanName = StepSpanNames.GetValueOrDefault(typeName, typeName);

            using var activity = PipelineTelemetry.ActivitySource.StartActivity(spanName);
            activity?.SetTag("pipeline.run_id", context.Run.RunId);
            activity?.SetTag("pipeline.step", spanName);

            var result = await step.ExecuteAsync(context, ct);
            if (result == StepResult.Stop)
            {
                activity?.SetTag("pipeline.step.result", "stop");
                return;
            }

            activity?.SetTag("pipeline.step.result", "continue");
        }
    }
}
