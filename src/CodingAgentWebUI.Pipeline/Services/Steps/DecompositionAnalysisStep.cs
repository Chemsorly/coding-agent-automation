using System.Text;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Prompts;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

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
        try
        {
            await WriteEpicContextAsync(context, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning(ex, "Failed to write epic context for run {RunId}, continuing without it", run.RunId);
            context.Callbacks.EmitOutputLine("⚠️ Failed to write epic context — continuing without it");
        }

        // 3. Build analysis prompt
        var maxSubIssues = config.MaxDecompositionSubIssues;
        var analysisPrompt = DecompositionPromptBuilder.BuildAnalysisPrompt(maxSubIssues, context.ProjectContext);

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

        run.AccumulateTokenUsage(agentResult!);

        if (!agentResult!.Success)
        {
            logger.Warning("Agent exited with code {ExitCode} for decomposition analysis run {RunId}",
                agentResult.ExitCode, run.RunId);
            context.Callbacks.EmitOutputLine($"❌ Agent exited with code {agentResult.ExitCode}");
            await context.FailRunAsync($"Agent exited with non-zero exit code: {agentResult.ExitCode}", ct);
            return StepResult.Stop;
        }

        // 6. Validate plan file exists and ≥20 chars
        var planFilePath = Path.Combine(run.WorkspacePath!, AgentWorkspacePaths.DecompositionPlanFilePath);

        if (!File.Exists(planFilePath))
        {
            logger.Warning("Decomposition plan file not found at {Path} for run {RunId}",
                planFilePath, run.RunId);
            context.Callbacks.EmitOutputLine("❌ Agent did not produce a decomposition plan file");
            await context.FailRunAsync("Agent did not produce a decomposition plan file at " +
                                AgentWorkspacePaths.DecompositionPlanFilePath, ct);
            return StepResult.Stop;
        }

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
            DecompositionPromptBuilder.BuildReviewPrompt(context.ProjectContext),
            DecompositionPromptBuilder.BuildRefinementPrompt(),
            AgentWorkspacePaths.DecompositionReviewFilePath,
            new AdversarialReviewConfig
            {
                Enabled = true,
                AgentTimeout = config.DecompositionTimeout
            },
            line => context.Callbacks.EmitOutputLine(line),
            logger,
            ct);

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
