namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Pulls the latest brain repo state before the agent writes to it. Non-fatal on failure.
/// </summary>
internal sealed class BrainPullBeforeWriteStep : IPipelineStep
{
    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        if (context.BrainProvider is null || context.BrainSync is null
            || context.Config.Agent.BrainReadOnly || !context.Run.BrainContextLoaded)
            return StepResult.Continue;

        try { await context.BrainSync.PullBeforeWriteAsync(context.Run, context.BrainProvider, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Warning(ex, "Pipeline {RunId} brain repo pull-before-write failed, continuing", context.Run.RunId);
        }

        return StepResult.Continue;
    }
}
