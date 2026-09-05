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
    // TODO: Add ArgumentNullException.ThrowIfNull(context) guard — public static method should guard
    // against null context instead of letting NullReferenceException propagate from context.Run access.
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
            // TODO: If ct is cancelled after Directory.CreateDirectory but before WriteAllTextAsync completes,
            // OperationCanceledException propagates (correctly not swallowed) but leaves the .agent directory
            // partially created with no file. This matches the original behaviour and is not a regression,
            // but callers that need cleanup on cancellation should handle this themselves.
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
