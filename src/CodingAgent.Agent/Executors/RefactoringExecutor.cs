using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
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
public sealed class RefactoringExecutor : ConsolidationExecutorBase
{
    protected override string ExecutorName => "Refactoring detection";

    private readonly string? _claudeRulesDirectory;
    private readonly string? _openCodeDirectory;

    public RefactoringExecutor(
        Serilog.ILogger logger,
        ConsolidationProgress? progress = null,
        string? claudeRulesDirectory = null,
        string? openCodeDirectory = null) : base(logger, progress)
    {
        _claudeRulesDirectory = claudeRulesDirectory;
        _openCodeDirectory = openCodeDirectory;
    }

    /// <summary>
    /// Executes the phased refactoring detection workflow:
    /// 1. Clone code repo + brain into the run's workspace
    /// 2. Hotspot analysis (git log)
    /// 3. Phase 0: Context extraction (project conventions)
    /// 4. Phase 1: Parallel focused detection (4 sub-agents: structural, correctness, design, test gaps)
    /// 5. Phase 2: Aggregation + prioritization → produces proposals JSON
    /// 6. If no proposals: return success
    /// 7. Adversarial review (if enabled)
    /// 8. Validate proposals (<see cref="RefactoringProposalValidator"/>), create GitHub issues (capped at MaxRefactoringProposals)
    /// 9. Return summary
    /// </summary>

