using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Handles brain repository synchronization: pre-run clone/pull and post-run inbox write.
/// Spec 054.
/// </summary>
public interface IBrainSyncService
{
    /// <summary>
    /// Clones or pulls the brain repository into the workspace .brain/ directory.
    /// </summary>
    Task SyncPreRunAsync(
        PipelineRun run, IRepositoryProvider brainProvider, WorkspacePath workspacePath,
        CancellationToken ct, Action<string>? onOutputLine = null);

    /// <summary>
    /// Writes the inbox file to the brain repo, commits and pushes (spec 054, req 3, D13).
    /// Sets run.BrainInboxFile, run.BrainUpdatesPushed, run.BrainPushFailed, run.BrainInboxFileUrl.
    /// Non-fatal (T6).
    /// </summary>
    Task WriteInboxAsync(
        PipelineRun run, IRepositoryProvider brainProvider,
        string relativePath, string text,
        CancellationToken ct, int maxPushRetries = 3);
}
