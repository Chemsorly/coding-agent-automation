using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Prompts;

namespace CodingAgent.Agent.Executors;

/// <summary>
/// Executes refactoring detection: clones the code repo, runs the holistic analysis
/// agent prompt, parses proposals from the workspace, and creates GitHub issues.
/// </summary>
public sealed partial class RefactoringExecutor : ConsolidationExecutorBase
{
    protected override string ExecutorName => "Refactoring detection";

    /// <summary>
    /// Sentinel provider ID used by DependencyResolver within CreateIssuesAsync.
    /// All refactoring proposals go to the same single issue provider, so there are no
    /// cross-tracker dependencies — every dependency always emits the short #N form.
    /// </summary>
    private const string SingleTrackerProviderId = "__refactoring_single_tracker__";

    public RefactoringExecutor(Serilog.ILogger logger) : base(logger)
    {
    }

    /// <summary>
    /// Executes the phased refactoring detection workflow:
    /// 1. Clone code repo + brain into the run's workspace
    /// 2. Hotspot analysis (git log)
    /// 3. Phase 0: Context extraction (project conventions)
    /// 4. Phase 1: Parallel focused detection (3 sub-agents: structural, correctness, design)
    /// 5. Phase 2: Aggregation + prioritization → produces proposals JSON
    /// 6. If no proposals: return success
    /// 7. Adversarial review (if enabled)
    /// 8. Validate proposals (<see cref="RefactoringProposalValidator"/>), create GitHub issues (capped at MaxRefactoringProposals)
    /// 9. Return summary
    /// </summary>
    public async Task<ConsolidationJobResult> ExecuteAsync(
        ConsolidationJobMessage job,
        IRepositoryProvider repoProvider,
        IRepositoryProvider? brainProvider,
        IIssueProvider issueProvider,
        IAgentProvider agentProvider,
        CancellationToken ct,
        Action<string>? onOutputLine = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(repoProvider);
        ArgumentNullException.ThrowIfNull(issueProvider);
        ArgumentNullException.ThrowIfNull(agentProvider);

        var invalid = ValidateJobId(job);
        if (invalid is not null) return invalid;

        var workspacePath = ResolveWorkspacePath(job);

        return await WrapWithCancellationHandlingAsync(job.JobId, async () =>
        {
            // 1. Clone code repo (optionally + brain repo)
            Directory.CreateDirectory(workspacePath);
            Logger.Information("Cloning code repo for refactoring detection run {RunId} into {Workspace}", job.JobId, workspacePath);

            await RunWithTracingAsync("RefactoringDetection.Clone", job.JobId, async _ =>
            {
                await repoProvider.CloneAsync(workspacePath, ct);
                if (brainProvider is not null)
                    await TryCloneBrainRepoAsync(brainProvider, workspacePath, job.JobId, ct);
            });

            // 2b. Query open issues for deduplication context (must run before hotspot analysis to avoid
            //     calling ListOpenIssuesAsync twice — CollectScanIssueReferencesAsync reuses the result)
            var (issueContext, openIssueTitles, openIssues) = await TryBuildIssueContextAsync(issueProvider, job.JobId, ct);

            // 2. Hotspot analysis — leave out commits that implement scan issues
            var scanIssueReferences = await CollectScanIssueReferencesAsync(
                issueProvider, openIssues, job.PipelineConfiguration.HotspotAnalysisLookback, ct);
            await RunWithTracingAsync("RefactoringDetection.HotspotAnalysis", job.JobId, async _ =>
            {
                await WriteHotspotAnalysisAsync(workspacePath, job.PipelineConfiguration.HotspotAnalysisLookback, scanIssueReferences, ct);
            });

            // 2c. Query past proposal outcomes for feedback context
            var (outcomeContext, closedIssueTitles) = await TryBuildOutcomeContextAsync(issueProvider, job.PipelineConfiguration, job.JobId, ct);

            // 2d. Write both for the review step, which checks overlap too
            await TryWriteIssueContextFileAsync(workspacePath, issueContext, outcomeContext, job.JobId, ct);

            var commitSha = await TryGetHeadCommitAsync(workspacePath, ct);

            // ── Phased Refactoring Detection ──
            var phasedResult = await ExecutePhasedRefactoringAsync(job, agentProvider, workspacePath, issueContext, outcomeContext, ct);
            if (!phasedResult.Success) return phasedResult;

            return await FinalizeProposalsAsync(
                job, agentProvider, issueProvider, workspacePath,
                new ScanBaseline([.. openIssueTitles, .. closedIssueTitles], commitSha), onOutputLine, ct);
        }, ct);
    }

    private async Task TryCloneBrainRepoAsync(IRepositoryProvider brainProvider, string workspacePath, string jobId, CancellationToken ct)
    {
        var brainPath = Path.Combine(workspacePath, AgentWorkspacePaths.BrainDirectory);
        try
        {
            await brainProvider.CloneAsync(brainPath, ct);
            Logger.Information("Brain repo cloned for architectural context in run {RunId}", jobId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "Failed to clone brain repo for context in run {RunId}, continuing without it", jobId);
        }
    }

    /// <summary>
    /// The footer sentence every issue of this scan carries (see <see cref="FormatIssueBody"/>). It tells the
    /// scan's issues apart from other <c>agent:generated</c> issues, such as decomposition sub-issues,
    /// without a label of their own; issues filed before this constant existed carry it too.
    /// </summary>
    internal const string GeneratedIssueFooter = "This issue was automatically generated by the refactoring detection consolidation loop";

    /// <summary>The number of open issues read for duplicate checks: the providers' maximum page size.</summary>
    private const int OpenIssueContextLimit = 100;

    /// <summary>The number of past scan issues whose outcome and implementer feedback go into the prompt.</summary>
    private const int OutcomeContextLimit = 20;

    internal static bool IsScanIssue(IssueSummary issue) =>
        issue.Description?.Contains(GeneratedIssueFooter, StringComparison.Ordinal) == true;

