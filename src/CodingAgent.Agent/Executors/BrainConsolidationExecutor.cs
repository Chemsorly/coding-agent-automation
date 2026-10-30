using System.Diagnostics;
using System.Text.RegularExpressions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Prompts;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Agent.Executors;

/// <summary>
/// Executes brain consolidation: clones the brain repo, runs the 4-phase consolidation
/// agent prompt, commits all changes, and pushes to the base branch.
/// </summary>
public sealed class BrainConsolidationExecutor : ConsolidationExecutorBase
{
    private readonly IBrainUpdateService _brainUpdateService;

    protected override string ExecutorName => "Brain consolidation";

    public BrainConsolidationExecutor(Serilog.ILogger logger, IBrainUpdateService brainUpdateService, ConsolidationProgress? progress = null) : base(logger, progress)
    {
        ArgumentNullException.ThrowIfNull(brainUpdateService);
        _brainUpdateService = brainUpdateService;
    }

    /// <summary>
    /// Executes the brain consolidation workflow:
    /// 1. Clone brain repo into the run's workspace (refused when the brain is read-only for the template)
    /// 2. Build 4-phase consolidation prompt
    /// 3. Execute agent with prompt in the cloned workspace
    /// 4. Produce diff summary and run adversarial review (if enabled)
    /// 5. Commit all changes via brainProvider.CommitAllAsync()
    /// 6. Push to the base branch via IBrainUpdateService.PushConsolidationAsync(), which merges the
    ///    lines other runs pushed to the brain in the meantime instead of failing
    /// 7. Parse agent output for metrics, format summary
    /// 8. Return ConsolidationJobResult with success and summary
    /// </summary>
    public async Task<ConsolidationJobResult> ExecuteAsync(
        ConsolidationJobMessage job,
        IRepositoryProvider brainProvider,
        IAgentProvider agentProvider,
        CancellationToken ct,
        Action<string>? onOutputLine = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(brainProvider);
        ArgumentNullException.ThrowIfNull(agentProvider);

        var invalid = ValidateJobId(job);
        if (invalid is not null) return invalid;

        // The trigger refuses a read-only brain; this covers a setting changed after the trigger. The
        // configuration is resolved at claim time: global, the project's override, then the template's flag.
        if (job.PipelineConfiguration.BrainReadOnly)
        {
            Logger.Warning("Brain consolidation run {RunId} not started: the brain is read-only for its template", job.JobId);
            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorMessage = "The brain is read-only for this template, so brain consolidation does not run from it."
            };
        }

        var workspacePath = ResolveWorkspacePath(job);

