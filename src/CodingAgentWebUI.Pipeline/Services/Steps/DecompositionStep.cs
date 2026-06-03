using System.Text;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Prompts;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Phase 2 decomposition step: writes epic context (including the approved plan comment),
/// queries existing agent-generated sub-issues for deduplication, executes the agent to
/// produce sub-issue JSON files at <c>.agent/sub-issues/</c>, and validates that the
/// plan comment exists in the epic's comment thread.
/// </summary>
internal sealed class DecompositionStep : IPipelineStep
{
    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Decomposition");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);
        activity?.SetTag("pipeline.run_type", context.Run.RunType.ToString());

        ArgumentNullException.ThrowIfNull(context);

        var run = context.Run;
        var config = context.Config;
        var logger = context.Logger;

        // 1. Transition to GeneratingSubIssues
        context.Callbacks.TransitionTo(PipelineStep.GeneratingSubIssues);
        context.Callbacks.EmitOutputLine("🧩 Starting sub-issue generation...");

        // 6. Validate plan comment exists (marker detection) — do this early to fail fast
        var comments = await context.IssueOps.ListCommentsAsync(run.IssueIdentifier, ct);
        var planComment = FindMostRecentPlanComment(comments);

        if (planComment is null)
        {
            var reason = "No approved decomposition plan found — the epic's comment thread does not contain " +
                         $"a comment with the '{CommentMarkers.DecompositionPlan}' marker.";
            logger.Warning("Pipeline {RunId} decomposition failed: {Reason}", run.RunId, reason);
            context.Callbacks.EmitOutputLine($"❌ {reason}");
            await context.FailRunAsync(reason, ct);
            return StepResult.Stop;
        }

        logger.Information("Pipeline {RunId} found plan comment (ID: {CommentId})", run.RunId, planComment.Id);

        // 2. Write epic body + all comments (including plan comment) to .agent/issue-context.md
        var issueContextPath = Path.Combine(run.WorkspacePath!, AgentWorkspacePaths.IssueContextFilePath);
        var issueContextDir = Path.GetDirectoryName(issueContextPath)!;
        Directory.CreateDirectory(issueContextDir);

        if (context.Issue is null)
        {
            var reason = "Issue detail not available on pipeline context";
            logger.Warning("Pipeline {RunId} decomposition failed: {Reason}", run.RunId, reason);
            context.Callbacks.EmitOutputLine($"❌ {reason}");
            await context.FailRunAsync(reason, ct);
            return StepResult.Stop;
        }

        var issueContextContent = BuildIssueContextContent(context.Issue, comments);
        await File.WriteAllTextAsync(issueContextPath, issueContextContent, ct);
        logger.Information("Pipeline {RunId} wrote issue context with {CommentCount} comments",
            run.RunId, comments.Count);

        // 3. Query existing agent-generated sub-issues for deduplication context
        var existingTitles = await QueryExistingSubIssueTitlesAsync(context.IssueOps, logger, run.RunId, ct);
        if (existingTitles.Count > 0)
        {
            // Append existing sub-issue titles to the issue context file for deduplication
            var deduplicationSection = BuildDeduplicationSection(existingTitles);
            await File.AppendAllTextAsync(issueContextPath, deduplicationSection, ct);
            logger.Information("Pipeline {RunId} appended {Count} existing sub-issue titles for deduplication",
                run.RunId, existingTitles.Count);
            context.Callbacks.EmitOutputLine($"📋 Found {existingTitles.Count} existing agent-generated sub-issues for deduplication");
        }

        // 4. Build prompt via DecompositionPromptBuilder.BuildDecompositionPrompt(maxSubIssues)
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(config.MaxDecompositionSubIssues, context.ProjectContext);

        // 5. Execute agent expecting .agent/sub-issues/*.json output
        context.Callbacks.EmitOutputLine("🤖 Executing decomposition agent...");

        AgentResult agentResult;
        try
        {
            agentResult = await AgentStallMonitor.ExecuteWithMonitoringAsync(
                context.AgentProvider,
                new AgentRequest
                {
                    Prompt = prompt,
                    WorkspacePath = run.WorkspacePath!,
                    Timeout = config.DecompositionTimeout,
                    UseResume = true
                },
                run, config, "Decomposition agent", context.Callbacks.NotifyChange, logger, ct,
                line =>
                {
                    run.OutputLines.Enqueue(line);
                    context.Callbacks.EmitOutputLine(line);
                });
        }
        catch (OperationCanceledException) when (context.Cts?.IsCancellationRequested == true)
        {
            throw; // Propagate orchestrator-level cancellation
        }
        catch (Exception ex)
        {
            var reason = $"Decomposition agent execution failed: {ex.Message}";
            logger.Warning(ex, "Pipeline {RunId} decomposition agent failed", run.RunId);
            context.Callbacks.EmitOutputLine($"❌ {reason}");
            await context.FailRunAsync(reason, ct);
            return StepResult.Stop;
        }

