using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Syncs the brain repository into the workspace (pre-run). Non-fatal on failure.
/// </summary>
internal sealed class SyncBrainPreRunStep : IPipelineStep
{
    public string StepName => "SyncBrainPreRun";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("SyncBrainPreRun");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        activity?.SetTag("pipeline.run_type", context.Run.RunType.ToString());
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);

        if (context.BrainProvider is null || context.BrainSync is null)
        {
            activity?.SetTag("pipeline.brain_sync.skipped", true);
            return StepResult.Continue;
        }

        context.Callbacks.TransitionTo(PipelineStep.SyncingBrainRepoPreRun);
        await context.TryNonCriticalAsync(
            () => context.BrainSync.SyncPreRunAsync(
                context.Run, context.BrainProvider, context.Run.WorkspacePath!, ct, context.Callbacks.EmitOutputLine),
            "brain sync", ct,
            onFailure: () => context.Run.BrainContextLoaded = false);

        await context.Callbacks.ReportBrainSyncResult(context.Run.BrainContextLoaded, context.Run.BrainKnowledgeFileCount);

        return StepResult.Continue;
    }
}
