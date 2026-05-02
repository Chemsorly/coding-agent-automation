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
        context.EmitOutputLine("🔍 Starting analysis...");

        var shouldContinue = await context.AgentExecution.ExecuteAnalysisPhaseAsync(
            context.Run, context.Config, context.AgentProvider, context.IssueOps,
            context.Issue!, context.ParsedIssue!, context.IssueComments ?? Array.Empty<IssueComment>(),
            context.TransitionTo,
            context.AddRunToHistory,
            context.EmitOutputLine, context.NotifyChange, ct);

        return shouldContinue ? StepResult.Continue : StepResult.Stop;
    }
}