    /// <summary>
    /// Returns the length of the root object's <c>findings</c> array.
    /// Returns null when the file does not exist, is not valid JSON, or has no <c>findings</c> array.
    /// Never throws for those cases.
    /// </summary>
    internal static int? CountFindings(string findingsFilePath)
    {
        if (!File.Exists(findingsFilePath))
            return null;

        try
        {
            var json = File.ReadAllText(findingsFilePath);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("findings", out var findings)
                && findings.ValueKind == JsonValueKind.Array)
                return findings.GetArrayLength();
            return null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

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
            TransitionTo(PipelineStep.CloningRepository);
            Directory.CreateDirectory(workspacePath);
            Logger.Information("Cloning code repo for refactoring detection run {RunId} into {Workspace}", job.JobId, workspacePath);

            await RunWithTracingAsync("RefactoringDetection.Clone", job.JobId, async _ =>
            {
                await repoProvider.CloneAsync(workspacePath, ct);
                if (brainProvider is not null)
                {
                    await TryCloneBrainRepoAsync(brainProvider, workspacePath, job.JobId, ct);
                    // Inject brain skills if the clone succeeded (spec 054, req 5.7)
                    var brainPath = Path.Combine(workspacePath, AgentWorkspacePaths.BrainDirectory);
                    if (Directory.Exists(brainPath))
                    {
                        var injection = await BrainSkillsReader.ReadAsync(
                            brainPath,
                            repoProvider.RepositoryFullName.Split('/')[^1],
                            job.StackLabels,
                            brainProvider,
                            Logger,
                            ct);
                        BrainSkillsWriter.Write(agentProvider.ProviderType, workspacePath, injection.Text,
                            _claudeRulesDirectory, _openCodeDirectory);
                    }
                }
            });

            // 2b. Query open issues for deduplication context (before hotspot so refs can be passed to hotspot)
            var (issueContext, openIssueTitles, openIssues) = await TryBuildIssueContextAsync(issueProvider, job.JobId, ct);

            // 2c-pre. Collect scan issue references for hotspot exclusion
            var scanIssueRefs = await CollectScanIssueReferencesAsync(
                issueProvider, openIssues, job.PipelineConfiguration.HotspotAnalysisLookback, job.JobId, ct);

            // 2. Hotspot analysis (now knows scan issue refs to exclude)
            TransitionTo(PipelineStep.AnalyzingHotspots);
            await RunWithTracingAsync("RefactoringDetection.HotspotAnalysis", job.JobId, async _ =>
            {
                await WriteHotspotAnalysisAsync(workspacePath, job.PipelineConfiguration.HotspotAnalysisLookback, scanIssueRefs, ct);
            });

            // 2c. Query past proposal outcomes for feedback context
            TransitionTo(PipelineStep.ReadingIssueContext);
            var (outcomeContext, closedIssueTitles) = await TryBuildOutcomeContextAsync(issueProvider, job.PipelineConfiguration, job.JobId, ct);

            // 2d. Write both for the review step, which checks overlap too
            await TryWriteIssueContextFileAsync(workspacePath, issueContext, outcomeContext, job.JobId, ct);

            var commitSha = await TryGetHeadCommitAsync(workspacePath, ct);

            // Build base scan outcome from job settings and commit SHA
            var scanOutcome = new RefactoringScanOutcome
            {
                MaxIssues = job.PipelineConfiguration.MaxRefactoringProposals,
                HotspotLookbackDays = (int)job.PipelineConfiguration.HotspotAnalysisLookback.TotalDays,
                ReviewEnabled = job.PipelineConfiguration.RefactoringReviewEnabled,
                AutoDispatch = job.AutoDispatch,
                AnalyzedCommitSha = commitSha
            };

            // ── Phased Refactoring Detection ──
            var (phasedResult, detectors) = await ExecutePhasedRefactoringAsync(job, agentProvider, workspacePath, issueContext, outcomeContext, onOutputLine, ct);
            scanOutcome = scanOutcome with { Detectors = detectors };
            if (!phasedResult.Success)
            {
                phasedResult.ScanOutcome = scanOutcome;
                return phasedResult;
            }

            return await FinalizeProposalsAsync(
                job, agentProvider, issueProvider, workspacePath,
                new ScanBaseline([.. openIssueTitles, .. closedIssueTitles], commitSha), scanOutcome, onOutputLine, ct);
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

    /// <summary>The run's failure reason when the aggregation step wrote no proposals file, not even <c>[]</c>.</summary>
    internal const string MissingProposalsFileMessage = "The aggregation step wrote no refactoring-proposals.json";

    /// <summary>The number of open issues read for duplicate checks: the providers' maximum page size.</summary>
    private const int OpenIssueContextLimit = 100;

    /// <summary>The number of past scan issues whose outcome and implementer feedback go into the prompt.</summary>
    private const int OutcomeContextLimit = 20;

    internal static bool IsScanIssue(IssueSummary issue) => RefactoringScanIssues.IsScanIssue(issue);

    /// <summary>
    /// Builds the open-issue section of the aggregation prompt from every open issue, whatever its labels
    /// or age: a proposal must not duplicate any of them. Also returns their titles (for deduplication) and
    /// the full list (for scan-issue reference collection, so <see cref="ListOpenIssuesAsync"/> is called only once).
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
    /// Collects the set of issue references (e.g. <c>#3284</c>) for all scan issues — open and recently
    /// closed — so the hotspot analysis can skip commits that implement them.
    /// <para>
    /// Open scan issues are taken from <paramref name="openIssues"/> (already fetched by
    /// <see cref="TryBuildIssueContextAsync"/>) to avoid a second <c>ListOpenIssuesAsync</c> call.
    /// Closed scan issues are fetched in pages (up to 10) with the <c>agent:generated</c> label
    /// and filtered by <see cref="IsScanIssue"/>.
    /// </para>
    /// <para>
    /// Any exception other than <see cref="OperationCanceledException"/> is logged as a warning and
    /// the references collected so far are returned so the hotspot analysis can still run.
    /// </para>
    /// </summary>
    internal async Task<IReadOnlySet<string>> CollectScanIssueReferencesAsync(
        IIssueProvider issueProvider,
        IReadOnlyList<IssueSummary> openIssues,
        TimeSpan hotspotLookback,
        string jobId,
        CancellationToken ct)
    {
        var refs = new HashSet<string>(StringComparer.Ordinal);

        // NOTE (issue #3540): FormatIssueReference is called here outside the try block that guards the
        // closed-issue loop below, creating an asymmetric exception safety profile. If FormatIssueReference
        // throws (it is an interface method with no documented exception contract), the exception propagates
        // uncaught to ExecuteAsync. Consider moving this loop inside the try block, or wrapping it in its
        // own try/catch with a warning log, so exception handling is consistent for both open and closed issues.
        // Open scan issues (already fetched — do not call ListOpenIssuesAsync again)
        foreach (var issue in openIssues.Where(IsScanIssue))
            refs.Add(issueProvider.FormatIssueReference(issue.Identifier));

        // Closed scan issues within the hotspot window
        try
        {
            var since = DateTime.UtcNow - hotspotLookback;
            const int maxPages = 10;
            for (var page = 1; page <= maxPages; page++)
            {
                var result = await issueProvider.ListClosedIssuesAsync(
                    page, 100, new[] { AgentLabels.Generated }, since: since, ct);
                foreach (var issue in result.Items.Where(IsScanIssue))
                    refs.Add(issueProvider.FormatIssueReference(issue.Identifier));
                if (!result.HasMore)
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "Failed to query closed scan issues for hotspot exclusion in run {RunId}, using partial set", jobId);
        }

        return refs;
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
    /// Phase 0 → Phase 1 (1–4 parallel agents, depending on focus) → Phase 2 (aggregation) → finalize.
    /// </summary>
    private async Task<(ConsolidationJobResult Result, IReadOnlyList<RefactoringDetectorOutcome> Detectors)> ExecutePhasedRefactoringAsync(
        ConsolidationJobMessage job,
        IAgentProvider agentProvider,
        string workspacePath,
        string? issueContext,
        string outcomeContext,
        Action<string>? onOutputLine,
        CancellationToken ct)
    {
        ConsolidationJobResult? failure;

        // Phase 0: Context Extraction
        TransitionTo(PipelineStep.ExtractingConventions);
        Logger.Information("Phase 0: Extracting project conventions for run {RunId}", job.JobId); // NOSONAR S6664 — one progress log per scan phase

        (_, failure) = await RunWithTracingAsync("RefactoringDetection.Phase0.ContextExtraction", job.JobId, async _ =>
        {
            return await ExecuteAgentAndCheckAsync(
                agentProvider,
                new AgentRequest
                {
                    Prompt = ConsolidationPromptBuilder.BuildRefactoringContextExtractionPrompt(
                        includeCoverage: job.RefactoringFocus is RefactoringScanFocus.All or RefactoringScanFocus.Tests),
                    WorkspacePath = workspacePath,
                    Timeout = job.PipelineConfiguration.AgentTimeout
                },
                job.JobId,
                ct,
                onOutputLine,
                "refactoring_conventions");
        });

        if (failure is not null) return (failure, []);

        // Phase 1: Parallel Focused Detection — run only the agents of the focus.
        TransitionTo(PipelineStep.DetectingFindings);
        var agents = new (string SpanName, Func<string> BuildPrompt, RefactoringScanFocus Focus, string? OutputPrefix, string? UsagePhase)[]
        {
            ("RefactoringDetection.Phase1.StructuralDebt", ConsolidationPromptBuilder.BuildRefactoringStructuralDebtPrompt, RefactoringScanFocus.Structure, "[A] ", "refactoring_structural"),
            ("RefactoringDetection.Phase1.Correctness", ConsolidationPromptBuilder.BuildRefactoringCorrectnessPrompt, RefactoringScanFocus.Correctness, "[B] ", "refactoring_correctness"),
            ("RefactoringDetection.Phase1.DesignConsistency", ConsolidationPromptBuilder.BuildRefactoringDesignConsistencyPrompt, RefactoringScanFocus.Design, "[C] ", "refactoring_design"),
            ("RefactoringDetection.Phase1.TestGaps", ConsolidationPromptBuilder.BuildRefactoringTestGapPrompt, RefactoringScanFocus.Tests, null, "refactoring_test_gaps"),
        };

        var selected = agents
            .Where(a => job.RefactoringFocus == RefactoringScanFocus.All || a.Focus == job.RefactoringFocus)
            .ToArray();

        Logger.Information("Phase 1: Dispatching {AgentCount} parallel analysis agents for run {RunId} (focus {Focus})",
            selected.Length, job.JobId, job.RefactoringFocus);

        var phase1Tasks = selected.Select(a =>
            RunWithTracingAsync(a.SpanName, job.JobId, async _ =>
            {
                var agentOnOutputLine = a.OutputPrefix is null
                    ? onOutputLine
                    : (onOutputLine is null ? null : line => onOutputLine($"{a.OutputPrefix}{line}"));
                return await ExecuteAgentAndCheckAsync(
                    agentProvider,
                    new AgentRequest
                    {
                        Prompt = a.BuildPrompt(),
                        WorkspacePath = workspacePath,
                        Timeout = job.PipelineConfiguration.AgentTimeout
                    },
                    job.JobId,
                    ct,
                    agentOnOutputLine,
                    a.UsagePhase);
            })).ToArray();

        var phase1Results = await Task.WhenAll(phase1Tasks);

        // Build detector outcomes in the order structural, correctness, design
        var detectorDefs = new[]
        {
            (Name: "Structural debt", Focus: RefactoringScanFocus.Structure, FilePath: AgentWorkspacePaths.RefactoringStructuralFindingsFilePath),
            (Name: "Correctness and hygiene", Focus: RefactoringScanFocus.Correctness, FilePath: AgentWorkspacePaths.RefactoringCorrectnessFindingsFilePath),
            (Name: "Design consistency", Focus: RefactoringScanFocus.Design, FilePath: AgentWorkspacePaths.RefactoringDesignFindingsFilePath),
        };
        var detectors = new List<RefactoringDetectorOutcome>(detectorDefs.Length);
        for (var di = 0; di < detectorDefs.Length; di++)
        {
            var dd = detectorDefs[di];
            var selectedIdx = Array.FindIndex(selected, a => a.Focus == dd.Focus);
            var succeeded = selectedIdx >= 0 && phase1Results[selectedIdx].Failure is null;
            detectors.Add(new RefactoringDetectorOutcome
            {
                Name = dd.Name,
                Succeeded = succeeded,
                FindingCount = CountFindings(Path.Combine(workspacePath, dd.FilePath))
            });
        }

        // Check for failures — log but continue if at least one agent succeeded
        var phase1Failures = phase1Results.Where(r => r.Failure is not null).ToList();
        if (phase1Failures.Count == phase1Results.Length)
        {
            return (CreateFailureResult(job.JobId, "All Phase 1 analysis agents failed"), detectors);
        }

        if (phase1Failures.Count > 0)
        {
            Logger.Warning("{FailCount}/{TotalCount} Phase 1 agents failed in run {RunId}, continuing with partial results",
                phase1Failures.Count, phase1Results.Length, job.JobId);
        }

        // Phase 2a: Parallel per-file scoring — same Task.WhenAll pattern as Phase 1
        TransitionTo(PipelineStep.AggregatingProposals);
        var phase2aAgents = new (string SpanName, RefactoringScanFocus Focus, string? OutputPrefix, string UsagePhase)[]
        {
            ("RefactoringDetection.Phase2a.Structural", RefactoringScanFocus.Structure, "[2a-A] ", "refactoring_phase2a_structural"),
            ("RefactoringDetection.Phase2a.Correctness", RefactoringScanFocus.Correctness, "[2a-B] ", "refactoring_phase2a_correctness"),
            ("RefactoringDetection.Phase2a.Design", RefactoringScanFocus.Design, "[2a-C] ", "refactoring_phase2a_design"),
            ("RefactoringDetection.Phase2a.TestGaps", RefactoringScanFocus.Tests, null, "refactoring_phase2a_test_gaps"),
        };

        var selected2a = phase2aAgents
            .Where(a => job.RefactoringFocus == RefactoringScanFocus.All || a.Focus == job.RefactoringFocus)
            .ToArray();

        Logger.Information("Phase 2a: Dispatching {AgentCount} parallel scoring agents for run {RunId} (focus {Focus})",
            selected2a.Length, job.JobId, job.RefactoringFocus);

        var phase2aTasks = selected2a.Select(a =>
            RunWithTracingAsync(a.SpanName, job.JobId, async _ =>
            {
                var agentOnOutputLine = a.OutputPrefix is null
                    ? onOutputLine
                    : (onOutputLine is null ? null : (Action<string>)(line => onOutputLine($"{a.OutputPrefix}{line}")));
                return await ExecuteAgentAndCheckAsync(
                    agentProvider,
                    new AgentRequest
                    {
                        Prompt = ConsolidationPromptBuilder.BuildRefactoringPhase2aPrompt(a.Focus),
                        WorkspacePath = workspacePath,
                        Timeout = job.PipelineConfiguration.AgentTimeout,
                        EffortOverride = AgentEffortLevel.High
                    },
                    job.JobId,
                    ct,
                    agentOnOutputLine,
                    a.UsagePhase);
            })).ToArray();

        var phase2aResults = await Task.WhenAll(phase2aTasks);

        var phase2aFailures = phase2aResults.Where(r => r.Failure is not null).ToList();
        if (phase2aFailures.Count == phase2aResults.Length)
        {
            return (CreateFailureResult(job.JobId, "All Phase 2a scoring agents failed"), detectors);
        }

        if (phase2aFailures.Count > 0)
        {
            Logger.Warning("{FailCount}/{TotalCount} Phase 2a scoring agents failed in run {RunId}, continuing with partial results",
                phase2aFailures.Count, phase2aResults.Length, job.JobId);
        }

        // Phase 2b: Final aggregation
        Logger.Information("Phase 2b: Aggregating and prioritizing findings for run {RunId}", job.JobId);

        var aggregationPrompt = ConsolidationPromptBuilder.BuildRefactoringAggregationPrompt(
            job.PipelineConfiguration.MaxRefactoringProposals,
            issueContext,
            outcomeContext.Length > 0 ? outcomeContext : null,
            job.RefactoringFocus);

        async Task<ConsolidationJobResult?> RunAggregationAsync()
        {
            var (_, aggregationFailure) = await RunWithTracingAsync("RefactoringDetection.Phase2b.Aggregation", job.JobId, async _ =>
            {
                return await ExecuteAgentAndCheckAsync(
                    agentProvider,
                    new AgentRequest
                    {
                        Prompt = aggregationPrompt,
                        WorkspacePath = workspacePath,
                        Timeout = job.PipelineConfiguration.AgentTimeout,
                        EffortOverride = AgentEffortLevel.High
                    },
                    job.JobId,
                    ct,
                    onOutputLine,
                    "refactoring_aggregation");
            });
            return aggregationFailure;
        }

        failure = await RunAggregationAsync();
        if (failure is not null) return (failure, detectors);

        // The aggregation writes [] when it drops every finding, so a missing file after a clean exit means the
        // call broke off. Run it once more: the Phase 1 findings are still on disk.
        if (!File.Exists(Path.Combine(workspacePath, AgentWorkspacePaths.RefactoringProposalsFilePath)))
        {
            Logger.Warning("Aggregation wrote no refactoring proposals file in run {RunId}, retrying once", job.JobId);
            failure = await RunAggregationAsync();
            if (failure is not null) return (failure, detectors);
        }

        // Proposals are now at .agent/refactoring-proposals.json — return success
        // (the caller will finalize with review + issue creation)
        return (new ConsolidationJobResult { JobId = job.JobId, Success = true, Summary = "Phased analysis complete" }, detectors);
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
        RefactoringScanOutcome scanOutcome,
        Action<string>? onOutputLine,
        CancellationToken ct)
    {
        var proposalsFilePath = Path.Combine(workspacePath, AgentWorkspacePaths.RefactoringProposalsFilePath);

        // The aggregation writes [] when it drops every finding, so a missing file means it broke
        // off, not that the scan found nothing.
        if (!File.Exists(proposalsFilePath))
        {
            Logger.Warning("No refactoring proposals file found at {Path} after aggregation in run {RunId}", proposalsFilePath, job.JobId);
            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorMessage = MissingProposalsFileMessage,
                ScanOutcome = scanOutcome
            };
        }

        var proposals = await ParseProposalsAsync(proposalsFilePath, ct);

        if (proposals is null)
        {
            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorMessage = "Failed to parse refactoring proposals JSON from agent output",
                ScanOutcome = scanOutcome
            };
        }

        if (proposals.Count == 0)
        {
            Logger.Information("No refactoring opportunities identified in run {RunId}", job.JobId);
            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = true,
                Summary = "No refactoring opportunities identified",
                ScanOutcome = scanOutcome with { ProposalCount = 0 }
            };
        }