    /// <summary>
    /// Builds the open-issue section of the aggregation prompt from every open issue, whatever its labels
    /// or age: a proposal must not duplicate any of them. Also returns their titles, so issue creation can
    /// reject a proposal that duplicates one.
    /// </summary>
    private async Task<(string? Context, IReadOnlyList<string> Titles, IReadOnlyList<IssueSummary> OpenIssues)> TryBuildIssueContextAsync(
        IIssueProvider issueProvider, string jobId, CancellationToken ct)
    {
        try
        {
            var openIssues = (await issueProvider.ListOpenIssuesAsync(1, OpenIssueContextLimit, null, ct)).Items;
            var scanIssues = openIssues.Where(IsScanIssue).ToList();
            var otherIssues = openIssues.Where(i => !IsScanIssue(i)).ToList();
            var context = ConsolidationPromptBuilder.BuildOpenIssueContext(scanIssues, otherIssues);
            if (!string.IsNullOrEmpty(context))
                Logger.Information("Including {Count} open issues as context for refactoring detection in run {RunId}",
                    openIssues.Count, jobId);
            return (context, openIssues.Select(i => i.Title).ToList(), openIssues);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "Failed to query open issues for context in run {RunId}, continuing without", jobId);
            return (null, [], []);
        }
    }

    /// <summary>
    /// Returns the set of issue references (e.g. <c>#3284</c>) for all scan issues — open and recently
    /// closed — so that <see cref="WriteHotspotAnalysisAsync"/> can exclude commits that implement them.
    /// <para>
    /// Open scan issues are taken from <paramref name="openIssues"/> (already fetched by
    /// <see cref="TryBuildIssueContextAsync"/>). Closed ones are queried in pages while
    /// <c>HasMore = true</c>, up to 10 pages. On any non-cancellation exception the method
    /// logs a warning and returns the references collected so far — the hotspot analysis must still run.
    /// </para>
    /// </summary>
    // TODO [WARNING]: Return type is HashSet<string> (mutable). Consider returning IReadOnlySet<string>
    // to prevent callers from accidentally mutating the collection between creation and consumption.
    // Currently safe because WriteHotspotAnalysisAsync only reads it, but a future caller could mutate it.
    internal async Task<HashSet<string>> CollectScanIssueReferencesAsync(
        IIssueProvider issueProvider,
        IReadOnlyList<IssueSummary> openIssues,
        TimeSpan lookback,
        CancellationToken ct)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);

        // Open scan issues
        foreach (var issue in openIssues.Where(IsScanIssue))
            references.Add(issueProvider.FormatIssueReference(issue.Identifier));

        // Closed scan issues within the lookback window
        try
        {
            var since = DateTime.UtcNow - lookback;
            for (var page = 1; page <= 10; page++)
            {
                var result = await issueProvider.ListClosedIssuesAsync(
                    page, pageSize: 100, labels: new[] { AgentLabels.Generated }, since: since, ct);

                foreach (var issue in result.Items.Where(IsScanIssue))
                    references.Add(issueProvider.FormatIssueReference(issue.Identifier));

                if (!result.HasMore)
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "Failed to query closed scan issues for hotspot exclusions, continuing with {Count} reference(s) collected so far", references.Count);
        }

        return references;
    }

    /// <summary>
    /// Builds the past-outcome section of the aggregation prompt from the scan's own closed issues: their
    /// outcome, and what the agents that implemented them said the issue got wrong. Also returns their
    /// titles, so issue creation can reject a proposal that re-files one of them.
    /// </summary>
    private async Task<(string Context, IReadOnlyList<string> Titles)> TryBuildOutcomeContextAsync(
        IIssueProvider issueProvider, PipelineConfiguration config, string jobId, CancellationToken ct)
    {
        try
        {
            var since = DateTime.UtcNow - config.RefactoringOutcomeLookback;
            // agent:generated narrows the query; the footer separates the scan's issues from decomposition sub-issues
            var closedResult = await issueProvider.ListClosedIssuesAsync(
                page: 1, pageSize: 50, labels: new[] { AgentLabels.Generated }, since: since, ct);
            var scanIssues = closedResult.Items.Where(IsScanIssue).Take(OutcomeContextLimit).ToList();
            var feedback = await ReadImplementerFeedbackAsync(issueProvider, scanIssues, jobId, ct);
            var titles = scanIssues.Select(i => i.Title).ToList();
            return (ConsolidationPromptBuilder.BuildProposalOutcomeContext(scanIssues, feedback), titles);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "Failed to query closed issues for feedback context in run {RunId}", jobId);
            return (string.Empty, []);
        }
    }

    /// <summary>
    /// Reads the latest issue-quality feedback comment (<see cref="CommentMarkers.IssueFeedback"/>) of each
    /// issue, which the agent that implemented the issue posts when the issue missed something. An issue
    /// whose comments cannot be read is skipped.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ReadImplementerFeedbackAsync(
        IIssueProvider issueProvider, IReadOnlyList<IssueSummary> issues, string jobId, CancellationToken ct)
    {
        var feedback = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var identifier in issues.Select(i => i.Identifier))
        {
            try
            {
                var comments = await issueProvider.ListCommentsAsync(identifier, ct);
                var latest = comments
                    .Where(c => c.Body.Contains(CommentMarkers.IssueFeedback, StringComparison.Ordinal))
                    .MaxBy(c => c.CreatedAt);
                if (latest is not null && FeedbackCommentFormatter.ReadSummary(latest.Body) is { } summary)
                    feedback[identifier] = summary;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Warning(ex, "Failed to read the comments of issue {Identifier} in run {RunId}, skipping its feedback",
                    identifier, jobId);
            }
        }

        if (feedback.Count > 0)
            Logger.Information("Including implementer feedback from {Count} past issue(s) in run {RunId}", feedback.Count, jobId);
        return feedback;
    }

    /// <summary>
    /// Writes the open-issue and past-outcome context to the workspace so the review step, which runs in
    /// its own session without the aggregation prompt, can check proposals for overlap.
    /// </summary>
    private async Task TryWriteIssueContextFileAsync(
        string workspacePath, string? issueContext, string outcomeContext, string jobId, CancellationToken ct)
    {
        var content = (issueContext ?? string.Empty) + outcomeContext;
        if (string.IsNullOrWhiteSpace(content))
            return;

        try
        {
            var path = Path.Combine(workspacePath, AgentWorkspacePaths.RefactoringIssueContextFilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "Failed to write the issue context file in run {RunId}, the review cannot check overlap", jobId);
        }
    }

    /// <summary>
    /// Returns the commit the analysis runs against, so issues can say which commit their line numbers
    /// refer to. Returns <c>null</c> when it cannot be read.
    /// </summary>
    private async Task<string?> TryGetHeadCommitAsync(string workspacePath, CancellationToken ct)
    {
        try
        {
            var sha = (await RunGitCommandAsync(workspacePath, "rev-parse HEAD", ct)).Trim();
            return sha.Length > 0 ? sha : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "Could not read the analyzed commit, issues will not name it");
            return null;
        }
    }

    /// <summary>
    /// Phased multi-agent refactoring path.
    /// Phase 0 → Phase 1 (3 parallel) → Phase 2 (aggregation) → finalize.
    /// </summary>
    private async Task<ConsolidationJobResult> ExecutePhasedRefactoringAsync(
        ConsolidationJobMessage job,
        IAgentProvider agentProvider,
        string workspacePath,
        string? issueContext,
        string outcomeContext,
        CancellationToken ct)
    {
        ConsolidationJobResult? failure;

        // Phase 0: Context Extraction
        Logger.Information("Phase 0: Extracting project conventions for run {RunId}", job.JobId); // NOSONAR S6664 — one progress log per scan phase

        (_, failure) = await RunWithTracingAsync("RefactoringDetection.Phase0.ContextExtraction", job.JobId, async _ =>
        {
            return await ExecuteAgentAndCheckAsync(
                agentProvider,
                new AgentRequest
                {
                    Prompt = ConsolidationPromptBuilder.BuildRefactoringContextExtractionPrompt(),
                    WorkspacePath = workspacePath,
                    Timeout = job.PipelineConfiguration.AgentTimeout
                },
                job.JobId,
                ct);
        });

        if (failure is not null) return failure;

        // Phase 1: Parallel Focused Detection (3 sub-agents)
        Logger.Information("Phase 1: Dispatching 3 parallel analysis agents for run {RunId}", job.JobId);

        var phase1Tasks = new[]
        {
            RunWithTracingAsync("RefactoringDetection.Phase1.StructuralDebt", job.JobId, async _ =>
            {
                return await ExecuteAgentAndCheckAsync(
                    agentProvider,
                    new AgentRequest
                    {
                        Prompt = ConsolidationPromptBuilder.BuildRefactoringStructuralDebtPrompt(),
                        WorkspacePath = workspacePath,
                        Timeout = job.PipelineConfiguration.AgentTimeout
                    },
                    job.JobId,
                    ct);
            }),
            RunWithTracingAsync("RefactoringDetection.Phase1.Correctness", job.JobId, async _ =>
            {
                return await ExecuteAgentAndCheckAsync(
                    agentProvider,
                    new AgentRequest
                    {
                        Prompt = ConsolidationPromptBuilder.BuildRefactoringCorrectnessPrompt(),
                        WorkspacePath = workspacePath,
                        Timeout = job.PipelineConfiguration.AgentTimeout
                    },
                    job.JobId,
                    ct);
            }),
            RunWithTracingAsync("RefactoringDetection.Phase1.DesignConsistency", job.JobId, async _ =>
            {
                return await ExecuteAgentAndCheckAsync(
                    agentProvider,
                    new AgentRequest
                    {
                        Prompt = ConsolidationPromptBuilder.BuildRefactoringDesignConsistencyPrompt(),
                        WorkspacePath = workspacePath,
                        Timeout = job.PipelineConfiguration.AgentTimeout
                    },
                    job.JobId,
                    ct);
            })
        };

        var phase1Results = await Task.WhenAll(phase1Tasks);

        // Check for failures — log but continue if at least one agent succeeded
        var phase1Failures = phase1Results.Where(r => r.Failure is not null).ToList();
        if (phase1Failures.Count == phase1Results.Length)
        {
            return CreateFailureResult(job.JobId, "All Phase 1 analysis agents failed");
        }

        if (phase1Failures.Count > 0)
        {
            Logger.Warning("{FailCount}/{TotalCount} Phase 1 agents failed in run {RunId}, continuing with partial results",
                phase1Failures.Count, phase1Results.Length, job.JobId);
        }

        // Phase 2: Aggregation & Prioritization
        Logger.Information("Phase 2: Aggregating and prioritizing findings for run {RunId}", job.JobId);

        var aggregationPrompt = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt(
            job.PipelineConfiguration.MaxRefactoringProposals,
            issueContext,
            outcomeContext.Length > 0 ? outcomeContext : null);

        (_, failure) = await RunWithTracingAsync("RefactoringDetection.Phase2.Aggregation", job.JobId, async _ =>
        {
            return await ExecuteAgentAndCheckAsync(
                agentProvider,
                new AgentRequest
                {
                    Prompt = aggregationPrompt,
                    WorkspacePath = workspacePath,
                    Timeout = job.PipelineConfiguration.AgentTimeout
                },
                job.JobId,
                ct);
        });

        if (failure is not null) return failure;

        // Proposals are now at .agent/refactoring-proposals.json — return success
        // (the caller will finalize with review + issue creation)
        return new ConsolidationJobResult { JobId = job.JobId, Success = true, Summary = "Phased analysis complete" };
    }

    /// <summary>
    /// Repository state captured before the analysis phases: the open and closed issue titles proposals are
    /// deduplicated against, and the commit the analysis ran on (null when it could not be read).
    /// </summary>
    private sealed record ScanBaseline(IReadOnlyList<string> ExistingIssueTitles, string? CommitSha);

    /// <summary>
    /// Shared finalization: parse proposals → adversarial review → create GitHub issues.
    /// Used by both phased and legacy paths.
    /// </summary>
    private async Task<ConsolidationJobResult> FinalizeProposalsAsync(
        ConsolidationJobMessage job,
        IAgentProvider agentProvider,
        IIssueProvider issueProvider,
        string workspacePath,
        ScanBaseline baseline,
        Action<string>? onOutputLine,
        CancellationToken ct)
    {
        var proposalsFilePath = Path.Combine(workspacePath, AgentWorkspacePaths.RefactoringProposalsFilePath);
        var proposals = await ParseProposalsAsync(proposalsFilePath, ct);

        if (proposals is null)
        {
            return CreateFailureResult(job.JobId, "Failed to parse refactoring proposals JSON from agent output");
        }

        if (proposals.Count == 0)
        {
            Logger.Information("No refactoring opportunities identified in run {RunId}", job.JobId);
            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = true,
                Summary = "No refactoring opportunities identified"
            };
        }

        // Adversarial review (if enabled and proposals non-empty)
        AdversarialReviewResult reviewResult;
        reviewResult = await RunWithTracingAsync("RefactoringDetection.AdversarialReview", job.JobId, async _ =>
        {
            return await AdversarialReviewHelper.ExecuteReviewAsync(
                agentProvider,
                workspacePath,
                new AdversarialReviewPrompts(
                    ConsolidationPromptBuilder.BuildRefactoringReviewPrompt(),
                    ConsolidationPromptBuilder.BuildRefactoringRefinementPrompt(),
                    AgentWorkspacePaths.RefactoringReviewFilePath),
                new AdversarialReviewConfig
                {
                    Enabled = job.PipelineConfiguration.RefactoringReviewEnabled,
                    AgentTimeout = job.PipelineConfiguration.AgentTimeout
                },
                onOutputLine,
                Logger,
                ct);
        });

        if (reviewResult.RefinementTriggered)
        {
            var refinedProposals = await ParseProposalsAsync(proposalsFilePath, ct);
            if (refinedProposals is null)
            {
                Logger.Warning("Refined proposals file is malformed in run {RunId}, keeping original proposals", job.JobId);
            }
            else
            {
                proposals = refinedProposals;
                Logger.Information("Using refined proposals ({Count} proposals) in run {RunId}",
                    proposals.Count, job.JobId);
            }
        }

        // Deterministic checks the prompts cannot guarantee (paths, scope, category, duplicates)
        var (validProposals, rejectedProposals) = RefactoringProposalValidator.Validate(proposals, workspacePath, baseline.ExistingIssueTitles);
        foreach (var rejection in rejectedProposals)
        {
            Logger.Warning("Dropped refactoring proposal '{Title}' in run {RunId}: {Reason}",
                rejection.Proposal.Title, job.JobId, rejection.Reason);
        }

        // Create GitHub issues (capped at MaxRefactoringProposals)
        var (createdIssues, firstFailureHint) = await RunWithTracingAsync("RefactoringDetection.CreateIssues", job.JobId, async activity =>
        {
            activity?.SetTag("pipeline.proposal_count", proposals.Count);
            activity?.SetTag("pipeline.rejected_proposal_count", rejectedProposals.Count);
            return await CreateIssuesAsync(
                validProposals, issueProvider, job.PipelineConfiguration.MaxRefactoringProposals, job.AutoDispatch, baseline.CommitSha, ct);
        });

        var attemptedCount = Math.Min(validProposals.Count, job.PipelineConfiguration.MaxRefactoringProposals);
        var summary = FormatRefactoringSummary(createdIssues, attemptedCount, firstFailureHint, rejectedProposals.Count);
        Logger.Information("{ExecutorName} run {RunId} completed: {Summary}", ExecutorName, job.JobId, summary);

        var allFailed = createdIssues.Count == 0 && attemptedCount > 0;
        return new ConsolidationJobResult
        {
            JobId = job.JobId,
            Success = !allFailed,
            Summary = summary,
            // TODO [WARNING]: ErrorMessage is non-null for both the all-failed (0/N) and partial-success (k/N, k > 0) cases.
            // This means Success = true && ErrorMessage != null is a valid intentional state for partial runs.
            // Downstream consumers (e.g. HubConsolidationOperations, UI) that treat ErrorMessage != null as a failure
            // indicator will misclassify partial-success runs. Review all consumers before treating non-null ErrorMessage
            // as equivalent to failure — branch on Success, not ErrorMessage nullability.
            ErrorMessage = firstFailureHint,
            CreatedIssues = createdIssues,
            ReviewTokenUsage = reviewResult.ReviewTokenUsage,
            RefinementTokenUsage = reviewResult.RefinementTokenUsage
        };
    }

    /// <summary>
    /// Runs git log to identify frequently-changed files and writes a hotspot summary.
    /// Gracefully degrades on any failure — logs a warning and continues without the file.
    /// </summary>
    private async Task WriteHotspotAnalysisAsync(string workspacePath, TimeSpan lookback, HashSet<string> scanIssueReferences, CancellationToken ct)
    {
        try
        {
            var sinceDate = DateTime.UtcNow.Subtract(lookback).ToString("yyyy-MM-dd");
            var output = await RunGitCommandAsync(workspacePath, $"log --name-only --since=\"{sinceDate}\" --format=\"COMMIT_DATE:%ai%nCOMMIT_SUBJECT:%s\"", ct);

            var excludedCount = 0;
            // TODO [WARNING]: excludedCount is a mutable local captured by the lambda. This is safe in the
            // current single-threaded call path (ParseHotspotOutput is synchronous and called once), but
            // would require synchronisation if the predicate were ever invoked concurrently or cached.
            Func<string, bool> isExcludedCommit = subject =>
            {
                foreach (var reference in scanIssueReferences)
                {
                    if (subject.Contains("(" + reference + ")", StringComparison.Ordinal))
                    {
                        excludedCount++;
                        return true;
                    }
                }
                return false;
            };

            // Files deleted since are not refactoring targets any more
            var hotspots = ParseHotspotOutput(output, lookback, fileExists: f => File.Exists(Path.Combine(workspacePath, f)),
                isExcludedCommit: scanIssueReferences.Count > 0 ? isExcludedCommit : null);
            if (hotspots is null)
            {
                Logger.Information("No git history found within lookback window for hotspot analysis");
                return;
            }

            var outputPath = Path.Combine(workspacePath, AgentWorkspacePaths.HotspotAnalysisFilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, hotspots, ct);

            Logger.Information("Hotspot analysis left out {ExcludedCommitCount} commits of refactoring scan issues", excludedCount);
            Logger.Information("Wrote hotspot analysis to {Path}", outputPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "Hotspot analysis failed, continuing without it");
        }
    }

    /// <summary>
    /// Files whose churn says nothing about code health: pipeline scratch space, documentation and
    /// lock files. They are left out of the hotspot list.
    /// </summary>
    private static readonly string[] HotspotExcludedPrefixes =
        [AgentWorkspacePaths.MetadataDirectory + "/", AgentWorkspacePaths.BrainDirectory + "/"];

    private static readonly string[] HotspotExcludedFileNames =
        ["package-lock.json", "packages.lock.json", "yarn.lock", "pnpm-lock.yaml"];

    internal static bool IsHotspotCandidate(string file) =>
        !HotspotExcludedPrefixes.Any(p => file.StartsWith(p, StringComparison.OrdinalIgnoreCase))
        && !file.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        && !file.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)
        && !HotspotExcludedFileNames.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parses git log output with COMMIT_DATE: markers into a time-decay-weighted hotspot summary.
    /// Each change is weighted by recency: factor = 1/(1 + days_since/30).
    /// Leaves out files that fail <see cref="IsHotspotCandidate"/> and, when <paramref name="fileExists"/>
    /// is given, files that no longer exist.
    /// When <paramref name="isExcludedCommit"/> is given, skips all file lines for any commit whose subject
    /// the predicate returns true for. A <c>COMMIT_SUBJECT:</c> line is never counted as a file entry,
    /// even when the predicate is null.
    /// Returns null if no files found. Exposed as internal static for testability.
    /// </summary>
    internal static string? ParseHotspotOutput(
        string gitLogOutput, TimeSpan lookback, DateTime? referenceTime = null, Func<string, bool>? fileExists = null,
        Func<string, bool>? isExcludedCommit = null)
    {
        var now = referenceTime ?? DateTime.UtcNow;
        var entries = new List<(string File, double RecencyFactor, double DaysSince)>();
        // Note: files appearing before any COMMIT_DATE: marker receive max recency weight (1.0).
        // In practice, git --format always emits the date line before file names per commit.
        DateTime currentCommitDate = now;
        bool dateParseFailedForCurrentCommit = false;
        bool skipCurrentCommit = false;

        foreach (var line in gitLogOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            if (trimmed.StartsWith("COMMIT_DATE:"))
            {
                var dateStr = trimmed[12..];
                if (DateTimeOffset.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                {
                    currentCommitDate = parsed.UtcDateTime;
                    dateParseFailedForCurrentCommit = false;
                }
                else
                {
                    // Graceful degradation: neutral weight (0.5 → equivalent to 30 days old)
                    dateParseFailedForCurrentCommit = true;
                }
                skipCurrentCommit = false;
                continue;
            }

            if (trimmed.StartsWith("COMMIT_SUBJECT:"))
            {
                // TODO [WARNING]: skipCurrentCommit is set here assuming COMMIT_DATE: always precedes
                // COMMIT_SUBJECT: per commit (which git --format guarantees). If the output ever starts
                // with a COMMIT_SUBJECT: before any COMMIT_DATE: (e.g. corrupted or unexpected format),
                // file lines that follow would be incorrectly skipped when the predicate returns true.
                // This matches the pre-existing COMMIT_DATE-first assumption documented above.
                var subject = trimmed[15..];
                skipCurrentCommit = isExcludedCommit?.Invoke(subject) == true;
                continue;
            }

            if (skipCurrentCommit)
                continue;

            double daysSince;
            double recencyFactor;

            if (dateParseFailedForCurrentCommit)
            {
                daysSince = 30.0;
                recencyFactor = 0.5;
            }
            else
            {
                daysSince = Math.Max(0, (now - currentCommitDate).TotalDays);
                recencyFactor = 1.0 / (1.0 + daysSince / 30.0);
            }

            entries.Add((trimmed, recencyFactor, daysSince));
        }

        if (entries.Count == 0)
            return null;

        var hotspots = entries
            .Where(e => IsHotspotCandidate(e.File))
            .GroupBy(e => e.File)
            .Where(g => fileExists is null || fileExists(g.Key))
            .Select(g => (
                File: g.Key,
                Score: g.Sum(e => e.RecencyFactor),
                Count: g.Count(),
                AvgDaysAgo: (int)g.Average(e => e.DaysSince)))
            .OrderByDescending(x => x.Score)
            .Take(30)
            .ToList();

        if (hotspots.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine($"# Git Hotspot Analysis (last {lookback.Days} days, time-decay weighted)");
        sb.AppendLine("# Score = sum of recency factors (1/(1+days/30) per change — recent changes weighted higher)");
        sb.AppendLine();
        foreach (var (file, score, count, avgDaysAgo) in hotspots)
            sb.AppendLine($"{score.ToString("F1", CultureInfo.InvariantCulture)} — {file} ({count} changes, avg {avgDaysAgo} days ago)");

        return sb.ToString();
    }

    internal static Task<string> RunGitCommandAsync(string workingDirectory, string arguments, CancellationToken ct)
        => GitProcessRunner.RunAsync(workingDirectory, arguments, ct, throwOnNonZeroExit: true);

    /// <summary>
    /// Parses the refactoring proposals JSON file from the workspace.
    /// Returns null if the file doesn't exist or contains malformed JSON.
    /// Returns an empty list if the file contains an empty array.
    /// </summary>
    private async Task<IReadOnlyList<RefactoringProposal>?> ParseProposalsAsync(
        string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            Logger.Information("No refactoring proposals file found at {Path}, treating as no proposals", filePath);
            return Array.Empty<RefactoringProposal>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            var proposals = JsonSerializer.Deserialize<List<RefactoringProposal>>(json, PipelineJsonOptions.Lenient);
            return (IReadOnlyList<RefactoringProposal>?)proposals ?? Array.Empty<RefactoringProposal>();
        }
        catch (JsonException ex)
        {
            Logger.Warning(ex, "Malformed JSON in refactoring proposals file at {Path}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Classifies an issue-creation exception into a human-readable hint string.
    /// For <see cref="HttpRequestException"/> with a known status code, returns a specific
    /// message that distinguishes 401 (token) from 403 (permissions).
    /// For all other exceptions, falls back to "{TypeName}: {Message}".
    /// </summary>
    // TODO [WARNING]: The fallback branch uses ex.GetType().Name + ex.Message for non-HTTP exceptions.
    // ex.Message may contain sensitive details (connection strings, file paths, internal hostnames) from
    // lower-level exceptions (SocketException, IOException, etc.) that flow directly into ErrorMessage
    // (persisted to the DB) and the run Summary (displayed in the UI). Consider sanitizing or truncating
    // the fallback message to avoid leaking environment-specific details into run history.
    private static string ClassifyIssueCreationException(Exception ex) =>
        ex is HttpRequestException { StatusCode: { } code }
            ? code switch
            {
                HttpStatusCode.Unauthorized => "401 Unauthorized — token expired or revoked",
                HttpStatusCode.Forbidden => "403 Forbidden — app missing 'issues: write' permission",
                HttpStatusCode.UnprocessableEntity => "422 Unprocessable — invalid issue content",
                HttpStatusCode.TooManyRequests => "429 Too Many Requests — GitHub rate limit hit",
                _ => $"HTTP {(int)code}"
            }
            : $"{ex.GetType().Name}: {ex.Message}";

    /// <summary>
    /// Creates GitHub issues for each proposal, capped at <paramref name="maxProposals"/>.
    /// Proposals are processed sequentially. For each successful creation the proposal title is
    /// registered with a <see cref="DependencyResolver"/> so that later proposals whose
    /// <see cref="RefactoringProposal.DependsOn"/> lists reference it receive a resolved
    /// "Depends on #N" line prepended to their issue body.
    /// Individual issue creation failures are logged but do not stop processing.
    /// The first failure hint (classified HTTP status or exception type) is captured and returned
    /// alongside the list of successfully created issues.
    /// </summary>
    /// <remarks>
    /// TODO: If a proposal's issue creation fails (exception swallowed by the catch block), its
    /// title is never registered with the resolver. Any later proposal that lists the failed title
    /// in its DependsOn will silently receive no dependency line — the same behaviour as an
    /// unresolvable title. Document this caveat at the call site or in tests if the contract needs
    /// to be visible to future maintainers.
    /// </remarks>
    private async Task<(IReadOnlyList<CreatedIssueInfo> Created, string? FirstFailureHint)> CreateIssuesAsync(
        IReadOnlyList<RefactoringProposal> proposals,
        IIssueProvider issueProvider,
        int maxProposals,
        bool autoDispatch,
        string? commitSha,
        CancellationToken ct)
    {
        var createdIssues = new List<CreatedIssueInfo>();
        string? firstFailureHint = null;
        var proposalsToProcess = TopologicalSortProposals(proposals.Take(maxProposals).ToList());
        var labels = autoDispatch
            ? new[] { AgentLabels.Generated, AgentLabels.Next }
            : new[] { AgentLabels.Generated };

        var resolver = new DependencyResolver();

        foreach (var proposal in proposalsToProcess)
        {
            try
            {
                // Build the full body first, then resolve and prepend any dependency lines.
                // Register() uses proposal.Title (raw, not sanitized) so that DependsOn references
                // from other proposals — which also use the raw agent-generated title — can resolve.
                var body = FormatIssueBody(proposal, commitSha);
                var dependencyLines = resolver.Resolve(proposal.DependsOn ?? [], SingleTrackerProviderId, Logger);
                if (dependencyLines.Count > 0)
                {
                    var depSection = string.Join("\n", dependencyLines);
                    body = $"{depSection}\n\n{body}";
                }

                var sanitizedTitle = SanitizeTitle(proposal.Title);
                var result = await issueProvider.CreateIssueAsync(
                    sanitizedTitle,
                    body,
                    labels,
                    ct);

                // DependsOn entries use the raw titles from the same JSON array; the sanitized
                // title (the one the tracker shows) is registered too (issue #1450).
                resolver.Register(proposal.Title, result.Identifier, result.Url, SingleTrackerProviderId);
                if (!string.Equals(proposal.Title.Trim(), sanitizedTitle, StringComparison.OrdinalIgnoreCase))
                    resolver.Register(sanitizedTitle, result.Identifier, result.Url, SingleTrackerProviderId);

                // TODO: resolver.Register is placed immediately after the awaited CreateIssueAsync
                // and before createdIssues.Add/logging. If the try block grows with additional
                // awaitable calls between Register and the catch, a failure there would leave the
                // resolver holding a registered title for an issue whose entry was never added to
                // createdIssues. This is harmless today but is a latent structural fragility — keep
                // Register as the last substantive statement before createdIssues.Add, or move it
                // inside a finally-guarded section if the block expands.

                createdIssues.Add(new CreatedIssueInfo
                {
                    Identifier = result.Identifier,
                    Title = proposal.Title,
                    Url = result.Url
                });

                Logger.Information("Created refactoring issue {Identifier}: {Title}",
                    result.Identifier, proposal.Title);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                firstFailureHint ??= ClassifyIssueCreationException(ex);
                Logger.Warning(ex, "Failed to create issue for proposal '{Title}', continuing with remaining",
                    proposal.Title);
            }
        }

        return (createdIssues, firstFailureHint);
    }

    /// <summary>
    /// Formats the issue body from a refactoring proposal: the problem (<see cref="RefactoringProposal.Rationale"/>),
    /// the change (<see cref="RefactoringProposal.Description"/>), the files and scope, the evidence, and the
    /// acceptance criteria. <paramref name="commitSha"/> names the commit the analysis ran against, which the
    /// evidence line numbers refer to.
    /// </summary>
    internal static string FormatIssueBody(RefactoringProposal proposal, string? commitSha = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Problem");
        sb.AppendLine();
        sb.AppendLine(SanitizeMarkdown(proposal.Rationale));
        sb.AppendLine();

        AppendMetadataLine(sb, proposal);

        sb.AppendLine("## Suggested Approach");
        sb.AppendLine();
        sb.AppendLine(SanitizeMarkdown(proposal.Description));
        sb.AppendLine();

        if (proposal.Prerequisites is { Count: > 0 })
        {
            sb.AppendLine("## Prerequisites");
            sb.AppendLine();
            foreach (var prereq in proposal.Prerequisites.Where(p => p is not null))
                sb.AppendLine($"- {SanitizePrerequisite(prereq)}");
            sb.AppendLine();
        }

        sb.AppendLine("## Affected Components");
        sb.AppendLine();
        foreach (var file in proposal.AffectedFiles.Where(f => f is not null))
            sb.AppendLine($"- {CodeSpan(file)}");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(proposal.ScopeQuery))
        {
            sb.AppendLine("## Scope");
            sb.AppendLine();
            sb.AppendLine("Every match of this search is in scope, unless the suggested approach excludes it:");
            sb.AppendLine();
            AppendCodeBlock(sb, proposal.ScopeQuery, "sh");
            sb.AppendLine();
        }

        AppendEvidenceSection(sb, proposal);

        sb.AppendLine("## Acceptance Criteria");
        sb.AppendLine();

        AppendAcceptanceCriteria(sb, proposal);

        sb.AppendLine();
        sb.AppendLine("---");
        sb.Append('*').Append(GeneratedIssueFooter);
        sb.Append(commitSha is null
            ? ".*"
            : $" at commit `{commitSha[..Math.Min(12, commitSha.Length)]}`. Line numbers refer to that commit.*");

        return sb.ToString();
    }

    private static void AppendMetadataLine(StringBuilder sb, RefactoringProposal proposal)
    {
        var parts = new List<string>();
        if (proposal.Category is not null)
            parts.Add($"**Category:** {SanitizeMarkdown(proposal.Category)}");
        if (proposal.EstimatedEffort is not null)
            parts.Add($"**Effort:** {SanitizeMarkdown(proposal.EstimatedEffort)}");
        if (proposal.RiskLevel is not null)
            parts.Add($"**Risk:** {SanitizeMarkdown(proposal.RiskLevel)}");
        if (proposal.Technique is not null)
            parts.Add($"**Technique:** {SanitizeMarkdown(proposal.Technique)}");

        if (parts.Count == 0)
            return;

        sb.AppendLine(string.Join(" | ", parts));
        sb.AppendLine();
    }

    private static void AppendEvidenceSection(StringBuilder sb, RefactoringProposal proposal)
    {
        var hasEvidence = !string.IsNullOrWhiteSpace(proposal.Evidence);
        var sources = proposal.EvidenceSources?.Where(s => s is not null).ToList() ?? [];
        if (!hasEvidence && sources.Count == 0)
            return;

        sb.AppendLine("## Evidence");
        sb.AppendLine();
        if (hasEvidence)
        {
            AppendCodeBlock(sb, proposal.Evidence!, language: null);
            sb.AppendLine();
        }

        foreach (var source in sources)
            sb.AppendLine($"- {CodeSpan(source)}");
        if (sources.Count > 0)
            sb.AppendLine();
    }

    private static void AppendAcceptanceCriteria(StringBuilder sb, RefactoringProposal proposal)
    {
        var criteria = proposal.AcceptanceCriteria;
        if (criteria is { Count: > 0 })
        {
            foreach (var criterion in criteria.Where(c => c is not null))
                sb.AppendLine($"- [ ] {SanitizeMarkdown(criterion)}");
        }
        else if (string.Equals(proposal.Category?.Trim(), RefactoringCategories.Bug, StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine("- [ ] A test reproduces the failure described under Problem and passes after the fix");
            sb.AppendLine("- [ ] Behavior outside the described failure is unchanged");
        }
        else
        {
            sb.AppendLine("- [ ] Refactoring applied without changing observable behavior");
            sb.AppendLine("- [ ] All existing tests continue to pass");
        }
    }

    /// <summary>
    /// Renders <paramref name="text"/> as an inline code span. Markdown and HTML are not interpreted inside
    /// a code span, so only backticks, which would end it early, need replacing.
    /// </summary>
    private static string CodeSpan(string text) => $"`{text.Replace('`', '\'')}`";

    /// <summary>
    /// Renders <paramref name="text"/> verbatim in a fenced code block. The fence is longer than any backtick
    /// run in the text, so the text cannot close it; markdown, HTML and mentions are not interpreted inside.
    /// </summary>
    private static void AppendCodeBlock(StringBuilder sb, string text, string? language)
    {
        var longestBacktickRun = 0;
        var currentRun = 0;
        foreach (var c in text)
        {
            currentRun = c == '`' ? currentRun + 1 : 0;
            longestBacktickRun = Math.Max(longestBacktickRun, currentRun);
        }

        var fence = new string('`', Math.Max(3, longestBacktickRun + 1));
        sb.AppendLine(fence + language);
        sb.AppendLine(text.TrimEnd());
        sb.AppendLine(fence);
    }

    /// <summary>
    /// Sanitizes the proposal title for use in GitHub issue titles.
    /// Truncates to 200 chars and strips newlines.
    /// </summary>
    internal static string SanitizeTitle(string title) => TextSanitizer.SanitizeTitle(title);

    /// <summary>
    /// Orders proposals so that a proposal comes after the proposals it lists in
    /// <see cref="RefactoringProposal.DependsOn"/>, keeping the original order otherwise.
    /// Issues are created in this order, so a dependency's issue number is known when its
    /// dependent's "Depends on #N" line is resolved. Falls back to the original order on a cycle.
    /// Kahn's algorithm; the batch is capped at a handful of proposals (issue #1450).
    /// </summary>
    internal static IReadOnlyList<RefactoringProposal> TopologicalSortProposals(IReadOnlyList<RefactoringProposal> proposals)
    {
        if (proposals.Count <= 1)
            return proposals;

        var (inDegree, dependents) = BuildDependencyGraph(proposals);

        var ready = new Queue<int>(Enumerable.Range(0, proposals.Count).Where(i => inDegree[i] == 0));
        var sorted = new List<RefactoringProposal>(proposals.Count);
        while (ready.Count > 0)
        {
            var index = ready.Dequeue();
            sorted.Add(proposals[index]);
            foreach (var dependent in dependents[index])
            {
                if (--inDegree[dependent] == 0)
                    ready.Enqueue(dependent);
            }
        }

        return sorted.Count == proposals.Count ? sorted : proposals;
    }

    /// <summary>
    /// Counts, for each proposal, how many proposals in the batch it depends on, and lists the proposals
    /// that depend on it. Blank titles, titles outside the batch and self-references are ignored.
    /// </summary>
    private static (int[] InDegree, List<int>[] Dependents) BuildDependencyGraph(IReadOnlyList<RefactoringProposal> proposals)
    {
        // Title lookup is trimmed and case-insensitive, like DependencyResolver.
        var titleToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < proposals.Count; i++)
            titleToIndex.TryAdd(proposals[i].Title.Trim(), i);

        var inDegree = new int[proposals.Count];
        var dependents = new List<int>[proposals.Count];
        for (var i = 0; i < proposals.Count; i++)
            dependents[i] = [];

        for (var i = 0; i < proposals.Count; i++)
        {
            foreach (var dependency in proposals[i].DependsOn ?? [])
            {
                if (!string.IsNullOrWhiteSpace(dependency)
                    && titleToIndex.TryGetValue(dependency.Trim(), out var dependencyIndex)
                    && dependencyIndex != i)
                {
                    inDegree[i]++;
                    dependents[dependencyIndex].Add(i);
                }
            }
        }

        return (inDegree, dependents);
    }

    /// <summary>
    /// Escapes markdown-sensitive characters to prevent injection in GitHub issues.
    /// Delegates to <see cref="TextSanitizer.SanitizeMarkdown"/>.
    /// </summary>
    private static string SanitizeMarkdown(string value) => TextSanitizer.SanitizeMarkdown(value);

    /// <summary>
    /// Sanitizes a prerequisite like <see cref="SanitizeMarkdown"/> and wraps bare <c>#N</c> in a
    /// code span. Agents write "proposal #1", and GitHub would link that to unrelated issue 1.
    /// The lookbehind leaves "C# 12" and "F#8" alone (issue #1450).
    /// </summary>
    private static string SanitizePrerequisite(string value) =>
        HashNumberPattern().Replace(SanitizeMarkdown(value), "`#$1`");

    [GeneratedRegex(@"(?<![A-Za-z])#(\d+)")]
    private static partial Regex HashNumberPattern();

    /// <summary>
    /// Formats the refactoring run summary with issue count and identifiers.
    /// Distinguishes between "no proposals found" and "proposals found but issue creation failed".
    /// When <paramref name="firstFailureHint"/> is provided, it is included in the summary for
    /// both the all-failed (0/N) and partial-failure (k/N) cases. <paramref name="droppedCount"/> counts the
    /// proposals that failed validation; they are not part of <paramref name="proposalCount"/>.
    /// </summary>
    internal static string FormatRefactoringSummary(
        IReadOnlyList<CreatedIssueInfo> createdIssues, int proposalCount = 0, string? firstFailureHint = null, int droppedCount = 0)
    {
        var summary = FormatIssueCreationSummary(createdIssues, proposalCount, firstFailureHint);
        return droppedCount > 0
            ? $"{summary} ({droppedCount} proposal(s) dropped by validation)"
            : summary;
    }

    private static string FormatIssueCreationSummary(IReadOnlyList<CreatedIssueInfo> createdIssues, int proposalCount, string? firstFailureHint)
    {
        if (createdIssues.Count == 0 && proposalCount == 0)
            return "No refactoring opportunities identified";

        if (createdIssues.Count == 0 && proposalCount > 0)
        {
            var hint = firstFailureHint ?? "check logs for details";
            return $"Found {proposalCount} proposal(s) but failed to create issues ({hint})";
        }

        var identifiers = string.Join(", ", createdIssues.Select(i => $"#{i.Identifier}"));
        if (createdIssues.Count < proposalCount)
        {
            var partialHint = firstFailureHint is not null ? $" — first failure: {firstFailureHint}" : "";
            return $"Created {createdIssues.Count}/{proposalCount} refactoring issue(s): {identifiers} ({proposalCount - createdIssues.Count} failed{partialHint})";
        }

        return $"Created {createdIssues.Count} refactoring issue(s): {identifiers}";
    }
}
