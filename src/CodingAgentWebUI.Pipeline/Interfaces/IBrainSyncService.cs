using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Handles brain repository synchronization: pre-run clone/pull, pre-write pull,
/// and post-run change detection/commit/push.
/// </summary>
public interface IBrainSyncService
{
    /// <summary>
    /// Clones or pulls the brain repository into the workspace .brain/ directory.
    /// </summary>
    Task SyncPreRunAsync(
        PipelineRun run, IRepositoryProvider brainProvider, string workspacePath,
        CancellationToken ct, Action<string>? onOutputLine = null);

    /// <summary>
    /// Pulls the brain repo before the agent writes lessons (minimizes merge conflicts).
    /// </summary>
    Task PullBeforeWriteAsync(
        PipelineRun run, IRepositoryProvider brainProvider, CancellationToken ct);

    /// <summary>
    /// Detects brain changes, validates, commits and pushes.
    /// </summary>
    Task SyncPostRunAsync(
        PipelineRun run, IRepositoryProvider brainProvider,
        CancellationToken ct, Action<string>? onOutputLine = null, int maxPushRetries = 3);
}
