namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Pulls the latest brain repo state before the agent writes to it. Non-fatal on failure.
/// </summary>
internal sealed class BrainPullBeforeWriteStep : IPipelineStep
{
    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        if (context.BrainProvider is null || context.BrainSync is null
            || context.Config.BrainReadOnly || !context.Run.BrainContextLoaded)
            return StepResult.Continue;

        return await context.TryNonCriticalAsync(
            () => context.BrainSync.PullBeforeWriteAsync(context.Run, context.BrainProvider, ct),
            "brain repo pull-before-write", ct);
    }
}
