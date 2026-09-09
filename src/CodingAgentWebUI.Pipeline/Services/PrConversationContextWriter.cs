using System.Diagnostics;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Steps;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Shared helper that fetches PR conversation comments, formats them, and writes the
/// result to the agent workspace. Used by both <see cref="Steps.ExtractLinkedIssuesStep"/>
/// (review pipeline) and <see cref="Steps.WritePrConversationContextStep"/> (rework pipeline).
/// Non-fatal: if any step fails, the pipeline continues without the context file.
/// </summary>
public static class PrConversationContextWriter
{
    /// <summary>
    /// Fetches PR comments, formats them via <see cref="PrConversationContextFormatter"/>,
    /// and writes the result to <c>.agent/pr-conversation-context.md</c> in the workspace.
    /// On any non-cancellation exception the error is recorded and a warning is logged —
    /// the pipeline continues without the file (best-effort policy).
    /// </summary>
    public static async Task WriteAsync(PipelineStepContext context, int prNumber, CancellationToken ct)
    {
        try
        {
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
            Activity.Current?.RecordError(ex, ct);
            context.Logger.Warning(ex,
                "Failed to write PR conversation context for PR #{PrNumber}, review will proceed without it",
                prNumber);
        }
    }
}