        run.AccumulateTokenUsage(agentResult);

        if (!agentResult.Success)
        {
            var reason = $"Decomposition agent exited with non-zero code {agentResult.ExitCode}";
            logger.Warning("Pipeline {RunId} {Reason}", run.RunId, reason);
            context.Callbacks.EmitOutputLine($"❌ {reason}");
            await context.FailRunAsync(reason, ct);
            return StepResult.Stop;
        }

        // Verify sub-issues directory has files (informational — CreateSubIssuesStep handles the actual parsing)
        var subIssuesDir = Path.Combine(run.WorkspacePath!, AgentWorkspacePaths.SubIssuesDirectory);
        if (Directory.Exists(subIssuesDir))
        {
            var fileCount = Directory.GetFiles(subIssuesDir, "*.json").Length;
            context.Callbacks.EmitOutputLine($"✅ Agent produced {fileCount} sub-issue file(s)");
            logger.Information("Pipeline {RunId} agent produced {FileCount} sub-issue files", run.RunId, fileCount);
        }
        else
        {
            context.Callbacks.EmitOutputLine("⚠️ Agent did not create sub-issues directory — CreateSubIssuesStep will handle this");
            logger.Warning("Pipeline {RunId} sub-issues directory not found after agent execution", run.RunId);
        }

        return StepResult.Continue;
    }

    /// <summary>
    /// Finds the most recent comment containing the decomposition plan marker.
    /// Returns null if no plan comment exists.
    /// </summary>
    internal static IssueComment? FindMostRecentPlanComment(IReadOnlyList<IssueComment> comments)
    {
        // Iterate in reverse to find the most recent comment with the marker
        for (var i = comments.Count - 1; i >= 0; i--)
        {
            if (comments[i].Body.Contains(CommentMarkers.DecompositionPlan, StringComparison.Ordinal))
                return comments[i];
        }

        return null;
    }

    /// <summary>
    /// Builds the issue context file content with the epic body and all comments.
    /// </summary>
    private static string BuildIssueContextContent(IssueDetail issue, IReadOnlyList<IssueComment> comments)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# Epic Issue Context");
        sb.AppendLine();
        sb.AppendLine($"## {issue.Title}");
        sb.AppendLine();
        sb.AppendLine(issue.Description);
        sb.AppendLine();

        if (comments.Count > 0)
        {
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("## Comments");
            sb.AppendLine();

            foreach (var comment in comments)
            {
                sb.AppendLine($"### Comment by {comment.Author} ({comment.CreatedAt:yyyy-MM-dd HH:mm:ss UTC})");
                sb.AppendLine();
                sb.AppendLine(comment.Body);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Queries existing agent-generated sub-issues for deduplication context.
    /// Returns a list of titles of existing sub-issues.
    /// </summary>
    private static async Task<IReadOnlyList<string>> QueryExistingSubIssueTitlesAsync(
        IAgentIssueOperations issueOps, Serilog.ILogger logger, string runId, CancellationToken ct)
    {
        var titles = new List<string>();

        try
        {
            var labels = new List<string> { AgentLabels.Generated };
            var page = 1;
            const int pageSize = 50;
            bool hasMore;

            do
            {
                var result = await issueOps.ListOpenIssuesAsync(page, pageSize, labels, ct);
                foreach (var issue in result.Items)
                {
                    titles.Add(issue.Title);
                }

                hasMore = result.HasMore;
                page++;
            } while (hasMore);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning(ex, "Pipeline {RunId} failed to query existing sub-issues for deduplication", runId);
        }

        return titles;
    }

    /// <summary>
    /// Builds a deduplication section listing existing sub-issue titles.
    /// </summary>
    private static string BuildDeduplicationSection(IReadOnlyList<string> existingTitles)
    {
        var sb = new StringBuilder();

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Existing Agent-Generated Sub-Issues (Do NOT Duplicate)");
        sb.AppendLine();
        sb.AppendLine("The following sub-issues already exist. Do NOT create duplicates:");
        sb.AppendLine();

        foreach (var title in existingTitles)
        {
            sb.AppendLine($"- {title}");
        }

        sb.AppendLine();

        return sb.ToString();
    }
}
