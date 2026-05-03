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
        if (context.Issue is null || context.ParsedIssue is null)
            throw new InvalidOperationException("Issue must be fetched before analysis. Ensure FetchIssueStep runs before AnalyzeCodeStep.");

        context.Callbacks.EmitOutputLine("🔍 Starting analysis...");

        var phaseContext = new AgentPhaseContext
        {
            Run = context.Run,
            Config = context.Config,
            AgentProvider = context.AgentProvider,
            Issue = context.Issue,
            ParsedIssue = context.ParsedIssue,
            IssueOps = context.IssueOps,
            Callbacks = context.Callbacks,
            OrchestratorCts = context.Cts
        };

        var shouldContinue = await context.AgentExecution.ExecuteAnalysisPhaseAsync(
            phaseContext, context.IssueComments ?? Array.Empty<IssueComment>(), ct);

        return shouldContinue ? StepResult.Continue : StepResult.Stop;
    }
}
