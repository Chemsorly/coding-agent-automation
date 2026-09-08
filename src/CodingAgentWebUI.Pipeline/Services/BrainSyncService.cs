using System.Diagnostics.Metrics;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Handles brain repository synchronization: pre-run clone/pull, post-run
/// change detection, validation, commit, and push. Extracted from
/// PipelineOrchestrationService to reduce file size.
/// </summary>
public sealed class BrainSyncService : IBrainSyncService
{
    private readonly IBrainUpdateService _brainUpdateService;
    private readonly Serilog.ILogger _logger;

    // Instruments — default to global static instruments, injectable for test isolation.
    private readonly Counter<long> _brainSyncsCompleted;
    private readonly Histogram<double> _brainSyncDuration;
    private readonly Counter<long> _brainUpdatesCommitted;
    private readonly Counter<long> _brainFilesWritten;
    private readonly Counter<long> _brainUpdatesEmpty;

    public BrainSyncService(IBrainUpdateService brainUpdateService, Serilog.ILogger logger,
        IMeterFactory? meterFactory = null)
    {
        ArgumentNullException.ThrowIfNull(brainUpdateService);
        ArgumentNullException.ThrowIfNull(logger);
        _brainUpdateService = brainUpdateService;
        _logger = logger;

        if (meterFactory is not null)
        {
            var meter = meterFactory.Create(new MeterOptions(PipelineTelemetry.SourceName));
            _brainSyncsCompleted    = meter.CreateCounter<long>("brain.syncs.completed");
            _brainSyncDuration      = meter.CreateHistogram<double>("brain.sync.duration");
            _brainUpdatesCommitted  = meter.CreateCounter<long>("brain.updates.committed");
            _brainFilesWritten      = meter.CreateCounter<long>("brain.files.written");
            _brainUpdatesEmpty      = meter.CreateCounter<long>("brain.updates.empty");
        }
        else
        {
            _brainSyncsCompleted   = PipelineTelemetry.BrainSyncsCompleted;
            _brainSyncDuration     = PipelineTelemetry.BrainSyncDuration;
            _brainUpdatesCommitted = PipelineTelemetry.BrainUpdatesCommitted;
            _brainFilesWritten     = PipelineTelemetry.BrainFilesWritten;
            _brainUpdatesEmpty     = PipelineTelemetry.BrainUpdatesEmpty;
        }
    }

    /// <summary>
    /// Clones or pulls the brain repository into the workspace .brain/ directory,
    /// ensures .gitignore entry, and counts knowledge files.
    /// </summary>
    public async Task SyncPreRunAsync(
        PipelineRun run, IRepositoryProvider brainProvider, string workspacePath,
        CancellationToken ct, Action<string>? onOutputLine = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(brainProvider);
        ArgumentNullException.ThrowIfNull(workspacePath);
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

        // Count knowledge files (lazy enumeration avoids string[] allocation)
        run.BrainKnowledgeFileCount = Directory.Exists(brainPath)
            ? Directory.EnumerateFiles(brainPath, "*.md", SearchOption.AllDirectories).Count()
            : 0;
        run.BrainContextLoaded = true;

        _logger.Information(
            "Pipeline {RunId} brain sync complete: {BrainFileCount} knowledge files in {Duration}ms",
            run.RunId, run.BrainKnowledgeFileCount, brainSw.ElapsedMilliseconds);
        onOutputLine?.Invoke($"🧠 Brain context loaded: {run.BrainKnowledgeFileCount} knowledge files");

        _brainSyncsCompleted.Add(1);
        _brainSyncDuration.Record(brainSw.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Pulls the brain repo before the agent writes lessons (minimizes merge conflicts).
    /// </summary>
    public async Task PullBeforeWriteAsync(
        PipelineRun run, IRepositoryProvider brainProvider, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(brainProvider);
        var brainPath = Path.Combine(run.WorkspacePath!, ".brain");
        await brainProvider.PullAsync(brainPath, ct);
        _logger.Information("Pipeline {RunId} brain repo pulled before write phase", run.RunId);
    }

    /// <summary>
    /// Detects brain changes, validates, appends fallback log if needed, commits and pushes.
    /// </summary>
    public async Task SyncPostRunAsync(
        PipelineRun run, IRepositoryProvider brainProvider,
        CancellationToken ct, Action<string>? onOutputLine = null, int maxPushRetries = 3)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(brainProvider);
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
                brainPath, run.RunId, run.IssueIdentifier, brainProvider, ct, maxPushRetries);
            run.BrainUpdatesPushed = syncResult.Success;
            run.BrainFilesCommitted = syncResult.FilesCommitted;

            _logger.Information(
                "Pipeline {RunId} brain post-run sync: {Success}, {FileCount} files in {Duration}ms",
                run.RunId, syncResult.Success, syncResult.FilesCommitted, brainSw.ElapsedMilliseconds);
            onOutputLine?.Invoke($"🧠 Brain updates pushed: {syncResult.FilesCommitted} files committed");

            if (syncResult.Success)
            {
                _brainUpdatesCommitted.Add(1);
                _brainFilesWritten.Add(syncResult.FilesCommitted);
            }
        }
        else
        {
            run.BrainUpdatesPushed = false;
            _logger.Information("Pipeline {RunId} no brain changes detected, skipping commit", run.RunId);
            onOutputLine?.Invoke("🧠 No brain changes detected");

            _brainUpdatesEmpty.Add(1);
        }
    }
}