        return await WrapWithCancellationHandlingAsync(job.JobId, async () =>
        {
            // 1. Clone brain repo
            TransitionTo(PipelineStep.CloningRepository);
            Directory.CreateDirectory(workspacePath);
            Logger.Information("Cloning brain repo for consolidation run {RunId} into {Workspace}", // NOSONAR S6664 — one progress log per consolidation phase
                job.JobId, workspacePath);

            await RunWithTracingAsync("BrainConsolidation.Clone", job.JobId, async _ =>
            {
                await brainProvider.CloneAsync(workspacePath, ct);
            });

            // 1b. Archive v1 content (spec 054, Task 7A.2)
            try
            {
                var trackedFiles = (await _brainUpdateService.ListTrackedFilesAsync(workspacePath, ct))
                    .Select(p =>
                    {
                        string? firstLine = null;
                        var full = Path.Combine(workspacePath, p.Replace('/', Path.DirectorySeparatorChar));
                        try
                        {
                            using var sr = new System.IO.StreamReader(full, new System.Text.UTF8Encoding(false, true));
                            firstLine = sr.ReadLine();
                        }
                        catch { }
                        return new CodingAgent.Pipeline.Brain.BrainTrackedFile(p, firstLine);
                    }).ToList();

                var moves = CodingAgent.Pipeline.Brain.BrainV1Archiver.Plan(trackedFiles);
                foreach (var move in moves)
                {
                    var from = Path.Combine(workspacePath, move.From.Replace('/', Path.DirectorySeparatorChar));
                    var to = Path.Combine(workspacePath, move.To.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Move(from, to, overwrite: true);
                }

                var readmePath = Path.Combine(workspacePath, "README.md");
                var readmeContent = CodingAgent.Pipeline.Brain.BrainReadmeTemplate.Text;
                if (!File.Exists(readmePath) || await File.ReadAllTextAsync(readmePath, ct) != readmeContent)
                    await File.WriteAllTextAsync(readmePath, readmeContent, new System.Text.UTF8Encoding(false), ct);

                if (moves.Count > 0)
                {
                    await brainProvider.CommitAllAsync(workspacePath, CodingAgent.Pipeline.Brain.BrainLayout.ArchiveCommitMessage, ct);
                    Logger.Information("Brain consolidation archived {Count} v1 files for run {RunId}", moves.Count, job.JobId);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Warning(ex, "Brain archive step failed for run {RunId}, continuing", job.JobId);
            }

            // 2. Write review input (spec 054, Task 7A.3)
            var reviewInputWriter = new BrainReviewInputWriter(_brainUpdateService, Logger);
            await reviewInputWriter.WriteAsync(workspacePath, job.BrainGroup, ct);

            // 3. Build prompt
            TransitionTo(PipelineStep.ConsolidatingKnowledge);
            var prompt = ConsolidationPromptBuilder.BuildBrainReviewPrompt();

            // 3. Execute agent
            Logger.Information("Executing brain consolidation agent for run {RunId}", job.JobId);
            AgentResult agentResult;
            ConsolidationJobResult? failure;
            (agentResult, failure) = await RunWithTracingAsync("BrainConsolidation.AgentExecution", job.JobId, async _ =>
            {
                return await ExecuteAgentAndCheckAsync(
                    agentProvider,
                    new AgentRequest
                    {
                        Prompt = prompt,
                        WorkspacePath = workspacePath,
                        Timeout = job.PipelineConfiguration.AgentTimeout
                    },
                    job.JobId,
                    ct,
                    onOutputLine,
                    "brain_consolidation");
            });

            if (failure is not null) return failure;

            // 4. Diff summary and adversarial review
            AdversarialReviewResult? reviewResult = null;

            // 4a. Diff summary (wrapped for error isolation)
            TransitionTo(PipelineStep.ReviewingBrainChanges);
            var (skipReview, diffSummaryTokenUsage) = await TryGenerateDiffSummaryAsync(
                job, agentProvider, workspacePath, onOutputLine, ct);

            // 4b. Review step (only if diff summary succeeded)
            if (!skipReview)
            {
                reviewResult = await RunWithTracingAsync("BrainConsolidation.AdversarialReview", job.JobId, async _ =>
                {
                    return await AdversarialReviewHelper.ExecuteReviewAsync(
                        agentProvider,
                        workspacePath,
                        new AdversarialReviewPrompts(
                            ConsolidationPromptBuilder.BuildBrainConsolidationReviewPrompt(),
                            ConsolidationPromptBuilder.BuildBrainConsolidationRefinementPrompt(),
                            AgentWorkspacePaths.BrainConsolidationReviewFilePath),
                        new AdversarialReviewConfig
                        {
                            Enabled = job.PipelineConfiguration.BrainConsolidationReviewEnabled,
                            AgentTimeout = job.PipelineConfiguration.AgentTimeout
                        },
                        onOutputLine,
                        Logger,
                        ct);
                });
            }

            // 5. Capture token usage (preserved regardless of subsequent step outcomes)
            RecordUsage(diffSummaryTokenUsage, "brain_diff_summary");
            if (reviewResult is not null)
            {
                RecordUsage(reviewResult.ReviewAgentResult, "brain_review");
                RecordUsage(reviewResult.RefinementAgentResult, "brain_refinement");
            }
            var reviewTokenUsage = reviewResult?.ReviewTokenUsage;
            var refinementTokenUsage = reviewResult?.RefinementTokenUsage;

            // 6. Validate and record review outcome (spec 054, Task 7B)
            string? reviewRefusalReason = null;
            BrainConsolidationOutcome? reviewOutcome = null;
            try
            {
                var summaryPath = Path.Combine(workspacePath, CodingAgent.Pipeline.AgentWorkspacePaths.BrainReviewSummaryFilePath);
                var summaryJson = File.Exists(summaryPath) ? await File.ReadAllTextAsync(summaryPath, ct) : null;
                var parseResult = CodingAgent.Pipeline.Brain.BrainReviewSummaryParser.Parse(summaryJson);
                if (parseResult.Summary is not null)
                {
                    reviewOutcome = CodingAgent.Pipeline.Brain.BrainReviewSummaryParser.ToOutcome(parseResult.Summary);
                    reviewOutcome = CodingAgent.Pipeline.Brain.BrainReviewSummaryParser.Cap(reviewOutcome);
                }

                // Validate the working-tree changes
                var changes = _brainUpdateService.GetWorkingTreeChanges(workspacePath);
                var trackedFiles = await _brainUpdateService.ListTrackedFilesAsync(workspacePath, ct);
                var headContents = trackedFiles.ToDictionary(
                    p => p,
                    p => _brainUpdateService.ReadFileAtHead(workspacePath, p),
                    StringComparer.Ordinal);
                var newContents = new Dictionary<string, string?>(StringComparer.Ordinal);
                foreach (var c in changes)
                {
                    if (c.Kind == CodingAgent.Pipeline.Models.BrainPathChangeKind.Deleted)
                    {
                        newContents[c.Path] = null;
                        continue;
                    }
                    var fullChangedPath = Path.Combine(workspacePath, c.Path.Replace('/', Path.DirectorySeparatorChar));
                    newContents[c.Path] = File.Exists(fullChangedPath)
                        ? await File.ReadAllTextAsync(fullChangedPath, ct)
                        : null;
                }

                var validationInput = new CodingAgent.Pipeline.Brain.BrainReviewValidationInput(
                    changes, headContents, newContents,
                    GroupRepositoryNames: job.BrainGroup?.Select(m => m.RepositoryShortName).ToList() ?? [],
                    SummaryJson: summaryJson);
                var validation = CodingAgent.Pipeline.Brain.BrainReviewValidator.Validate(validationInput);
                if (!validation.IsValid)
                {
                    reviewRefusalReason = validation.Reason;
                    Logger.Warning("Brain review validation refused for run {RunId}: {Reason} ({Detail})",
                        job.JobId, validation.Reason, validation.Detail);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Brain review validation failed for run {RunId}, continuing", job.JobId);
            }

            // 7. Commit all changes (AFTER review/refinement completes)
            TransitionTo(PipelineStep.PushingBrainChanges);
            Logger.Information("Committing brain consolidation changes for run {RunId}", job.JobId);
            var commitMessage = reviewRefusalReason is null
                ? CodingAgent.Pipeline.Brain.BrainLayout.ReviewCommitMessage(job.JobId)
                : $"brain: review run {job.JobId} (refused: {reviewRefusalReason})";
            using (var commitActivity = PipelineTelemetry.ActivitySource.StartActivity("BrainConsolidation.Commit"))
            {
                commitActivity?.SetTag("pipeline.run_id", job.JobId);
                try
                {
                    await brainProvider.CommitAllAsync(workspacePath, commitMessage, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    commitActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    commitActivity?.AddException(ex);
                    Logger.Error(ex, "Commit failed for brain consolidation run {RunId}: {Message}", job.JobId, ex.Message);
                    return new ConsolidationJobResult
                    {
                        JobId = job.JobId,
                        Success = false,
                        ErrorMessage = $"Commit failed: {ex.Message}",
                        DiffSummaryTokenUsage = diffSummaryTokenUsage,
                        ReviewTokenUsage = reviewTokenUsage,
                        RefinementTokenUsage = refinementTokenUsage
                    };
                }
            }

            // 7. Push to base branch. Many repositories feed one brain, so their runs may have pushed
            //    lessons since the clone; the push then merges those lines in instead of failing.
            Logger.Information("Pushing brain consolidation changes for run {RunId}", job.JobId);
            int pushAttempts;
            using (var pushActivity = PipelineTelemetry.ActivitySource.StartActivity("BrainConsolidation.Push"))
            {
                pushActivity?.SetTag("pipeline.run_id", job.JobId);
                try
                {
                    pushAttempts = await _brainUpdateService.PushConsolidationAsync(
                        workspacePath, commitMessage, brainProvider, ct, job.PipelineConfiguration.BrainPushMaxRetries);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    pushActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    pushActivity?.AddException(ex);
                    Logger.Error(ex, "Push failed for brain consolidation run {RunId}: {Message}", job.JobId, ex.Message);
                    return new ConsolidationJobResult
                    {
                        JobId = job.JobId,
                        Success = false,
                        ErrorMessage = $"Push failed: {ex.Message}",
                        DiffSummaryTokenUsage = diffSummaryTokenUsage,
                        ReviewTokenUsage = reviewTokenUsage,
                        RefinementTokenUsage = refinementTokenUsage
                    };
                }
            }

            // Read the SHA of the commit that was just pushed. Failure here must never fail the run.
            string? pushedSha = null;
            try
            {
                pushedSha = await brainProvider.GetHeadCommitShaAsync(workspacePath, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Warning(ex, "Could not read the pushed brain commit for run {RunId}", job.JobId);
            }
            if (string.IsNullOrEmpty(pushedSha)) pushedSha = null;

            // 8. Parse metrics from agent output
            var responseText = string.Join("\n", agentResult.OutputLines);
            var (filesModified, entriesMerged, contradictionsResolved, entriesPruned) = ParseMetrics(responseText);

            // 9. Format summary and return
            var summary = reviewOutcome is not null
                ? ConsolidationPromptBuilder.FormatBrainReviewSummary(reviewOutcome)
                : ConsolidationPromptBuilder.FormatBrainConsolidationSummary(
                    filesModified, entriesMerged, contradictionsResolved, entriesPruned);

            Logger.Information("{ExecutorName} run {RunId} completed: {Summary}", ExecutorName, job.JobId, summary);

            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = true,
                Summary = summary,
                DiffSummaryTokenUsage = diffSummaryTokenUsage,
                ReviewTokenUsage = reviewTokenUsage,
                RefinementTokenUsage = refinementTokenUsage,
                BrainReviewRefusalReason = reviewRefusalReason,
                BrainOutcome = reviewOutcome ?? new BrainConsolidationOutcome
                {
                    FilesModified = filesModified,
                    EntriesMerged = entriesMerged,
                    ContradictionsResolved = contradictionsResolved,
                    EntriesPruned = entriesPruned,
                    PushedCommitSha = pushedSha,
                    PushedCommitUrl = pushedSha is null ? null : brainProvider.GetCommitWebUrl(pushedSha),
                    PushAttempts = pushAttempts,
                    BaseBranch = brainProvider.BaseBranch
                }
            };
        }, ct);
    }

    /// <summary>
    /// Generates the diff summary file and validates its content threshold.
    /// Returns (skipReview, tokenUsage).
    /// </summary>
    private async Task<(bool skipReview, TokenUsage? tokenUsage)> TryGenerateDiffSummaryAsync(
        ConsolidationJobMessage job, IAgentProvider agentProvider, string workspacePath,
        Action<string>? onOutputLine, CancellationToken ct)
    {
        using var diffActivity = PipelineTelemetry.ActivitySource.StartActivity("BrainConsolidation.DiffGeneration");
        diffActivity?.SetTag("pipeline.run_id", job.JobId);
        try
        {
            var diffFilePath = Path.Combine(workspacePath, AgentWorkspacePaths.BrainConsolidationDiffFilePath);
            if (File.Exists(diffFilePath)) File.Delete(diffFilePath);

            onOutputLine?.Invoke("📝 Requesting diff summary from generator...");
            var diffResult = await agentProvider.ExecuteAsync(
                new AgentRequest
                {
                    Prompt = ConsolidationPromptBuilder.BuildBrainConsolidationDiffPrompt(),
                    WorkspacePath = workspacePath,
                    Timeout = job.PipelineConfiguration.AgentTimeout,
                    UseResume = true
                }, ct, onOutputLine);

            if (!File.Exists(diffFilePath))
            {
                Logger.Warning("Diff summary file not produced at {Path}, skipping review", AgentWorkspacePaths.BrainConsolidationDiffFilePath);
                onOutputLine?.Invoke("⚠️ Diff summary file not produced — skipping review");
                return (skipReview: true, diffResult.Usage);
            }

            var diffContent = await File.ReadAllTextAsync(diffFilePath, ct);
            if (diffContent.Trim().Length < AdversarialReviewHelper.MinimumContentThreshold)
            {
                Logger.Warning("Diff summary file too short ({Length} chars), skipping review", diffContent.Trim().Length);
                onOutputLine?.Invoke($"⚠️ Diff summary too short ({diffContent.Trim().Length} chars) — skipping review");
                return (skipReview: true, diffResult.Usage);
            }

            return (skipReview: false, diffResult.Usage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            diffActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            diffActivity?.AddException(ex);
            Logger.Warning(ex, "Diff summary step failed: {Message}, skipping review entirely", ex.Message);
            onOutputLine?.Invoke($"⚠️ Diff summary failed: {ex.Message} — skipping review");
            return (skipReview: true, null);
        }
    }

    /// <summary>
    /// Parses consolidation metrics from the agent's output text.
    /// Looks for patterns like "Files modified: N", "Entries merged: N", etc.
    /// Returns zeros for any metrics not found.
    /// </summary>
    internal static (int FilesModified, int EntriesMerged, int ContradictionsResolved, int EntriesPruned) ParseMetrics(string responseText)
    {
        var filesModified = ExtractMetric(responseText, @"[Ff]iles?\s+modified\D*(\d+)");
        var entriesMerged = ExtractMetric(responseText, @"[Ee]ntries?\s+merged\D*(\d+)");
        var contradictionsResolved = ExtractMetric(responseText, @"[Cc]ontradictions?\s+resolved\D*(\d+)");
        var entriesPruned = ExtractMetric(responseText, @"[Ee]ntries?\s+pruned\D*(\d+)");

        return (filesModified, entriesMerged, contradictionsResolved, entriesPruned);
    }

    private static int ExtractMetric(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.None, TimeSpan.FromSeconds(2));
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : 0;
    }
}
