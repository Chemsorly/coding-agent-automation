using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Delegates to <see cref="AgentExecutionOrchestrator.ExecuteAnalysisPhaseAsync"/>.
/// Returns <see cref="StepResult.Stop"/> if the confidence gate rejects the issue.
/// </summary>
internal sealed class AnalyzeCodeStep : IPipelineStep
{
    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        context.Callbacks.EmitOutputLine("🔍 Starting analysis...");

        var phaseContext = context.BuildAgentPhaseContext();

        var shouldContinue = await context.AgentExecution.ExecuteAnalysisPhaseAsync(
            phaseContext, context.IssueComments ?? Array.Empty<IssueComment>(), ct);

        return shouldContinue ? StepResult.Continue : StepResult.Stop;
    }
}
