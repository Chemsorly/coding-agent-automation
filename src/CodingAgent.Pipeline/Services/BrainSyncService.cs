using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Handles brain repository synchronization: pre-run clone/pull, post-run inbox write.
/// Spec 054.
/// </summary>
public sealed class BrainSyncService : IBrainSyncService
{
    private readonly IBrainUpdateService _brainUpdateService;
    private readonly Serilog.ILogger _logger;

    public BrainSyncService(IBrainUpdateService brainUpdateService, Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(brainUpdateService);
        ArgumentNullException.ThrowIfNull(logger);
        _brainUpdateService = brainUpdateService;
        _logger = logger;
    }

    /// <summary>
    /// Clones or pulls the brain repository into the workspace .brain/ directory,
    /// ensures .gitignore entry, and marks the run as brain-context loaded.
    /// </summary>
    public async Task SyncPreRunAsync(
        PipelineRun run, IRepositoryProvider brainProvider, WorkspacePath workspacePath,
        CancellationToken ct, Action<string>? onOutputLine = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(brainProvider);
        ArgumentException.ThrowIfNullOrEmpty(workspacePath.Value, nameof(workspacePath));
        onOutputLine?.Invoke("🧠 Syncing brain repository...");
        var brainSw = System.Diagnostics.Stopwatch.StartNew();
        var brainPath = Path.Combine(workspacePath, AgentWorkspacePaths.BrainDirectory);

        if (Directory.Exists(brainPath))
        {
            await brainProvider.PullAsync(brainPath, ct);
            _logger.Information("Pipeline {RunId} brain repo pulled in {Duration}ms",
                run.RunId, brainSw.ElapsedMilliseconds);
        }
        else
        {
            await brainProvider.CloneAsync(brainPath, ct);
            _logger.Information("Pipeline {RunId} brain repo cloned in {Duration}ms",
                run.RunId, brainSw.ElapsedMilliseconds);
        }

        // Ensure .brain/ is in the code repo's .gitignore
        var gitignorePath = Path.Combine(workspacePath, ".gitignore");
        var gitignoreContent = File.Exists(gitignorePath)
            ? await File.ReadAllTextAsync(gitignorePath, ct)
            : "";
        var updatedGitignore = IBrainUpdateService.EnsureGitignoreEntry(gitignoreContent, AgentWorkspacePaths.BrainDirectory + "/");
        if (updatedGitignore != gitignoreContent)
        {
            await File.WriteAllTextAsync(gitignorePath, updatedGitignore, ct);
            _logger.Information("Pipeline {RunId} added .brain/ to .gitignore", run.RunId);
        }

        run.BrainKnowledgeFileCount = Directory.Exists(brainPath)
            ? Directory.EnumerateFiles(brainPath, "*.md", SearchOption.AllDirectories).Count()
            : 0;
        run.BrainContextLoaded = true;

        _logger.Information(
            "Pipeline {RunId} brain sync complete: {BrainFileCount} knowledge files in {Duration}ms",
            run.RunId, run.BrainKnowledgeFileCount, brainSw.ElapsedMilliseconds);
        onOutputLine?.Invoke("🧠 Brain synced");
    }

    /// <summary>
    /// Writes the inbox text to the brain repository through the provider's API. Reads nothing from .brain/,
    /// which the agent can change: .brain/ is read context only. Only this run's own path is admitted.
    /// Sets run.BrainInboxFile, BrainInboxText, BrainUpdatesPushed, BrainPushFailed, BrainInboxFileUrl.
    /// Non-fatal (T6).
    /// </summary>
    public async Task WriteInboxAsync(
        PipelineRun run, IRepositoryProvider brainProvider,
        string relativePath, string text,
        CancellationToken ct, int maxPushRetries = 3)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(brainProvider);
        ArgumentNullException.ThrowIfNull(relativePath);
        ArgumentNullException.ThrowIfNull(text);

        try
        {
            if (!Brain.BrainLayout.IsInboxPath(relativePath)
                || (run.BrainInboxFile is not null && run.BrainInboxFile != relativePath))
            {
                _logger.Warning("Pipeline {RunId} refused a brain inbox write to {InboxFile}", run.RunId, relativePath);
                run.BrainUpdatesPushed = false;
                run.BrainPushFailed = true;
                return;
            }

            // Set after the path guard: a refused call (different or invalid path) must not overwrite
            // the text from a previous successful write (DD8).
            run.BrainInboxText = text;

            var result = await _brainUpdateService.WriteInboxFileAsync(
                relativePath, text,
                Brain.BrainLayout.InboxCommitMessage(run.RunId, $"#{run.IssueIdentifier}"),
                brainProvider, ct, maxPushRetries);

            run.BrainInboxFile = relativePath;
            run.BrainUpdatesPushed = result.Success;
            run.BrainPushFailed = !result.Success;

            // D22: web link of the commit that added the inbox file
            if (result.Success && !string.IsNullOrEmpty(result.CommitSha))
                run.BrainInboxFileUrl = brainProvider.GetCommitWebUrl(result.CommitSha);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Pipeline {RunId} WriteInboxAsync failed: {Error}", run.RunId, ex.Message);
            run.BrainUpdatesPushed = false;
            run.BrainPushFailed = true;
        }
    }
}
