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
        try
        {
            await context.BrainSync.SyncPreRunAsync(
                context.Run, context.BrainProvider, context.Run.WorkspacePath!, ct, context.Callbacks.EmitOutputLine);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Warning(ex, "Pipeline {RunId} brain sync failed, continuing without brain context", context.Run.RunId);
            context.Run.BrainContextLoaded = false;
        }

        await context.Callbacks.ReportBrainSyncResult(context.Run.BrainContextLoaded, context.Run.BrainKnowledgeFileCount);

        return StepResult.Continue;
    }
}
