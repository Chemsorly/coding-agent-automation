using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Prompts;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Builds the rework prompt (if applicable) and delegates to
/// <see cref="AgentPhaseExecutor.ExecuteCodeGenerationAsync"/>.
/// </summary>
internal sealed class GenerateCodeStep : IPipelineStep
{
    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("GenerateCode");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        activity?.SetTag("pipeline.is_rework", context.Run.LinkedPullRequest is not null);

        string? reworkPromptOverride = null;
        if (context.Run.LinkedPullRequest is not null)
        {
            reworkPromptOverride = PromptBuilder.BuildReworkPrompt(
                context.Run.MergeConflictFiles,
                context.Run.LinkedPullRequest.ReviewComments,
                isDraft: context.Run.LinkedPullRequest.IsDraft,
                forceResolved: context.Run.MergeForceResolved);

            if (reworkPromptOverride is null)
            {
                context.Callbacks.EmitOutputLine("⏭️ No conflicts, review comments, or draft status — skipping code generation");
                context.Logger.Information(
                    "Pipeline {RunId} rework prompt is null (no conflicts, no comments, not draft), skipping code generation",
                    context.Run.RunId);
                return StepResult.Continue;
            }

            context.Callbacks.EmitOutputLine("⚙️ Starting rework code generation...");
        }
        else
        {
            context.Callbacks.EmitOutputLine("⚙️ Starting code generation...");
        }

        var phaseContext = context.BuildAgentPhaseContext();

        var shouldContinue = await context.AgentExecution.ExecuteCodeGenerationAsync(
            phaseContext, ct, promptOverride: reworkPromptOverride);

        return shouldContinue ? StepResult.Continue : StepResult.Stop;
    }
}
