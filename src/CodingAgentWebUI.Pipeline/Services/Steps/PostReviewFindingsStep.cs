using System.Diagnostics;
using CodingAgentWebUI.Pipeline.CodeReview;
using CodingAgentWebUI.Pipeline.CodeReview.Models;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Formats code review findings and posts them as a PR review.
/// Enhanced with inline comment orchestration: parses structured findings,
/// retries agents that don't produce file:line references, selects/caps/consolidates
/// findings, and submits via the Reviews API with inline comments.
/// 
/// Stale handling: dismisses previous reviews (when supported) or collapses them
/// into a details block (fallback for non-inline providers).
/// 
/// Non-fatal on all failures — the step always returns <see cref="StepResult.Continue"/>.
/// </summary>
public sealed class PostReviewFindingsStep : IPipelineStep
{
    public string StepName => "PostReviewFindings";

    private const string NoReviewerMessage =
        "No applicable reviewers found for this repository's labels. Review skipped.";

    private const string SupersededPrefix =
        "<details>\n<summary>⏳ Superseded by newer review (click to expand)</summary>\n\n";

    private const string SupersededSuffix = "\n</details>";

    private const string DismissReason = "Superseded by a newer automated review.";

    private const string FollowUpPromptTemplate =
        """
        Your previous review output did not include file:line references in the expected structured format.
        Please reformat your findings using this structure (one finding per line):

        [SEVERITY] path/to/file.ext:LINE — description of the issue

        Where:
        - SEVERITY is one of: CRITICAL, WARNING, SUGGESTION
        - path is relative to the repository root using forward slashes
        - LINE is the 1-based line number in the file

        Here is your original output to reformat:

        {0}
        """;

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("PostReviewFindings");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);
        activity?.SetTag("pipeline.run_type", context.Run.RunType.ToString());

        context.Callbacks.TransitionTo(PipelineStep.PostingFindings);

        if (!int.TryParse(context.Run.IssueIdentifier, out var prNumber))
        {
            context.Logger.Warning("PR identifier '{Identifier}' is not a valid integer, skipping review posting",
                context.Run.IssueIdentifier);
            return StepResult.Continue;
        }

        try
        {
            await ExecuteInternalAsync(context, prNumber, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Activity.Current?.RecordError(ex);
            // Outer non-fatal handler: catches double-failures and any unexpected exceptions.
            // The step NEVER throws — all failures are non-fatal.
            context.Logger.Warning(ex, "Failed to post review findings to PR #{PrNumber}", prNumber);
        }

        return StepResult.Continue;
    }

    private static async Task ExecuteInternalAsync(PipelineStepContext context, int prNumber, CancellationToken ct)
    {
        var supportsInline = context.RepoProvider.SupportsInlineReviewComments;
        var inlineSettings = context.Config.CodeReview.InlineComments;

        // Step 1: Stale review handling
        if (supportsInline)
        {
            // Dismiss previous reviews via platform-native API
            await DismissPreviousReviewSafeAsync(context, prNumber, ct);
        }
        else
        {
            // Collapse existing reviews (existing behavior for non-inline providers)
            await CollapseExistingReviewsAsync(context, prNumber, ct);
        }

        // Step 2: Determine the body and review type
        var body = context.Run.CodeReviewAgentsRun.Count == 0
            ? $"{CommentMarkers.PrReview}\n{NoReviewerMessage}"
            : ReviewFindingsFormatter.Format(context.Run);

        var reviewType = DetermineReviewType(context.Run);

        // Step 3: If inline comments are disabled, submit body-only and return
        if (!inlineSettings.Enabled)
        {
            await SubmitWithOwnPrFallbackAsync(context, prNumber, body, reviewType, ct);
            return;
        }

        // Step 4: If provider doesn't support inline comments but inline is enabled,
        // parse findings and append "Findings by Location" section to body
        if (!supportsInline)
        {
            if (inlineSettings.Enabled)
            {
                // Parse findings to get location metadata for the body section
                var findings = new List<StructuredFinding>();
                foreach (var kvp in context.Run.CodeReviewAgentFindings)
                {
                    if (!string.IsNullOrEmpty(kvp.Value))
                        findings.AddRange(FindingsParser.Parse(kvp.Value, kvp.Key));
                }

                var locationSection = ReviewFindingsFormatter.FormatFindingsByLocation(findings);
                if (!string.IsNullOrEmpty(locationSection))
                {
                    body += "\n" + locationSection;
                }
            }

            await SubmitWithOwnPrFallbackAsync(context, prNumber, body, reviewType, ct);
            return;
        }

        // Step 5: Parse structured findings per agent + retry loop
        var allFindings = await ParseFindingsWithRetryAsync(context, inlineSettings, ct);

        // Step 6: Select/filter/cap/consolidate via FindingsSelector
        var findingsWithLocation = allFindings
            .Where(f => f.FilePath is not null && f.LineNumber > 0)
            .ToList();

        var (comments, excludedCount) = FindingsSelector.Select(findingsWithLocation, inlineSettings);

        // Step 6.5: Filter comments to only those targeting lines within diff hunks.
        // GitHub's API returns 422 if a comment targets a line outside the diff.
        var diffPath = Path.Combine(context.Run.WorkspacePath!, AgentWorkspacePaths.FullDiffFilePath);
        IReadOnlyList<ReviewComment> validComments = comments;
        if (File.Exists(diffPath))
        {
            try
            {
                var diffText = await File.ReadAllTextAsync(diffPath, ct);
                var validLines = DiffHunkParser.ParseValidLines(diffText);
                validComments = comments
                    .Where(c => validLines.TryGetValue(c.Path, out var lines) && lines.Contains(c.Line))
                    .ToList();

                var filteredCount = comments.Count - validComments.Count;
                if (filteredCount > 0)
                {
                    context.Logger.Information(
                        "Filtered {FilteredCount}/{TotalCount} inline comments targeting lines outside diff hunks",
                        filteredCount, comments.Count);

                    // Log per-comment diagnostics at Debug level for troubleshooting
                    foreach (var c in comments)
                    {
                        if (!validLines.ContainsKey(c.Path))
                        {
                            context.Logger.Debug("Comment on {Path}:{Line} filtered — file not in diff", c.Path, c.Line);
                        }
                        else if (!validLines[c.Path].Contains(c.Line))
                        {
                            context.Logger.Debug("Comment on {Path}:{Line} filtered — line not in any diff hunk", c.Path, c.Line);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                context.Logger.Warning(ex, "Failed to parse diff for hunk validation, submitting all comments (may 422)");
                // Fall through with unfiltered comments — the 422 fallback will handle it
            }
        }

        // Step 7: Build ReviewSubmission with CommitId
        string? commitId = null;
        try
        {
            commitId = await context.RepoProvider.GetHeadCommitShaAsync(context.Run.WorkspacePath!, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Warning(ex, "Failed to get HEAD commit SHA, inline comments will not be anchored to a specific commit");
        }

        var submission = new ReviewSubmission
        {
            Body = body,
            Type = reviewType,
            Comments = validComments,
            CommitId = commitId
        };

        // Step 8: Submit via new overload
        try
        {
            await context.RepoProvider.SubmitPullRequestReviewAsync(prNumber, submission, ct);
            context.Run.InlineCommentsPosted = validComments.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Step 9: On exception (including 422), retry body-only
            context.Logger.Warning(ex, "Failed to submit review with inline comments on PR #{PrNumber}, retrying body-only", prNumber);

            context.Run.InlineCommentsDegraded = true;
            context.Run.InlineCommentsDegradedReason = $"Inline submission failed: {ex.Message}";

            // Retry body-only — if this also fails due to "own pull request" restriction,
            // downgrade to Comment type (GitHub disallows REQUEST_CHANGES on own PRs).
            await SubmitWithOwnPrFallbackAsync(context, prNumber, body, reviewType, ct);
        }
    }

    /// <summary>
    /// Determines the review type based on the severity of findings in the run.
    /// Critical/Warning → RequestChanges (blocks merge, dismissible).
    /// Suggestion-only or no findings → Comment (doesn't block, doesn't auto-approve).
    /// Using Approve could inadvertently satisfy branch protection rules,
    /// allowing PRs to merge without human review.
    /// </summary>
    private static PullRequestReviewType DetermineReviewType(PipelineRun run)
    {
        if (run.CodeReviewCriticalCount > 0 || run.CodeReviewWarningCount > 0)
            return PullRequestReviewType.RequestChanges;

        // Suggestion-only and no-findings both use Comment (not Approve).
        // Using Approve could inadvertently satisfy branch protection rules,
        // allowing PRs to merge without human review.
        return PullRequestReviewType.Comment;
    }

    /// <summary>
    /// Submits a body-only review, falling back from RequestChanges to Comment
    /// when GitHub returns 422 "Can not request changes on your own pull request".
    /// This occurs when the bot that created the PR is also the reviewer.
    /// </summary>
    private static async Task SubmitWithOwnPrFallbackAsync(
        PipelineStepContext context, int prNumber, string body,
        PullRequestReviewType reviewType, CancellationToken ct)
    {
        try
        {
            await context.RepoProvider.SubmitPullRequestReviewAsync(
                prNumber, body, reviewType, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
                                    && IsOwnPullRequestError(ex))
        {
            context.Logger.Warning(
                "Cannot request changes on own PR #{PrNumber}, downgrading to Comment review type",
                prNumber);
            await context.RepoProvider.SubmitPullRequestReviewAsync(
                prNumber, body, PullRequestReviewType.Comment, ct);
        }
    }

    /// <summary>
    /// Detects GitHub's "Can not request changes on your own pull request" error.
    /// </summary>
    private static bool IsOwnPullRequestError(Exception ex)
    {
        return ex.Message.Contains("request changes on your own pull request", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses structured findings from each agent's output, with per-agent retry
    /// when the agent has severity markers but no file:line findings.
    /// </summary>
    private static async Task<List<StructuredFinding>> ParseFindingsWithRetryAsync(
        PipelineStepContext context, InlineCommentSettings settings, CancellationToken ct)
    {
        var allFindings = new List<StructuredFinding>();
        var maxRetries = Math.Clamp(settings.MaxRetries, 0, 5);

        foreach (var kvp in context.Run.CodeReviewAgentFindings)
        {
            var agentName = kvp.Key;
            var agentOutput = kvp.Value;

            if (string.IsNullOrEmpty(agentOutput))
                continue;

            // Initial parse
            var findings = FindingsParser.Parse(agentOutput, agentName);
            var hasLocationFindings = findings.Any(f => f.FilePath is not null && f.LineNumber > 0);

            // Check if agent has severity markers but no file:line findings → candidate for retry
            if (!hasLocationFindings && maxRetries > 0)
            {
                var severityCounts = SeverityParser.Parse(agentOutput.Split('\n'));
                var hasMarkers = severityCounts.Critical > 0 || severityCounts.Warning > 0 || severityCounts.Suggestion > 0;

                if (hasMarkers)
                {
                    findings = await RetryAgentForStructuredOutputAsync(
                        context, agentName, agentOutput, maxRetries, ct);
                }
            }

            allFindings.AddRange(findings);
        }

        return allFindings;
    }

    /// <summary>
    /// Retries a specific agent to get structured output with file:line references.
    /// Returns the best findings obtained (from retry or original parse).
    /// </summary>
    private static async Task<IReadOnlyList<StructuredFinding>> RetryAgentForStructuredOutputAsync(
        PipelineStepContext context, string agentName, string originalOutput,
        int maxRetries, CancellationToken ct)
    {
        // Need AgentPhaseContext and ReviewerConfiguration for retry
        AgentPhaseContext? phaseContext = null;
        try
        {
            phaseContext = context.BuildAgentPhaseContext();
        }
        catch (InvalidOperationException)
        {
            // Cannot build context (Issue/ParsedIssue is null) — skip retry
            context.Logger.Warning("Cannot retry agent '{AgentName}' for structured output: AgentPhaseContext unavailable", agentName);
            context.Run.InlineCommentsDegraded = true;
            context.Run.InlineCommentsDegradedReason = "Retry skipped: pipeline context unavailable for follow-up prompts.";
            return FindingsParser.Parse(originalOutput, agentName);
        }

        // Find the ReviewerConfiguration for this agent
        var reviewerConfig = FindReviewerConfigForAgent(context, agentName);
        if (reviewerConfig is null)
        {
            context.Logger.Warning("Cannot retry agent '{AgentName}': no matching ReviewerConfiguration found", agentName);
            return FindingsParser.Parse(originalOutput, agentName);
        }

        var currentOutput = originalOutput;
        for (var retry = 0; retry < maxRetries; retry++)
        {
            try
            {
                const int maxPromptOutputLength = 8000;
                var promptOutput = currentOutput.Length > maxPromptOutputLength
                    ? currentOutput[..maxPromptOutputLength] + "\n\n...[output truncated for brevity]..."
                    : currentOutput;
                var followUpPrompt = string.Format(FollowUpPromptTemplate, promptOutput);
                var retryResponse = await context.AgentExecution.ExecuteFollowUpAsync(
                    phaseContext, reviewerConfig, followUpPrompt, ct);

                if (string.IsNullOrEmpty(retryResponse))
                {
                    context.Logger.Debug("Retry {Retry}/{MaxRetries} for agent '{AgentName}' returned empty response",
                        retry + 1, maxRetries, agentName);
                    continue;
                }

                var retryFindings = FindingsParser.Parse(retryResponse, agentName);
                var hasLocation = retryFindings.Any(f => f.FilePath is not null && f.LineNumber > 0);

                if (hasLocation)
                {
                    context.Logger.Debug("Retry {Retry}/{MaxRetries} for agent '{AgentName}' produced {Count} findings with location",
                        retry + 1, maxRetries, agentName, retryFindings.Count(f => f.FilePath is not null));
                    return retryFindings;
                }

                // Update currentOutput for next retry attempt
                currentOutput = retryResponse;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                context.Logger.Warning(ex, "Retry {Retry}/{MaxRetries} for agent '{AgentName}' failed",
                    retry + 1, maxRetries, agentName);
            }
        }

        // All retries exhausted — return original parse (findings without location)
        context.Logger.Debug("All {MaxRetries} retries exhausted for agent '{AgentName}', using original findings",
            maxRetries, agentName);
        return FindingsParser.Parse(originalOutput, agentName);
    }

    /// <summary>
    /// Finds the ReviewerConfiguration that contains an agent with the given name.
    /// Returns null if not found (graceful degradation).
    /// </summary>
    private static ReviewerConfiguration? FindReviewerConfigForAgent(PipelineStepContext context, string agentName)
    {
        var resolvedConfigs = context.ResolvedReviewerConfigs;
        if (resolvedConfigs is null)
            return null;

        return resolvedConfigs.FirstOrDefault(rc =>
            rc.Agents.Any(a => a.Name.Equals(agentName, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Safely dismisses previous reviews. Failures are logged but don't block the new review.
    /// </summary>
    private static async Task DismissPreviousReviewSafeAsync(PipelineStepContext context, int prNumber, CancellationToken ct)
    {
        try
        {
            await context.RepoProvider.DismissPreviousReviewAsync(
                prNumber, CommentMarkers.PrReview, DismissReason, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Warning(ex, "Failed to dismiss previous reviews on PR #{PrNumber}, continuing with new post", prNumber);
        }
    }

    /// <summary>
    /// Finds all existing review comments (by marker) and collapses them into a
    /// &lt;details&gt; block so they don't clutter the PR but remain accessible.
    /// Best-effort — failures are logged but don't block the new review post.
    /// </summary>
    private static async Task CollapseExistingReviewsAsync(PipelineStepContext context, int prNumber, CancellationToken ct)
    {
        try
        {
            var existingId = await context.RepoProvider.FindExistingReviewCommentAsync(
                prNumber, CommentMarkers.PrReview, ct);

            const int maxCollapseIterations = 20;
            var iterations = 0;
            while (existingId is not null && iterations < maxCollapseIterations)
            {
                iterations++;
                var collapsedBody = $"<!-- agent:pr-review-superseded -->\n{SupersededPrefix}_This review has been superseded by a newer run below._\n{SupersededSuffix}";
                await context.RepoProvider.UpdateReviewCommentAsync(
                    prNumber, existingId.Value, collapsedBody, ct);

                context.Logger.Debug("Collapsed previous review comment {CommentId} on PR #{PrNumber}",
                    existingId.Value, prNumber);

                existingId = await context.RepoProvider.FindExistingReviewCommentAsync(
                    prNumber, CommentMarkers.PrReview, ct);
            }

            if (iterations >= maxCollapseIterations)
            {
                context.Logger.Warning("Reached max collapse iterations ({Max}) on PR #{PrNumber}, some old reviews may remain uncollapsed",
                    maxCollapseIterations, prNumber);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Warning(ex, "Failed to collapse existing review comments on PR #{PrNumber}, continuing with new post", prNumber);
        }
    }
}
