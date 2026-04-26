using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Services;

/// <summary>
/// Handles brain repository synchronization: pre-run clone/pull, post-run
/// change detection, validation, commit, and push. Extracted from
/// PipelineOrchestrationService to reduce file size.
/// </summary>
internal class BrainSyncOrchestrator
{
    private readonly IBrainUpdateService _brainUpdateService;
    private readonly Serilog.ILogger _logger;

    public BrainSyncOrchestrator(IBrainUpdateService brainUpdateService, Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(brainUpdateService);
        ArgumentNullException.ThrowIfNull(logger);
        _brainUpdateService = brainUpdateService;
        _logger = logger;
    }

    /// <summary>
    /// Clones or pulls the brain repository into the workspace .brain/ directory,
    /// ensures .gitignore entry, and counts knowledge files.
    /// </summary>
    // TODO: [ARC-10] Add ArgumentNullException.ThrowIfNull for public method parameters
    public async Task SyncPreRunAsync(
        PipelineRun run, IRepositoryProvider brainProvider, string workspacePath,
        CancellationToken ct, Action<string>? onOutputLine = null)
    {
        onOutputLine?.Invoke("🧠 Syncing brain repository...");
        var brainSw = System.Diagnostics.Stopwatch.StartNew();
        var brainPath = Path.Combine(workspacePath, ".brain");

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
        var updatedGitignore = IBrainUpdateService.EnsureGitignoreEntry(gitignoreContent, ".brain/");
        if (updatedGitignore != gitignoreContent)
        {
            await File.WriteAllTextAsync(gitignorePath, updatedGitignore, ct);
            _logger.Information("Pipeline {RunId} added .brain/ to .gitignore", run.RunId);
        }

        // Count knowledge files
        var brainFiles = Directory.Exists(brainPath)
            ? Directory.GetFiles(brainPath, "*.md", SearchOption.AllDirectories)
            : Array.Empty<string>();
        run.BrainKnowledgeFileCount = brainFiles.Length;
        run.BrainContextLoaded = true;

        _logger.Information(
            "Pipeline {RunId} brain sync complete: {BrainFileCount} knowledge files in {Duration}ms",
            run.RunId, run.BrainKnowledgeFileCount, brainSw.ElapsedMilliseconds);
        onOutputLine?.Invoke($"🧠 Brain context loaded: {run.BrainKnowledgeFileCount} knowledge files");
    }

    /// <summary>
    /// Pulls the brain repo before the agent writes lessons (minimizes merge conflicts).
    /// </summary>
    // TODO: [ARC-10] Add ArgumentNullException.ThrowIfNull for public method parameters
    public async Task PullBeforeWriteAsync(
        PipelineRun run, IRepositoryProvider brainProvider, CancellationToken ct)
    {
        var brainPath = Path.Combine(run.WorkspacePath!, ".brain");
        await brainProvider.PullAsync(brainPath, ct);
        _logger.Information("Pipeline {RunId} brain repo pulled before write phase", run.RunId);
    }

    /// <summary>
    /// Detects brain changes, validates, appends fallback log if needed, commits and pushes.
    /// </summary>
    // TODO: [ARC-10] Add ArgumentNullException.ThrowIfNull for public method parameters
    public async Task SyncPostRunAsync(
        PipelineRun run, IRepositoryProvider brainProvider,
        CancellationToken ct, Action<string>? onOutputLine = null)
    {
        var brainSw = System.Diagnostics.Stopwatch.StartNew();
        var brainPath = Path.Combine(run.WorkspacePath!, ".brain");
        var changedFiles = await _brainUpdateService.DetectChangesAsync(brainPath, ct);

        if (changedFiles.Count > 0)
        {
            var validation = _brainUpdateService.Validate(brainPath, run.RunId, changedFiles);
            run.BrainValidation = validation;

            if (!validation.OperationLogUpdated)
            {
                await _brainUpdateService.AppendFallbackLogEntryAsync(
                    brainPath, run.RunId, changedFiles, ct);
            }

            var syncResult = await _brainUpdateService.CommitAndPushAsync(
                brainPath, run.RunId, run.IssueIdentifier, brainProvider, ct);
            run.BrainUpdatesPushed = syncResult.Success;
            run.BrainFilesCommitted = syncResult.FilesCommitted;

            _logger.Information(
                "Pipeline {RunId} brain post-run sync: {Success}, {FileCount} files in {Duration}ms",
                run.RunId, syncResult.Success, syncResult.FilesCommitted, brainSw.ElapsedMilliseconds);
            onOutputLine?.Invoke($"🧠 Brain updates pushed: {syncResult.FilesCommitted} files committed");
        }
        else
        {
            run.BrainUpdatesPushed = false;
            _logger.Information("Pipeline {RunId} no brain changes detected, skipping commit", run.RunId);
            onOutputLine?.Invoke("🧠 No brain changes detected");
        }
    }

    /// <summary>
    /// Retrieves validation warnings from previous runs. Currently a stub.
    /// </summary>
    public IReadOnlyList<string>? GetPreviousBrainWarnings(string? brainProviderConfigId)
    {
        if (string.IsNullOrEmpty(brainProviderConfigId))
            return null;
        return null; // Feedback loop requires persisted validation — deferred to future enhancement
    }
}