        // Adversarial review (if enabled and proposals non-empty)
        TransitionTo(PipelineStep.ReviewingProposals);
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

        RecordUsage(reviewResult.ReviewAgentResult, "refactoring_review");
        RecordUsage(reviewResult.RefinementAgentResult, "refactoring_refinement");

        if (reviewResult.RefinementTriggered)
        {
            var refinedProposals = await ParseProposalsAsync(proposalsFilePath, ct);
            if (refinedProposals is null)
            {
                Logger.Warning("Refined proposals file is missing or malformed in run {RunId}, keeping original proposals", job.JobId);
            }
            else
            {
                proposals = refinedProposals;
                Logger.Information("Using refined proposals ({Count} proposals) in run {RunId}",
                    proposals.Count, job.JobId);
            }
        }

        // Deterministic checks the prompts cannot guarantee (paths, scope, category, duplicates)
        TransitionTo(PipelineStep.CreatingRefactoringIssues);
        var (validProposals, rejectedProposals) = RefactoringProposalValidator.Validate(
            proposals, workspacePath, baseline.ExistingIssueTitles,
            RefactoringCategories.ForFocus(job.RefactoringFocus));
        foreach (var rejection in rejectedProposals)
        {
            Logger.Warning("Dropped refactoring proposal '{Title}' in run {RunId}: {Reason}",
                rejection.Proposal.Title, job.JobId, rejection.Reason);
        }

