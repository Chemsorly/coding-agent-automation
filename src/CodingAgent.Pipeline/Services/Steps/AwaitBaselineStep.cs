namespace CodingAgent.Pipeline.Services.Steps;

/// <summary>
/// Waits for the workspace baseline that <see cref="VerifyBaselineStep"/> started, because the baseline must see
/// the code before the agent changes it. Sits between the analysis, which leaves the code alone, and code generation.
/// </summary>
public sealed class AwaitBaselineStep : IPipelineStep
{
    public string StepName => "AwaitBaseline";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        if (context.RunningBaseline is not { } baseline)
            return StepResult.Continue;

        // No TransitionTo here: going back to VerifyingBaseline would mark the analysis steps as revisited.
        if (!baseline.IsCompleted)
            context.Callbacks.EmitOutputLine("⏳ Analysis done, waiting for the workspace baseline...");

        context.Run.BaselineHealthPassed = await baseline.WaitAsync(ct);

        context.RunningBaseline = null;
        await baseline.StopAsync();
        return StepResult.Continue;
    }
}
