using System.Diagnostics;
using System.Text;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services.Prompts;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services.Steps;

/// <summary>
/// Phase 2 decomposition step: writes epic context (including the approved plan comment),
/// queries existing agent-generated sub-issues for deduplication, executes the agent to
/// produce sub-issue JSON files at <c>.agent/sub-issues/</c>, and validates that the
/// plan comment exists in the epic's comment thread.
/// </summary>
public sealed class DecompositionStep : IPipelineStep
{
    public string StepName => "Decomposition";

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
        var existingTitles = await QueryExistingSubIssueTitlesAsync(context.IssueOps, context.ProjectContext, logger, run.RunId, ct);
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
        var prompt = DecompositionPromptBuilder.BuildDecompositionPrompt(config.MaxDecompositionSubIssues, config.MaxDecompositionSubIssueFiles, context.ProjectContext);

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
                    Timeout = config.AgentTimeout,
                    UseResume = true
                },
                run, config, "Decomposition agent", context.Callbacks.NotifyChange, logger, ct,
                line => context.Callbacks.EmitOutputLine(line),
                stallMetrics: AgentPhaseExecutor.BuildStallMetricsWithServerSideReporting(context.ReportPipelineRunEvent),
                phase: "decomposition");
        }
        catch (OperationCanceledException) when (context.Cts?.IsCancellationRequested == true)
        {
            throw; // Propagate orchestrator-level cancellation
        }
        catch (Exception ex)
        {
            Activity.Current?.RecordError(ex, ct);
            var reason = $"Decomposition agent execution failed: {ex.Message}";
            logger.Warning(ex, "Pipeline {RunId} decomposition agent failed", run.RunId);
            context.Callbacks.EmitOutputLine($"❌ {reason}");
            await context.FailRunAsync(reason, ct);
            return StepResult.Stop;
        }

        run.AccumulateTokenUsage(agentResult, phase: "decomposition");

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
    /// For repo epics (<paramref name="projectContext"/> is null), reads only from the run's own tracker.
    /// For project epics, also reads from each enabled template tracker so that a rerun does not
    /// recreate sub-issues that were already created in a previous partial run.
    /// Returns a deduplicated list of titles (case-sensitive).
    /// </summary>
    private static async Task<IReadOnlyList<string>> QueryExistingSubIssueTitlesAsync(
        IAgentIssueOperations issueOps,
        DecompositionProjectContext? projectContext,
        Serilog.ILogger logger,
        string runId,
        CancellationToken ct)
    {
        var titles = new HashSet<string>(StringComparer.Ordinal);
        var generatedLabels = new List<string> { AgentLabels.Generated };

        // Always read from the run's own (epic) tracker via the unscoped path.
        await CollectTitlesFromTrackerAsync(
            (page, pageSize, labels, token) => issueOps.ListOpenIssuesAsync(page, pageSize, labels, token),
            generatedLabels, titles, logger, runId, ct);

        // For project epics, also read from each enabled template tracker.
        if (projectContext is not null)
        {
            foreach (var repo in projectContext.Repositories)
            {
                if (!repo.DecompositionEnabled || string.IsNullOrEmpty(repo.IssueProviderId))
                    continue;

                // TODO: Skip repo.IssueProviderId when it equals the run's own IssueProviderConfigId to avoid
                // a redundant network round-trip. The HashSet deduplicates titles so correctness is unaffected,
                // but the extra call is unnecessary when the epic tracker and a template tracker are the same.
                // See review finding: DecompositionStep — duplicate call for same-tracker template.
                var providerId = repo.IssueProviderId;
                await CollectTitlesFromTrackerAsync(
                    (page, pageSize, labels, token) => issueOps.ListOpenIssuesForProviderAsync(providerId, page, pageSize, labels, token),
                    generatedLabels, titles, logger, runId, ct);
            }
        }

        return titles.ToList();
    }

    /// <summary>
    /// Paginates through issues returned by <paramref name="listAsync"/> and adds their titles
    /// to <paramref name="titles"/>. Swallows non-cancellation exceptions (matching the existing
    /// fault-tolerance behaviour) so that a single failing tracker does not abort the whole query.
    /// </summary>
    // TODO: The fault-tolerance policy here also swallows failures on the own tracker (unscoped
    // ListOpenIssuesAsync call). A transient own-tracker failure silently returns an empty set,
    // indistinguishable from "no existing sub-issues", which could lead to duplicate creation on
    // a rerun. Consider distinguishing own-tracker failures (re-throw or surface as pipeline error)
    // from template-tracker failures (swallow). See review finding: CollectTitlesFromTrackerAsync — own-tracker failure masking.
    // TODO: WriteOpenIssueContextStep is not updated to read from template trackers for project epics.
    // The issue description (Suggested Fix §2) lists both OpenIssueContextWriter and DecompositionStep
    // as consumers needing cross-repo merging; only DecompositionStep (agent:generated deduplication)
    // is fixed here. The open/closed issue context written for the agent still excludes template tracker
    // issues on reruns. See review finding: WriteOpenIssueContextStep — incomplete open/closed context for project epics.
    private static async Task CollectTitlesFromTrackerAsync(
        Func<int, int, IReadOnlyList<string>?, CancellationToken, Task<PagedResult<IssueSummary>>> listAsync,
        IReadOnlyList<string> labels,
        HashSet<string> titles,
        Serilog.ILogger logger,
        string runId,
        CancellationToken ct)
    {
        try
        {
            var page = 1;
            const int pageSize = 50;
            bool hasMore;

            do
            {
                var result = await listAsync(page, pageSize, labels, ct);
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
