using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Builds the rework prompt (if applicable) and delegates to
/// <see cref="AgentExecutionOrchestrator.ExecuteCodeGenerationAsync"/>.
/// </summary>
internal sealed class GenerateCodeStep : IPipelineStep
{
    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        string? reworkPromptOverride = null;
        if (context.Run.LinkedPullRequest is not null)
        {
            reworkPromptOverride = PromptBuilder.BuildReworkPrompt(
                context.Run.MergeConflictFiles,
                context.Run.LinkedPullRequest.ReviewComments,
                isDraft: context.Run.LinkedPullRequest.IsDraft);

            if (reworkPromptOverride is null)
            {
                context.EmitOutputLine("⏭️ No conflicts, review comments, or draft status — skipping code generation");
                context.Logger.Information(
                    "Pipeline {RunId} rework prompt is null (no conflicts, no comments, not draft), skipping code generation",
                    context.Run.RunId);
                return StepResult.Continue;
            }

            context.EmitOutputLine("⚙️ Starting rework code generation...");
        }
        else
        {
            context.EmitOutputLine("⚙️ Starting code generation...");
        }

        var shouldContinue = await context.AgentExecution.ExecuteCodeGenerationAsync(
            context.Run, context.Config, context.AgentProvider,
            context.Issue!, context.ParsedIssue!,
            context.Cts,
            context.TransitionTo,
            context.EmitOutputLine, context.NotifyChange,
            context.UpdateFileChangeStats,
            context.IssueOps,
            context.AddRunToHistory, ct,
            promptOverride: reworkPromptOverride);

        return shouldContinue ? StepResult.Continue : StepResult.Stop;
    }
}
