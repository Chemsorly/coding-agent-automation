using System.Diagnostics;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Writes PR conversation context to the agent workspace when a linked PR exists (rework mode).
/// This enables review agents to see prior human feedback and review comments during the code review phase.
/// Non-fatal: if fetching or writing fails, the pipeline continues without conversation context.
/// </summary>
internal sealed class WritePrConversationContextStep : IPipelineStep
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

        try
        {
            // In rework mode the PR author is typically the bot — pass empty string
            // so all comments are included without special author attribution.
            var prAuthor = context.Run.ReviewPrAuthor ?? "";

            var comments = await context.RepoProvider.ListPullRequestCommentsAsync(prNumber, prAuthor, ct);

            var contextDir = Path.Combine(context.Run.WorkspacePath!, ".agent");
            Directory.CreateDirectory(contextDir);

            var content = PrConversationContextFormatter.Format(comments);
            var filePath = Path.Combine(context.Run.WorkspacePath!, AgentWorkspacePaths.PrConversationContextFilePath);
            await File.WriteAllTextAsync(filePath, content, ct);

            context.Logger.Information(
                "Wrote PR conversation context ({CommentCount} comments) to {FilePath} for PR #{PrNumber}",
                comments.Count, AgentWorkspacePaths.PrConversationContextFilePath, prNumber);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Activity.Current?.RecordError(ex);
            context.Logger.Warning(ex,
                "Failed to write PR conversation context for PR #{PrNumber}, review will proceed without it",
                prNumber);
        }

        return StepResult.Continue;
    }
}
