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
    /// Writes the inbox text to .brain/&lt;relativePath&gt;, commits and pushes.
    /// Sets run.BrainInboxFile, BrainUpdatesPushed, BrainPushFailed, BrainInboxFileUrl.
    /// Non-fatal (T6).
    /// </summary>
    public async Task WriteInboxAsync(
        PipelineRun run, IRepositoryProvider brainProvider,
        string relativePath, string text,
        CancellationToken ct, int maxPushRetries = 3)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(brainProvider);

        try
        {
            var brainPath = Path.Combine(run.WorkspacePath!, AgentWorkspacePaths.BrainDirectory);
            var fullPath = Path.Combine(brainPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

            // Write inbox file with UTF-8 without BOM (T22)
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await File.WriteAllTextAsync(fullPath, text,
                new System.Text.UTF8Encoding(false), ct);

            var result = await _brainUpdateService.CommitInboxAndPushAsync(
                brainPath, relativePath,
                Brain.BrainLayout.InboxCommitMessage(run.RunId, $"#{run.IssueIdentifier}"),
                brainProvider, ct, maxPushRetries);

            run.BrainInboxFile = relativePath;
            run.BrainUpdatesPushed = result.Success;
            run.BrainPushFailed = !result.Success;

            // D22: web link of the commit that added the inbox file
            if (result.Success)
            {
                try
                {
                    var sha = await brainProvider.GetHeadCommitShaAsync(brainPath, ct);
                    run.BrainInboxFileUrl = brainProvider.GetCommitWebUrl(sha);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex, "Pipeline {RunId} could not read brain commit URL after inbox push: {Error}",
                        run.RunId, ex.Message);
                }
            }
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
