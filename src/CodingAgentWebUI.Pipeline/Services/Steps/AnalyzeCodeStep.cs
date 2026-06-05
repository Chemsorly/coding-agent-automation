using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Delegates to <see cref="AgentPhaseExecutor.ExecuteAnalysisPhaseAsync"/>.
/// Returns <see cref="StepResult.Stop"/> if the confidence gate rejects the issue.
/// </summary>
internal sealed class AnalyzeCodeStep : IPipelineStep
{
    public string StepName => "AnalyzeCode";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("AnalyzeIssue");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);

        context.Callbacks.EmitOutputLine("🔍 Starting analysis...");

        var phaseContext = context.BuildAgentPhaseContext();

        var shouldContinue = await context.AgentExecution.ExecuteAnalysisPhaseAsync(
            phaseContext, context.IssueComments ?? Array.Empty<IssueComment>(), ct);

        activity?.SetTag("pipeline.analysis.continue", shouldContinue);
        return shouldContinue ? StepResult.Continue : StepResult.Stop;
    }
}
