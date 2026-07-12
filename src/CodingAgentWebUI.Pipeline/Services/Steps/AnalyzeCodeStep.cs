using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Delegates to <see cref="AgentPhaseExecutor.ExecuteAnalysisPhaseAsync"/>.
/// Returns <see cref="StepResult.Stop"/> if the confidence gate rejects the issue.
/// </summary>
public sealed class AnalyzeCodeStep : IPipelineStep
{
    public string StepName => "AnalyzeCode";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("AnalyzeIssue");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);

        // Set staleness telemetry tags propagated from dispatch
        if (context.StalenessSignal is not null)
            activity?.SetTag("pipeline.analysis.staleness_signal", context.StalenessSignal);
        activity?.SetTag("pipeline.analysis.refresh_count", context.AnalysisRefreshCount);

        context.Callbacks.EmitOutputLine("🔍 Starting analysis...");

        var phaseContext = context.BuildAgentPhaseContext();

        var shouldContinue = await context.AgentExecution.ExecuteAnalysisPhaseAsync(
            phaseContext, context.IssueComments ?? Array.Empty<IssueComment>(),
            context.ForceRefreshAnalysis, ct);

        // Capture session ID for trace correlation
        try
        {
            var sessionId = await context.AgentProvider.GetLatestSessionIdAsync(context.Run.WorkspacePath!, ct);
            if (sessionId is not null)
                activity?.SetTag("pipeline.session_id", sessionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Non-critical — don't fail the pipeline for telemetry
        }

        activity?.SetTag("pipeline.analysis.continue", shouldContinue);
        return shouldContinue ? StepResult.Continue : StepResult.Stop;
    }
}
