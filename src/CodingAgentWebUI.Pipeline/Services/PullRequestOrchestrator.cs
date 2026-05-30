using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Handles pull request creation: commit, push, verify commits ahead,
/// build PR info, create PR, and file change stats. Extracted from
/// PipelineOrchestrationService to reduce file size.
/// </summary>
internal class PullRequestOrchestrator
{
    private readonly Serilog.ILogger _logger;

    public PullRequestOrchestrator(Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Commits uncommitted changes, pushes the branch, verifies commits ahead,
    /// builds PR info, and creates the pull request.
    /// Returns the PR URL, or null if no commits ahead of base.
    /// </summary>
    public async Task<string?> CreatePullRequestAsync(
        PipelineRun run,
        QualityGateReport report,
        bool isDraft,
        IRepositoryProvider repoProvider,
        IssueDetail? issue,
        IReadOnlyList<IssueComment>? issueComments,
        PipelineConfiguration config,
        CancellationToken ct,
        Action<string>? onOutputLine = null,
        bool isRework = false)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(repoProvider);
        ArgumentNullException.ThrowIfNull(config);
        // Commit any uncommitted changes
        try
        {
            var commitMessage = PipelineFormatting.GenerateCommitMessage(
                run.IssueTitle, run.IssueIdentifier);
            var blacklisted = await repoProvider.CommitAllAsync(
                run.WorkspacePath!, commitMessage, config.BlacklistedPaths, ct);
            if (blacklisted.Count > 0)
            {
                RecordBlacklistedFiles(run, blacklisted, config);
                if (config.BlacklistMode == BlacklistMode.Fail)
                    return null; // Caller handles failure transition
            }
            // NOTE: [UX-16] File counts may be stale — UpdateFileChangeStatsAsync runs after push, not before this line
            onOutputLine?.Invoke($"📦 Committed {run.FilesChangedCount} files (+{run.LinesAdded} -{run.LinesRemoved})");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No changes to commit"))
        {
            _logger.Information("Pipeline {RunId} no uncommitted changes, skipping commit", run.RunId);
        }

        // Always force-push: pipeline owns these feature branches and rebase may rewrite history
        await repoProvider.PushBranchAsync(run.WorkspacePath!, run.BranchName!, forcePush: true, ct);
        onOutputLine?.Invoke($"🔀 Pushed to origin/{run.BranchName}");

        // Refresh file change stats
        await UpdateFileChangeStatsAsync(run, repoProvider);

        // Verify commits ahead
        if (!await repoProvider.HasCommitsAheadAsync(run.WorkspacePath!, ct))
        {
            _logger.Warning("Pipeline {RunId} branch has no commits ahead of {BaseBranch}",
                run.RunId, repoProvider.BaseBranch);
            onOutputLine?.Invoke("⚠️ No commits ahead of base branch");
            return null;
        }

        // Build PR info
        var testsPassed = report.Tests.TestsPassed ?? 0;
        var testsFailed = report.Tests.TestsFailed ?? 0;
        var testsSkipped = report.Tests.TestsSkipped ?? 0;
        var coverage = report.Coverage?.CoveragePercent;

        var fileChanges = await repoProvider.GetFileChangesAsync(run.WorkspacePath!, ct);

        var issueTitle = issue?.Title ?? run.IssueTitle;

        var prTitle = PipelineFormatting.GeneratePrTitle(run.IssueTitle, run.IssueIdentifier);

        var codeReviewSummary = run.CodeReviewAgentsRun is { Count: > 0 }
            ? new CodeReviewSummary(
                run.CodeReviewAgentsRun,
                run.CodeReviewCriticalCount,
                run.CodeReviewWarningCount,
                run.CodeReviewSuggestionCount,
                run.CodeReviewAgentFindings
                    .Select(kv => new AgentFindings(kv.Key, kv.Value))
                    .ToArray())
            : null;

        var prBody = PipelineFormatting.GeneratePrBody(
            run.IssueIdentifier, testsPassed, testsFailed, testsSkipped,
            coverage, fileChanges, issueTitle, isDraft, issueComments,
            run.BlacklistedFilesDetected.Count > 0 ? run.BlacklistedFilesDetected : null,
            run.ModelName,
            codeReviewSummary);

        if (isRework || !string.IsNullOrEmpty(run.PullRequestNumber))
        {
            // Rework or draft PR already exists: update existing PR body.
            // run.PullRequestUrl and run.PullRequestNumber are already set
            // (from LinkedPullRequest in rework, or from CreateDraftPrIfNotExistsAsync).
            run.IsDraftPr = isDraft;
            run.CompletedAt = DateTime.UtcNow;

            try
            {
                var prNumber = int.Parse(run.PullRequestNumber!);
                await repoProvider.UpdatePullRequestAsync(prNumber, prBody, !isDraft, ct);
                onOutputLine?.Invoke($"📝 Updated PR #{run.PullRequestNumber} body");
                if (!isDraft)
                    onOutputLine?.Invoke($"✅ PR #{run.PullRequestNumber} marked ready for review");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Non-fatal — code is already pushed to the branch
                _logger.Warning(ex, "Pipeline {RunId} failed to update PR body, continuing", run.RunId);
            }

            return run.PullRequestUrl;
        }
        else
        {
            // New-issue: create new PR
            var prInfo = new PullRequestInfo
            {
                Title = prTitle,
                Body = prBody,
                BranchName = run.BranchName!,
                BaseBranch = repoProvider.BaseBranch,
                IsDraft = isDraft
            };

            var prUrl = await repoProvider.CreatePullRequestAsync(prInfo, ct);
            run.PullRequestUrl = prUrl;
            run.IsDraftPr = isDraft;
            run.PullRequestNumber = ExtractPrNumber(prUrl);
            run.CompletedAt = DateTime.UtcNow;

            var prLabel = isDraft ? "Draft pull request" : "Pull request";
            onOutputLine?.Invoke($"🔗 {prLabel} #{run.PullRequestNumber} created");

            return prUrl;
        }
    }

    /// <summary>
    /// Records blacklisted files on the pipeline run and logs the violation.
    /// Merges with any previously detected files.
    /// </summary>
    public void RecordBlacklistedFiles(PipelineRun run, IReadOnlyList<string> blacklisted, PipelineConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(blacklisted);
        ArgumentNullException.ThrowIfNull(config);
        if (blacklisted.Count == 0) return;

        var merged = run.BlacklistedFilesDetected.Count > 0
            ? run.BlacklistedFilesDetected.Concat(blacklisted).Distinct().ToList()
            : blacklisted.ToList();
        run.BlacklistedFilesDetected = merged;

        _logger.Warning(
            "Pipeline {RunId} blacklisted {Count} file(s) excluded from commit (mode={BlacklistMode}, patterns={Patterns}): {Files}",
            run.RunId, blacklisted.Count, config.BlacklistMode, config.BlacklistedPaths, blacklisted);
    }

    /// <summary>Updates file change statistics on the run.</summary>
    // NOTE: [ARC-10] CancellationToken.None should propagate caller's token — deferred to separate issue
    public async Task UpdateFileChangeStatsAsync(PipelineRun run, IRepositoryProvider repoProvider)
    {
        try
        {
            if (string.IsNullOrEmpty(run.WorkspacePath)) return;
            var changes = await repoProvider.GetFileChangesAsync(run.WorkspacePath, CancellationToken.None);
            run.FilesChangedCount = changes.Count;
            run.LinesAdded = changes.Sum(c => c.LinesAdded);
            run.LinesRemoved = changes.Sum(c => c.LinesDeleted);
            _logger.Debug("Pipeline {RunId} file changes: {Count} files, +{Added} -{Removed} lines",
                run.RunId, changes.Count, run.LinesAdded, run.LinesRemoved);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Pipeline {RunId} failed to compute file change stats", run.RunId);
        }
    }

    /// <summary>Extracts PR number from a GitHub PR URL.</summary>
    internal static string? ExtractPrNumber(string? prUrl)
    {
        if (string.IsNullOrEmpty(prUrl)) return null;
        var lastSlash = prUrl.LastIndexOf('/');
        if (lastSlash >= 0 && lastSlash < prUrl.Length - 1)
        {
            var candidate = prUrl[(lastSlash + 1)..];
            if (int.TryParse(candidate, out _)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Creates a draft pull request with a minimal body if one does not already exist for this run.
    /// No-op when the run already has a linked PR (rework) or a PR URL (already created).
    /// Returns the PR URL, or null if creation was skipped.
    /// </summary>
    public async Task<string?> CreateDraftPrIfNotExistsAsync(
        PipelineRun run,
        IRepositoryProvider repoProvider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(repoProvider);

        // Skip if PR already exists (rework flow or already created in a prior push)
        if (run.LinkedPullRequest != null || !string.IsNullOrEmpty(run.PullRequestUrl))
        {
            _logger.Debug("Pipeline {RunId} draft PR creation skipped — PR already exists: {PrUrl}",
                run.RunId, run.PullRequestUrl ?? run.LinkedPullRequest?.Url);
            return run.PullRequestUrl ?? run.LinkedPullRequest?.Url;
        }

        if (string.IsNullOrEmpty(run.BranchName))
        {
            _logger.Warning("Pipeline {RunId} cannot create draft PR — branch name is null", run.RunId);
            return null;
        }

        // Verify commits ahead before creating PR
        if (!await repoProvider.HasCommitsAheadAsync(run.WorkspacePath!, ct))
        {
            _logger.Warning("Pipeline {RunId} cannot create draft PR — no commits ahead of base", run.RunId);
            return null;
        }

        var prTitle = PipelineFormatting.GeneratePrTitle(run.IssueTitle, run.IssueIdentifier);
        var prBody = $"🤖 Agent working on #{run.IssueIdentifier}\n\n" +
                     "_This draft PR was created automatically. " +
                     "It will be updated with quality gate results and marked ready for review upon completion._";

        var prInfo = new PullRequestInfo
        {
            Title = prTitle,
            Body = prBody,
            BranchName = run.BranchName,
            BaseBranch = repoProvider.BaseBranch,
            IsDraft = true
        };

        string prUrl;
        try
        {
            prUrl = await repoProvider.CreatePullRequestAsync(prInfo, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            && ex.Message.Contains("A pull request already exists", StringComparison.OrdinalIgnoreCase))
        {
            // PR already exists for this branch (e.g., from a previous run that wasn't detected as rework).
            // Look up the existing PR via GetAgentPullRequestsAsync.
            _logger.Information("Pipeline {RunId} draft PR already exists for branch {BranchName}, looking up existing PR",
                run.RunId, run.BranchName);

            var existingPrs = await repoProvider.GetAgentPullRequestsAsync(run.IssueIdentifier, ct);
            var matchingPr = existingPrs.FirstOrDefault(pr => pr.BranchName == run.BranchName);
            if (matchingPr != null)
            {
                run.PullRequestUrl = matchingPr.Url;
                run.PullRequestNumber = matchingPr.Number.ToString();
                run.IsDraftPr = matchingPr.IsDraft;
                _logger.Information("Pipeline {RunId} found existing PR #{PrNumber}: {PrUrl}",
                    run.RunId, run.PullRequestNumber, run.PullRequestUrl);
                return run.PullRequestUrl;
            }

            // Couldn't find it — let the error propagate
            _logger.Warning(ex, "Pipeline {RunId} PR already exists but couldn't find it via API", run.RunId);
            throw;
        }

        run.PullRequestUrl = prUrl;
        run.PullRequestNumber = ExtractPrNumber(prUrl);
        run.IsDraftPr = true;

        _logger.Information("Pipeline {RunId} created draft PR #{PrNumber}: {PrUrl}",
            run.RunId, run.PullRequestNumber, prUrl);

        return prUrl;
    }

    /// <summary>
    /// Finalizes an existing pull request: updates the body with quality gate results
    /// and optionally marks it ready for review.
    /// </summary>
    public async Task<string?> FinalizePullRequestAsync(
        PipelineRun run,
        QualityGateReport report,
        bool isDraft,
        IRepositoryProvider repoProvider,
        IssueDetail? issue,
        IReadOnlyList<IssueComment>? issueComments,
        PipelineConfiguration config,
        CancellationToken ct,
        Action<string>? onOutputLine = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(repoProvider);
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrEmpty(run.PullRequestNumber))
        {
            _logger.Warning("Pipeline {RunId} cannot finalize PR — no PR number available", run.RunId);
            return null;
        }

        // Commit any remaining uncommitted changes
        try
        {
            var commitMessage = PipelineFormatting.GenerateCommitMessage(run.IssueTitle, run.IssueIdentifier);
            var blacklisted = await repoProvider.CommitAllAsync(
                run.WorkspacePath!, commitMessage, config.BlacklistedPaths, ct);
            if (blacklisted.Count > 0)
            {
                RecordBlacklistedFiles(run, blacklisted, config);
                if (config.BlacklistMode == BlacklistMode.Fail)
                    return null;
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No changes to commit"))
        {
            _logger.Information("Pipeline {RunId} no uncommitted changes during finalization, skipping commit", run.RunId);
        }

        // Push final state
        await repoProvider.PushBranchAsync(run.WorkspacePath!, run.BranchName!, forcePush: true, ct);
        onOutputLine?.Invoke($"🔀 Pushed final changes to origin/{run.BranchName}");

        // Refresh file change stats
        await UpdateFileChangeStatsAsync(run, repoProvider);

        // Build the full PR body
        var testsPassed = report.Tests.TestsPassed ?? 0;
        var testsFailed = report.Tests.TestsFailed ?? 0;
        var testsSkipped = report.Tests.TestsSkipped ?? 0;
        var coverage = report.Coverage?.CoveragePercent;
        var fileChanges = await repoProvider.GetFileChangesAsync(run.WorkspacePath!, ct);
        var issueTitle = issue?.Title ?? run.IssueTitle;

        var codeReviewSummary = run.CodeReviewAgentsRun is { Count: > 0 }
            ? new CodeReviewSummary(
                run.CodeReviewAgentsRun,
                run.CodeReviewCriticalCount,
                run.CodeReviewWarningCount,
                run.CodeReviewSuggestionCount,
                run.CodeReviewAgentFindings
                    .Select(kv => new AgentFindings(kv.Key, kv.Value))
                    .ToArray())
            : null;

        var prBody = PipelineFormatting.GeneratePrBody(
            run.IssueIdentifier, testsPassed, testsFailed, testsSkipped,
            coverage, fileChanges, issueTitle, isDraft, issueComments,
            run.BlacklistedFilesDetected.Count > 0 ? run.BlacklistedFilesDetected : null,
            run.ModelName,
            codeReviewSummary);

        // Update PR body and mark ready (or leave as draft)
        run.IsDraftPr = isDraft;
        run.CompletedAt = DateTime.UtcNow;

        try
        {
            var prNumber = int.Parse(run.PullRequestNumber);
            await repoProvider.UpdatePullRequestAsync(prNumber, prBody, !isDraft, ct);
            onOutputLine?.Invoke($"📝 Updated PR #{run.PullRequestNumber} body");
            if (!isDraft)
                onOutputLine?.Invoke($"✅ PR #{run.PullRequestNumber} marked ready for review");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Pipeline {RunId} failed to update/finalize PR, continuing", run.RunId);
        }

        return run.PullRequestUrl;
    }
}
