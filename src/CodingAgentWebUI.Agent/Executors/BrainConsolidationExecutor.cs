using System.Diagnostics;
using System.Text.RegularExpressions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Prompts;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Agent.Executors;

/// <summary>
/// Executes brain consolidation: clones the brain repo, runs the 4-phase consolidation
/// agent prompt, commits all changes, and pushes to the base branch.
/// </summary>
public sealed class BrainConsolidationExecutor : ConsolidationExecutorBase
{
    protected override string WorkspaceSuffix => "brain";
    protected override string ExecutorName => "Brain consolidation";

    public BrainConsolidationExecutor(Serilog.ILogger logger) : base(logger)
    {
    }

    /// <summary>
    /// Executes the brain consolidation workflow:
    /// 1. Clone brain repo into temp workspace
    /// 2. Build 4-phase consolidation prompt
    /// 3. Execute agent with prompt in the cloned workspace
    /// 4. Produce diff summary and run adversarial review (if enabled)
    /// 5. Commit all changes via brainProvider.CommitAllAsync()
    /// 6. Push via brainProvider.PushBranchAsync() to base branch
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

        var workspacePath = ResolveWorkspacePath(job);

        return await WrapWithCancellationHandlingAsync(job.JobId, async () =>
        {
            // 1. Clone brain repo
            Directory.CreateDirectory(workspacePath);
            Logger.Information("Cloning brain repo for consolidation run {RunId} into {Workspace}",
                job.JobId, workspacePath);

            await RunWithTracingAsync("BrainConsolidation.Clone", job.JobId, async _ =>
            {
                await brainProvider.CloneAsync(workspacePath, ct);
            });

            // 2. Build prompt
            var prompt = ConsolidationPromptBuilder.BuildBrainConsolidationPrompt(job.LastSuccessfulRunUtc);

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
                    ct);
            });

            if (failure is not null) return failure;

            // 4. Diff summary and adversarial review
            TokenUsage? diffSummaryTokenUsage = null;
            AdversarialReviewResult? reviewResult = null;
            var skipReview = false;

            // 4a. Diff summary step (wrapped in try-catch for error isolation)
            using (var diffActivity = PipelineTelemetry.ActivitySource.StartActivity("BrainConsolidation.DiffGeneration"))
            {
                diffActivity?.SetTag("pipeline.run_id", job.JobId);
                try
                {
                    // Delete stale diff file before requesting new one
                    var diffFilePath = Path.Combine(workspacePath, AgentWorkspacePaths.BrainConsolidationDiffFilePath);
                    if (File.Exists(diffFilePath))
                        File.Delete(diffFilePath);

                    onOutputLine?.Invoke("📝 Requesting diff summary from generator...");

                    var diffResult = await agentProvider.ExecuteAsync(
                        new AgentRequest
                        {
                            Prompt = ConsolidationPromptBuilder.BuildBrainConsolidationDiffPrompt(),
                            WorkspacePath = workspacePath,
                            Timeout = job.PipelineConfiguration.AgentTimeout,
                            UseResume = true
                        },
                        ct);

                    diffSummaryTokenUsage = diffResult.Usage;

                    // Check if diff summary file meets minimum content threshold
                    if (!File.Exists(diffFilePath))
                    {
                        Logger.Warning("Diff summary file not produced at {Path}, skipping review", AgentWorkspacePaths.BrainConsolidationDiffFilePath);
                        onOutputLine?.Invoke("⚠️ Diff summary file not produced — skipping review");
                        skipReview = true;
                    }
                    else
                    {
                        var diffContent = await File.ReadAllTextAsync(diffFilePath, ct);
                        if (diffContent.Trim().Length < AdversarialReviewHelper.MinimumContentThreshold)
                        {
                            Logger.Warning("Diff summary file too short ({Length} chars), skipping review",
                                diffContent.Trim().Length);
                            onOutputLine?.Invoke($"⚠️ Diff summary too short ({diffContent.Trim().Length} chars) — skipping review");
                            skipReview = true;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    diffActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                    diffActivity?.AddException(ex);
                    Logger.Warning(ex, "Diff summary step failed: {Message}, skipping review entirely", ex.Message);
                    onOutputLine?.Invoke($"⚠️ Diff summary failed: {ex.Message} — skipping review");
                    skipReview = true;
                }
            }

            // 4b. Review step (only if diff summary succeeded)
            if (!skipReview)
            {
                reviewResult = await RunWithTracingAsync("BrainConsolidation.AdversarialReview", job.JobId, async _ =>
                {
                    return await AdversarialReviewHelper.ExecuteReviewAsync(
                        agentProvider,
                        workspacePath,
                        ConsolidationPromptBuilder.BuildBrainConsolidationReviewPrompt(),
                        ConsolidationPromptBuilder.BuildBrainConsolidationRefinementPrompt(),
                        AgentWorkspacePaths.BrainConsolidationReviewFilePath,
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
            var reviewTokenUsage = reviewResult?.ReviewTokenUsage;
            var refinementTokenUsage = reviewResult?.RefinementTokenUsage;

            // 6. Commit all changes (AFTER review/refinement completes)
            Logger.Information("Committing brain consolidation changes for run {RunId}", job.JobId);
            using (var commitActivity = PipelineTelemetry.ActivitySource.StartActivity("BrainConsolidation.Commit"))
            {
                commitActivity?.SetTag("pipeline.run_id", job.JobId);
                try
                {
                    await brainProvider.CommitAllAsync(workspacePath, $"Brain consolidation run {job.JobId}", ct);
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

            // 7. Push to base branch
            Logger.Information("Pushing brain consolidation changes for run {RunId}", job.JobId);
            using (var pushActivity = PipelineTelemetry.ActivitySource.StartActivity("BrainConsolidation.Push"))
            {
                pushActivity?.SetTag("pipeline.run_id", job.JobId);
                try
                {
                    await brainProvider.PushBranchAsync(workspacePath, brainProvider.BaseBranch, ct);
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

            // 8. Parse metrics from agent output
            var responseText = string.Join("\n", agentResult.OutputLines);
            var (filesModified, entriesMerged, contradictionsResolved, entriesPruned) = ParseMetrics(responseText);

            // 9. Format summary and return
            var summary = ConsolidationPromptBuilder.FormatBrainConsolidationSummary(
                filesModified, entriesMerged, contradictionsResolved, entriesPruned);

            Logger.Information("{ExecutorName} run {RunId} completed: {Summary}", ExecutorName, job.JobId, summary);

            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = true,
                Summary = summary,
                DiffSummaryTokenUsage = diffSummaryTokenUsage,
                ReviewTokenUsage = reviewTokenUsage,
                RefinementTokenUsage = refinementTokenUsage
            };
        }, ct);
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
        var match = Regex.Match(text, pattern);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : 0;
    }
}