        // Create GitHub issues (capped at MaxRefactoringProposals)
        var (createdIssues, firstFailureHint, failedProposals) = await RunWithTracingAsync("RefactoringDetection.CreateIssues", job.JobId, async activity =>
        {
            activity?.SetTag("pipeline.proposal_count", proposals.Count);
            activity?.SetTag("pipeline.rejected_proposal_count", rejectedProposals.Count);
            return await CreateIssuesAsync(
                validProposals, issueProvider, job.PipelineConfiguration.MaxRefactoringProposals, job.AutoDispatch, baseline.CommitSha, ct);
        });

        var attemptedCount = Math.Min(validProposals.Count, job.PipelineConfiguration.MaxRefactoringProposals);
        var summary = RefactoringIssueBodyFormatter.FormatRefactoringSummary(createdIssues, attemptedCount, firstFailureHint, rejectedProposals.Count);
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
            RefinementTokenUsage = reviewResult.RefinementTokenUsage,
            ScanOutcome = scanOutcome with
            {
                ProposalCount = proposals.Count,
                CreatedIssues = createdIssues,
                DroppedProposals = rejectedProposals
                    .Select(r => new RefactoringProposalNote { Title = r.Proposal.Title, Category = r.Proposal.Category, Reason = r.Reason })
                    .ToList(),
                UnfiledProposals = failedProposals
                    .Select(p => new RefactoringProposalNote { Title = p.Title, Category = p.Category })
                    .ToList()
            }
        };
    }

    /// <summary>
    /// Runs git log to identify frequently-changed files and writes a hotspot summary.
    /// Gracefully degrades on any failure — logs a warning and continues without the file.
    /// Commits that implement a scan issue (identified by <paramref name="scanIssueRefs"/>) are excluded.
    /// </summary>
    private async Task WriteHotspotAnalysisAsync(string workspacePath, TimeSpan lookback, IReadOnlySet<string> scanIssueRefs, CancellationToken ct)
    {
        try
        {
            var sinceDate = DateTime.UtcNow.Subtract(lookback).ToString("yyyy-MM-dd");
            var output = await RunGitCommandAsync(workspacePath, $"log --name-only --since=\"{sinceDate}\" --format=\"COMMIT_DATE:%ai%nCOMMIT_SUBJECT:%s\"", ct);

            Func<string, bool>? isExcludedCommit = null;
            if (scanIssueRefs.Count > 0)
            {
                isExcludedCommit = subject => scanIssueRefs.Any(r => subject.Contains($"({r})", StringComparison.Ordinal));
            }

            // Files deleted since are not refactoring targets any more
            var parseResult = RefactoringHotspotAnalyzer.ParseHotspotOutput(output, lookback, fileExists: f => File.Exists(Path.Combine(workspacePath, f)), isExcludedCommit: isExcludedCommit);
            if (parseResult.Summary is null)
            {
                Logger.Information("No git history found within lookback window for hotspot analysis");
                return;
            }

            // Count excluded commits for the log message
            var excludedCount = parseResult.ExcludedCommitCount;
            Logger.Information("Hotspot analysis left out {ExcludedCommitCount} commits of refactoring scan issues", excludedCount);

            var outputPath = Path.Combine(workspacePath, AgentWorkspacePaths.HotspotAnalysisFilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, parseResult.Summary, ct);

            Logger.Information("Wrote hotspot analysis to {Path}", outputPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "Hotspot analysis failed, continuing without it");
        }
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
            Logger.Warning("No refactoring proposals file found at {Path}", filePath);
            return null;
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
    /// Proposals are processed sequentially in the order they appear in <paramref name="proposals"/>.
    /// Individual issue creation failures are logged but do not stop processing.
    /// The first failure hint (classified HTTP status or exception type) is captured and returned
    /// alongside the list of successfully created issues and the proposals whose creation failed.
    /// </summary>
    private async Task<(IReadOnlyList<CreatedIssueInfo> Created, string? FirstFailureHint, IReadOnlyList<RefactoringProposal> Failed)> CreateIssuesAsync(
        IReadOnlyList<RefactoringProposal> proposals,
        IIssueProvider issueProvider,
        int maxProposals,
        bool autoDispatch,
        string? commitSha,
        CancellationToken ct)
    {
        var createdIssues = new List<CreatedIssueInfo>();
        var failedProposals = new List<RefactoringProposal>();
        string? firstFailureHint = null;
        var proposalsToProcess = proposals.Take(maxProposals).ToList();
        var labels = autoDispatch
            ? new[] { AgentLabels.Generated, AgentLabels.Next }
            : new[] { AgentLabels.Generated };

        foreach (var proposal in proposalsToProcess)
        {
            try
            {
                var body = RefactoringIssueBodyFormatter.FormatIssueBody(proposal, commitSha);
                var sanitizedTitle = RefactoringIssueBodyFormatter.SanitizeTitle(proposal.Title);
                var result = await issueProvider.CreateIssueAsync(
                    sanitizedTitle,
                    body,
                    labels,
                    ct);

                createdIssues.Add(new CreatedIssueInfo
                {
                    Identifier = result.Identifier,
                    Title = proposal.Title,
                    Url = result.Url,
                    Category = proposal.Category
                });

                Logger.Information("Created refactoring issue {Identifier}: {Title}",
                    result.Identifier, proposal.Title);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                firstFailureHint ??= ClassifyIssueCreationException(ex);
                failedProposals.Add(proposal);
                Logger.Warning(ex, "Failed to create issue for proposal '{Title}', continuing with remaining",
                    proposal.Title);
            }
        }

        return (createdIssues, firstFailureHint, failedProposals);
    }
}
