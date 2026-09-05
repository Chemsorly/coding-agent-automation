using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Writes PR conversation context to the agent workspace when a linked PR exists (rework mode).
/// This enables review agents to see prior human feedback and review comments during the code review phase.
/// Non-fatal: if fetching or writing fails, the pipeline continues without conversation context.
/// </summary>
public sealed class WritePrConversationContextStep : IPipelineStep
{
    public string StepName => "WritePrConversationContext";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        if (context.Run.LinkedPullRequest is null)
            return StepResult.Continue;

        using var activity = PipelineTelemetry.ActivitySource.StartActivity("WritePrConversationContext");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);
        activity?.SetTag("pipeline.pr_number", context.Run.LinkedPullRequest.Number);

        var prNumber = context.Run.LinkedPullRequest.Number;

        await PrConversationContextWriter.WriteAsync(context, prNumber, ct);

        return StepResult.Continue;
    }
}
