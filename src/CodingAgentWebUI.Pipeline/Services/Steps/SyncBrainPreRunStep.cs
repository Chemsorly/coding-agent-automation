using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Syncs the brain repository into the workspace (pre-run). Non-fatal on failure.
/// </summary>
internal sealed class SyncBrainPreRunStep : IPipelineStep
{
    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        if (context.BrainProvider is null || context.BrainSync is null)
            return StepResult.Continue;

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
