using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Services;

/// <summary>
/// Handles pull request creation: commit, push, verify commits ahead,
/// build PR info, create PR, and file change stats. Extracted from
/// PipelineOrchestrationService to reduce file size.
/// </summary>
public class PullRequestOrchestrator
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
    // TODO: [ARC-10] Add ArgumentNullException.ThrowIfNull for public method parameters
    public async Task<string?> CreatePullRequestAsync(
        PipelineRun run,
        QualityGateReport report,
        bool isDraft,
        IRepositoryProvider repoProvider,
        IssueDetail? issue,
        ParsedIssue? parsedIssue,
        IReadOnlyList<IssueComment>? issueComments,
        PipelineConfiguration config,
        CancellationToken ct)
    {
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
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("No changes to commit"))
        {
            _logger.Information("Pipeline {RunId} no uncommitted changes, skipping commit", run.RunId);
        }

        // Push
        await repoProvider.PushBranchAsync(run.WorkspacePath!, run.BranchName!, ct);

        // Refresh file change stats
        await UpdateFileChangeStatsAsync(run, repoProvider);

        // Verify commits ahead
        if (!await repoProvider.HasCommitsAheadAsync(run.WorkspacePath!, ct))
        {
            _logger.Warning("Pipeline {RunId} branch has no commits ahead of {BaseBranch}",
                run.RunId, repoProvider.BaseBranch);
            return null;
        }

        // Build PR info
        var testsPassed = report.Tests.TestsPassed ?? 0;
        var testsFailed = report.Tests.TestsFailed ?? 0;
        var testsSkipped = report.Tests.TestsSkipped ?? 0;
        var coverage = report.Coverage?.CoveragePercent;

        var fileChanges = await repoProvider.GetFileChangesAsync(run.WorkspacePath!, ct);

        var issueTitle = issue?.Title ?? run.IssueTitle;
        var issueDescription = issue?.Description ?? string.Empty;
        var acceptanceCriteria = parsedIssue?.AcceptanceCriteria
            ?? (IReadOnlyList<string>)Array.Empty<string>();

        var prTitle = PipelineFormatting.GeneratePrTitle(run.IssueTitle, run.IssueIdentifier);

        var codeReviewSummary = config.CodeReview.Enabled
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
            coverage, fileChanges, issueTitle, issueDescription,
            acceptanceCriteria, isDraft, issueComments,
            run.BlacklistedFilesDetected.Count > 0 ? run.BlacklistedFilesDetected : null,
            run.ModelName,
            codeReviewSummary);

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

        return prUrl;
    }

    /// <summary>
    /// Records blacklisted files on the pipeline run and logs the violation.
    /// Merges with any previously detected files.
    /// </summary>
    // TODO: [ARC-10] Duplicated blacklist-fail transition logic between orchestrator and PR orchestrator
    // TODO: [ARC-10] Add ArgumentNullException.ThrowIfNull for public method parameters
    public void RecordBlacklistedFiles(PipelineRun run, IReadOnlyList<string> blacklisted, PipelineConfiguration config)
    {
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
    // TODO: [ARC-10] CancellationToken.None should propagate caller's token
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
}
