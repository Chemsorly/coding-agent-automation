using System.Text;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services.Prompts;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services.Steps;

/// <summary>
/// Phase 1 step that explores the codebase and produces a validated decomposition plan.
/// Responsibilities:
/// 1. Transition to ExploringCodebase
/// 2. Write epic issue body + comments to .agent/issue-context.md
/// 3. Build analysis prompt via DecompositionPromptBuilder.BuildAnalysisPrompt
/// 4. Transition to GeneratingPlan
/// 5. Execute agent expecting .agent/decomposition-plan.md output
/// 6. Validate plan file exists and ≥20 chars
/// 7. Transition to ReviewingPlan
/// 8. Execute adversarial review via AdversarialReviewHelper.ExecuteReviewAsync
/// 9. Return StepResult.Continue on success or StepResult.Stop on failure
/// </summary>
public sealed class DecompositionAnalysisStep : IPipelineStep
{
    public string StepName => "DecompositionAnalysis";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("DecompositionAnalysis");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);
        activity?.SetTag("pipeline.run_type", context.Run.RunType.ToString());

        var run = context.Run;
        var config = context.Config;
        var logger = context.Logger;

        // 1. Transition to ExploringCodebase
        context.Callbacks.TransitionTo(PipelineStep.ExploringCodebase);
        context.Callbacks.EmitOutputLine("🔍 Starting decomposition analysis...");

        // 2. Write epic issue body + comments to .agent/issue-context.md
        var epicContextFailed = false;
        try
        {
            await WriteEpicContextAsync(context, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            epicContextFailed = true;
            logger.Warning(ex, "Failed to write epic context for run {RunId}, continuing without it", run.RunId);
            context.Callbacks.EmitOutputLine("⚠️ Failed to write epic context — continuing without it");
        }

        // Capture WriteOpenIssueContext degradation: if the prior step tried to fetch issues
        // but wrote 0 files (hub errors silently swallowed per-identifier), treat it the same
        // as a WriteEpicContextAsync failure so the run uses FailureReason.InfrastructureFailure
        // rather than the generic "Agent did not produce" message. Only applies to epic-scoped
        // runs (DecompositionAnalysis / Decomposition) — other run types don't download context.
        if (!epicContextFailed && IsOpenIssueContextDegraded(run))
        {
            epicContextFailed = true;
            logger.Warning(
                "WriteOpenIssueContext wrote 0 files for run {RunId} (RunType={RunType}) — " +
                "treating as context unavailable; all RequestGetIssue calls likely failed",
                run.RunId, run.RunType);
            context.Callbacks.EmitOutputLine("⚠️ Issue context unavailable — open-issue context could not be downloaded");
        }

        // 3. Build analysis prompt
        var maxSubIssues = config.MaxDecompositionSubIssues;
        var maxFiles = config.MaxDecompositionSubIssueFiles;
        var analysisPrompt = DecompositionPromptBuilder.BuildAnalysisPrompt(maxSubIssues, maxFiles, context.ProjectContext);

        // 4. Transition to GeneratingPlan
        context.Callbacks.TransitionTo(PipelineStep.GeneratingPlan);
        context.Callbacks.EmitOutputLine("📝 Executing agent to generate decomposition plan...");

        // 5. Execute agent expecting .agent/decomposition-plan.md output
        AgentResult? agentResult = null;
        var execResult = await context.TryCriticalAsync(async () =>
        {
            agentResult = await AgentStallMonitor.ExecuteWithMonitoringAsync(
                context.AgentProvider,
                new AgentRequest
                {
                    Prompt = analysisPrompt,
                    WorkspacePath = run.WorkspacePath!,
                    Timeout = config.DecompositionTimeout,
                    UseResume = false
                },
                run, config, "Decomposition analysis agent", context.Callbacks.NotifyChange, logger, ct,
                line => context.Callbacks.EmitOutputLine(line));
        }, "Agent execution", ct);

        if (execResult == StepResult.Stop)
            return StepResult.Stop;

        run.AccumulateTokenUsage(agentResult!, phase: "decomposition_analysis");

        if (!agentResult!.Success)
        {
            logger.Warning("Agent exited with code {ExitCode} for decomposition analysis run {RunId}",
                agentResult.ExitCode, run.RunId);
            context.Callbacks.EmitOutputLine($"❌ Agent exited with code {agentResult.ExitCode}");
            await context.FailRunAsync($"Agent exited with non-zero exit code: {agentResult.ExitCode}", FailureReason.ExitCodeFailure, ct);
            return StepResult.Stop;
        }

        // 6. Validate plan file exists and ≥20 chars
        var planFilePath = Path.Combine(run.WorkspacePath!, AgentWorkspacePaths.DecompositionPlanFilePath);

        var planValidationResult = await ValidatePlanFileAsync(context, planFilePath, epicContextFailed, ct);
        if (planValidationResult == StepResult.Stop)
            return StepResult.Stop;

        var planContent = await File.ReadAllTextAsync(planFilePath, ct);
        if (planContent.Trim().Length < AdversarialReviewHelper.MinimumContentThreshold)
        {
            logger.Warning("Decomposition plan file too short ({Length} chars) for run {RunId}",
                planContent.Trim().Length, run.RunId);
            context.Callbacks.EmitOutputLine($"❌ Decomposition plan too short ({planContent.Trim().Length} chars, minimum {AdversarialReviewHelper.MinimumContentThreshold})");
            await context.FailRunAsync($"Decomposition plan file is too short ({planContent.Trim().Length} characters, minimum {AdversarialReviewHelper.MinimumContentThreshold})", ct);
            return StepResult.Stop;
        }

        context.Callbacks.EmitOutputLine($"✅ Decomposition plan generated ({planContent.Trim().Length} chars)");

        // 7. Transition to ReviewingPlan
        context.Callbacks.TransitionTo(PipelineStep.ReviewingPlan);

        // 8. Execute adversarial review via AdversarialReviewHelper.ExecuteReviewAsync
        var reviewResult = await AdversarialReviewHelper.ExecuteReviewAsync(
            context.AgentProvider,
            run.WorkspacePath!,
            DecompositionPromptBuilder.BuildReviewPrompt(maxFiles, maxSubIssues, context.ProjectContext),
            DecompositionPromptBuilder.BuildRefinementPrompt(maxFiles, maxSubIssues),
            AgentWorkspacePaths.DecompositionReviewFilePath,
            new AdversarialReviewConfig
            {
                Enabled = true,
                AgentTimeout = config.DecompositionTimeout
            },
            line => context.Callbacks.EmitOutputLine(line),
            logger,
            ct);

        // Accumulate token usage from adversarial review and refinement before checking success.
        // Mirrors the pattern in AgentPhaseExecutor.Analysis.cs — tokens are always recorded
        // regardless of whether the review found issues or triggered refinement.
        if (reviewResult.ReviewTokenUsage is not null)
            run.AccumulateTokenUsage(reviewResult.ReviewTokenUsage, phase: "decomposition_review");
        if (reviewResult.RefinementTokenUsage is not null)
            run.AccumulateTokenUsage(reviewResult.RefinementTokenUsage, phase: "decomposition_refinement");

        // 9. Return StepResult.Continue on success or StepResult.Stop on failure
        if (!reviewResult.ReviewExecuted)
        {
            // Review failed to execute (agent crash, etc.) — treat as failure
            logger.Warning("Adversarial review did not execute for decomposition run {RunId}", run.RunId);
            context.Callbacks.EmitOutputLine("❌ Adversarial review failed to execute");
            await context.FailRunAsync("Adversarial review failed to execute", ct);
            return StepResult.Stop;
        }

        context.Callbacks.EmitOutputLine("✅ Decomposition analysis complete");
        return StepResult.Continue;
    }

    /// <summary>
    /// Checks whether the decomposition plan file exists and contains enough content.
    /// Returns <see cref="StepResult.Stop"/> and calls FailRunAsync if the file is missing or too short;
    /// returns <see cref="StepResult.Continue"/> otherwise.
    /// </summary>
    private static async Task<StepResult> ValidatePlanFileAsync(
        PipelineStepContext context, string planFilePath, bool epicContextFailed, CancellationToken ct)
    {
        var run = context.Run;
        var logger = context.Logger;

        if (!File.Exists(planFilePath))
        {
            var reason = epicContextFailed
                ? "Agent could not produce a decomposition plan because epic context was unavailable " +
                  "(RequestGetIssue failed — possible cross-replica state miss). " +
                  $"Expected file: {AgentWorkspacePaths.DecompositionPlanFilePath}"
                : "Agent did not produce a decomposition plan file at " +
                  AgentWorkspacePaths.DecompositionPlanFilePath;

            logger.Warning("Decomposition plan file not found at {Path} for run {RunId} (epicContextFailed={EpicContextFailed})",
                planFilePath, run.RunId, epicContextFailed);
            context.Callbacks.EmitOutputLine("❌ " + (epicContextFailed
                ? "Agent could not produce plan — epic context was unavailable (check API logs for RequestGetIssue errors)"
                : "Agent did not produce a decomposition plan file"));

            if (epicContextFailed)
                await context.FailRunAsync(reason, FailureReason.InfrastructureFailure, ct);
            else
                await context.FailRunAsync(reason, ct);
            return StepResult.Stop;
        }

        return StepResult.Continue;
    }

    /// <summary>
    /// Returns true when the WriteOpenIssueContext step wrote 0 files for an epic-scoped run,
    /// indicating that all RequestGetIssue calls silently failed — treat as context unavailable.
    /// </summary>
    private static bool IsOpenIssueContextDegraded(PipelineRun run) =>
        run.OpenIssuesDownloaded == 0
        && run.RunType is PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition;

    /// <summary>
    /// Writes the epic issue body and all comments to .agent/issue-context.md.
    /// Uses IAgentIssueOperations to fetch the issue detail and comments.
    /// </summary>
    private static async Task WriteEpicContextAsync(PipelineStepContext context, CancellationToken ct)
    {
        var run = context.Run;
        var workspacePath = run.WorkspacePath!;

        // Ensure .agent directory exists
        var agentDir = Path.Combine(workspacePath, AgentWorkspacePaths.MetadataDirectory);
        Directory.CreateDirectory(agentDir);

        // Fetch issue detail
        var issue = await context.IssueOps.GetIssueAsync(run.IssueIdentifier, ct);

        // Fetch comments
        var comments = await context.IssueOps.ListCommentsAsync(run.IssueIdentifier, ct);

        // Build issue context content
        var sb = new StringBuilder();
        sb.AppendLine($"# Epic: {issue.Title}");
        sb.AppendLine();
        sb.AppendLine("## Description");
        sb.AppendLine();
        sb.AppendLine(issue.Description);
        sb.AppendLine();

        if (issue.Labels.Count > 0)
        {
            sb.AppendLine("## Labels");
            sb.AppendLine();
            foreach (var label in issue.Labels)
            {
                sb.AppendLine($"- {label}");
            }
            sb.AppendLine();
        }

        if (comments.Count > 0)
        {
            sb.AppendLine("## Comments");
            sb.AppendLine();
            foreach (var comment in comments)
            {
                sb.AppendLine($"### Comment by @{comment.Author} ({comment.CreatedAt:yyyy-MM-dd HH:mm} UTC)");
                sb.AppendLine();
                sb.AppendLine(comment.Body);
                sb.AppendLine();
            }
        }

        // Write to workspace
        var contextFilePath = Path.Combine(workspacePath, AgentWorkspacePaths.IssueContextFilePath);
        await File.WriteAllTextAsync(contextFilePath, sb.ToString(), ct);

        context.Logger.Information("Wrote epic context ({IssueId}, {CommentCount} comments) to {Path}",
            run.IssueIdentifier, comments.Count, AgentWorkspacePaths.IssueContextFilePath);
    }
}
