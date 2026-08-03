using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Fair round-robin dispatch logic across issue/PR/decomposition queues.
/// Alternates between dispatching one round of issues (one per template), one round
/// of PRs (one per template), and one round of decomposition (one per template) to
/// ensure all three queue types get fair access to the budget.
/// </summary>
internal sealed class DispatchScheduler
{
    private readonly IDispatchRunCreator _orchestration;
    private readonly IDispatchOrchestrationService? _dispatchOrchestration;
    private readonly IWorkDistributor? _workDistributor;
    private readonly IDependencyChecker? _dependencyChecker;
    private readonly ProviderCacheManager _cacheManager;
    private readonly Serilog.ILogger _logger;

    internal DispatchScheduler(
        IDispatchRunCreator orchestration,
        IDispatchOrchestrationService? dispatchOrchestration,
        IWorkDistributor? workDistributor,
        IDependencyChecker? dependencyChecker,
        ProviderCacheManager cacheManager,
        Serilog.ILogger logger)
    {
        _orchestration = orchestration;
        _dispatchOrchestration = dispatchOrchestration;
        _workDistributor = workDistributor;
        _dependencyChecker = dependencyChecker;
        _cacheManager = cacheManager;
        _logger = logger;
    }

    /// <summary>
    /// Represents which queue type the round-robin dispatcher should process next.
    /// </summary>
    internal enum DispatchTurn { Issues = 0, PullRequests = 1, Decomposition = 2 }

    /// <summary>
    /// Advances to the next turn in the three-way round-robin cycle.
    /// </summary>
    internal static DispatchTurn NextTurn(DispatchTurn turn) =>
        (DispatchTurn)(((int)turn + 1) % 3);

    /// <summary>
    /// Result of a single-template dispatch attempt within <see cref="DispatchRoundAsync"/>.
    /// </summary>
    internal readonly record struct DispatchAttemptResult(bool Dispatched, bool Attempted = true, bool AbortRemaining = false)
    {
        /// <summary>No candidate found for this template — skip it.</summary>
        public static readonly DispatchAttemptResult Skip = new(false, Attempted: false);

        /// <summary>Abort remaining templates (e.g., concurrency limit reached).</summary>
        public static readonly DispatchAttemptResult Abort = new(false, Attempted: false, AbortRemaining: true);
    }

    /// <summary>
    /// Result of a full dispatch cycle returned to the caller.
    /// </summary>
    internal readonly record struct DispatchResult(int ProcessedCount, int FailedCount);

    /// <summary>
    /// Bundles all queue state and callbacks for a single fair round-robin dispatch cycle.
    /// Passed to <see cref="DispatchFairRoundRobinAsync"/> instead of individual parameters,
    /// eliminating the S107 excessive-parameter violation.
    /// </summary>
    internal sealed class DispatchRoundRobinRequest
    {
        /// <summary>Templates eligible to receive dispatch in this cycle.</summary>
        public required IReadOnlyList<PipelineJobTemplate> PollableTemplates { get; init; }

        /// <summary>Flattened template-project pairs for project context resolution.</summary>
        public required IReadOnlyList<(PipelineJobTemplate Template, PipelineProject Project)> FlattenedTemplates { get; init; }

        /// <summary>Pipeline configuration for this dispatch cycle.</summary>
        public required PipelineConfiguration Config { get; init; }

        /// <summary>Maximum number of runs to dispatch in this cycle. Zero = unlimited.</summary>
        public required int MaxRunsPerCycle { get; init; }

        /// <summary>Issue identifiers already processing in this cycle (dedup).</summary>
        public required HashSet<(IssueIdentifier IssueIdentifier, ProviderConfigId IssueProviderConfigId)> ActiveIssueIdentifiers { get; init; }

        /// <summary>Per-template queues of implementation issues to dispatch.</summary>
        public required Dictionary<string, List<IssueSummary>> IssueQueues { get; init; }

        /// <summary>Per-template queues of PR reviews to dispatch.</summary>
        public required Dictionary<string, List<PullRequestSummary>> PrQueues { get; init; }

        /// <summary>Per-template queues of decomposition epics to dispatch.</summary>
        public required Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>> DecompositionQueues { get; init; }

        /// <summary>Per-project queues of project-level decomposition epics.</summary>
        public required Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>> ProjectLevelDecompositionQueues { get; init; }

        /// <summary>Callback to report current dispatch status string.</summary>
        public required Action<string> ReportStatus { get; init; }

        /// <summary>Callback to report the current issue identifier being dispatched.</summary>
        public required Action<string?> ReportIssue { get; init; }

        /// <summary>Callback to notify UI of a state change.</summary>
        public required Action NotifyChange { get; init; }
    }

    /// <summary>
    /// Bundles template, queue, and callback context shared by all private round-dispatch helpers.
    /// </summary>
    private sealed class RoundDispatchContext
    {
        public required IReadOnlyList<PipelineJobTemplate> PollableTemplates { get; init; }
        public required HashSet<(IssueIdentifier IssueIdentifier, ProviderConfigId IssueProviderConfigId)> ActiveIssueIdentifiers { get; init; }
        public required Dictionary<string, PipelineProject> TemplateProjectLookup { get; init; }
        public required Action<string?> TrackingReportIssue { get; init; }
        public required Action<string> ReportStatus { get; init; }
        public required Action NotifyChange { get; init; }
        public required int RemainingBudget { get; init; }
        public required Func<string?> GetCurrentIssueIdentifier { get; init; }
    }

    /// <summary>
    /// Fair dispatch — three-way interleaved round-robin (issues → PRs → decomposition).
    /// </summary>
    internal async Task<DispatchResult> DispatchFairRoundRobinAsync(
        DispatchRoundRobinRequest request,
        CancellationToken stoppingToken,
        CancellationToken ct)
    {
        int remaining = request.MaxRunsPerCycle > 0 ? request.MaxRunsPerCycle : int.MaxValue;
        int processedCount = 0;
        int failedCount = 0;

        // Per-cycle dependency state cache shared across all issue evaluations
        var cycleStateCache = new Dictionary<int, bool>();

        // Build template → project lookup for passing project context at dispatch time
        var templateProjectLookup = request.FlattenedTemplates.ToDictionary(ft => ft.Template.Id, ft => ft.Project);

        // Count active decomposition runs for concurrency enforcement
        var activeDecompositionCount = _orchestration.GetAllActiveRuns()
            .Count(r => r.RunType is PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition);

        var currentTurn = DispatchTurn.Issues;

        // Track last reported issue identifier for error logging in DispatchRoundAsync
        string? lastReportedIssue = null;
        var trackingReportIssue = (string? id) => { lastReportedIssue = id; request.ReportIssue(id); };

        while (remaining > 0)
        {
            if (ct.IsCancellationRequested) break;

            bool issueMadeProgress = false;
            bool prMadeProgress = false;
            bool decompMadeProgress = false;

            bool hasIssues = HasEligible(request.PollableTemplates, request.IssueQueues, t => t.ImplementationEnabled);
            bool hasPrs = HasEligible(request.PollableTemplates, request.PrQueues, t => t.ReviewEnabled);
            bool hasDecomp = (HasEligible(request.PollableTemplates, request.DecompositionQueues, t => t.DecompositionEnabled)
                || HasEligibleProjectLevelDecomposition(request.ProjectLevelDecompositionQueues))
                && activeDecompositionCount < request.Config.MaxConcurrentDecompositions;

            // Determine which queue to dispatch from this iteration.
            // If the current turn's queue is empty, try the next non-empty queue.
            var startTurn = currentTurn;
            bool foundTurn = false;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var tryTurn = (DispatchTurn)(((int)startTurn + attempt) % 3);
                if ((tryTurn == DispatchTurn.Issues && hasIssues) || (tryTurn == DispatchTurn.PullRequests && hasPrs) || (tryTurn == DispatchTurn.Decomposition && hasDecomp))
                {
                    currentTurn = tryTurn;
                    foundTurn = true;
                    break;
                }
            }
            if (!foundTurn) break; // All queues exhausted

            var roundCtx = new RoundDispatchContext
            {
                PollableTemplates = request.PollableTemplates,
                ActiveIssueIdentifiers = request.ActiveIssueIdentifiers,
                TemplateProjectLookup = templateProjectLookup,
                TrackingReportIssue = trackingReportIssue,
                ReportStatus = request.ReportStatus,
                NotifyChange = request.NotifyChange,
                RemainingBudget = remaining,
                GetCurrentIssueIdentifier = () => lastReportedIssue
            };

            // ── Issue dispatch (one per template per pass) ──
            if (currentTurn == DispatchTurn.Issues && hasIssues)
            {
                var (progress, count, processed, failed) = await DispatchIssueRoundAsync(
                    roundCtx, request.IssueQueues, cycleStateCache, stoppingToken, ct);
                issueMadeProgress = progress;
                remaining -= count;
                processedCount += processed;
                failedCount += failed;
            }

            if (ct.IsCancellationRequested || remaining <= 0) break;

            // ── PR review dispatch (one per template per pass) ──
            if (currentTurn == DispatchTurn.PullRequests && hasPrs)
            {
                var (progress, count, processed, failed) = await DispatchPrRoundAsync(
                    roundCtx, request.PrQueues, stoppingToken, ct);
                prMadeProgress = progress;
                remaining -= count;
                processedCount += processed;
                failedCount += failed;
            }

            if (ct.IsCancellationRequested || remaining <= 0) break;

            // ── Decomposition dispatch (one per template per pass) ──
            if (currentTurn == DispatchTurn.Decomposition && hasDecomp)
            {
                var (progress, count, processed, failed, additionalDecomp) = await DispatchDecompositionRoundAsync(
                    roundCtx, request.DecompositionQueues, request.Config, activeDecompositionCount, stoppingToken, ct);
                decompMadeProgress = progress;
                remaining -= count;
                processedCount += processed;
                failedCount += failed;
                activeDecompositionCount += additionalDecomp;
            }            // ── Project-level decomposition dispatch ──
            if (currentTurn == DispatchTurn.Decomposition && !decompMadeProgress && request.ProjectLevelDecompositionQueues.Count > 0
                && activeDecompositionCount < request.Config.MaxConcurrentDecompositions)
            {
                var (progress, count, processed, failed, additionalDecomp) = await DispatchProjectLevelDecompositionRoundAsync(
                    roundCtx, request.ProjectLevelDecompositionQueues, request.Config, activeDecompositionCount, stoppingToken, ct);
                decompMadeProgress = progress;
                remaining -= count;
                processedCount += processed;
                failedCount += failed;
                activeDecompositionCount += additionalDecomp;
            }

            // If no queue made progress, all are exhausted
            if (!issueMadeProgress && !prMadeProgress && !decompMadeProgress) break;

            // Advance to next turn for fair alternation
            currentTurn = NextTurn(currentTurn);
        }

        // Emit skipped_max_runs for items remaining in queues after budget exhaustion
        if (remaining <= 0)
        {
            var remainingItems = request.IssueQueues.Values.Sum(q => q.Count)
                + request.PrQueues.Values.Sum(q => q.Count)
                + request.DecompositionQueues.Values.Sum(q => q.Count)
                + request.ProjectLevelDecompositionQueues.Values.Sum(q => q.Count);
            if (remainingItems > 0)
                PipelineTelemetry.LoopDispatchDecisions.Add(remainingItems, new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedMaxRuns));
        }

        return new DispatchResult(processedCount, failedCount);
    }

    /// <summary>
    /// Dispatches one round of issues (one per template). Filters by ImplementationEnabled,
    /// dequeues candidates filtering by labels and duplicates, checks dependencies, then dispatches.
    /// </summary>
    private async Task<(bool madeProgress, int consumed, int processed, int failed)> DispatchIssueRoundAsync(
        RoundDispatchContext ctx,
        Dictionary<string, List<IssueSummary>> issueQueues,
        Dictionary<int, bool> cycleStateCache,
        CancellationToken stoppingToken,
        CancellationToken ct)
    {
        return await DispatchRoundAsync(ctx.PollableTemplates, async (template, stopToken) =>
        {
            if (!template.ImplementationEnabled) return DispatchAttemptResult.Skip;
            if (!issueQueues.TryGetValue(template.Id, out var queue) || queue.Count == 0)
                return DispatchAttemptResult.Skip;

            // Dequeue next valid issue
            IssueSummary? issue = null;
            while (queue.Count > 0)
            {
                var candidate = queue[0];
                queue.RemoveAt(0);

                if (candidate.Labels.Contains(AgentLabels.Error) || candidate.Labels.Contains(AgentLabels.NeedsRefinement))
                {
                    PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedFilteredByLabel));
                    continue;
                }
                if (_orchestration.IsIssueBeingProcessed(candidate.Identifier, template.IssueProviderId))
                {
                    PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                    continue;
                }
                if (ctx.ActiveIssueIdentifiers.Contains((candidate.Identifier, template.IssueProviderId)))
                {
                    PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                    continue;
                }

                if (_dependencyChecker != null)
                {
                    if (!_cacheManager.IssueProviders.TryGetValue(template.IssueProviderId, out var provider))
                    {
                        _logger.Warning("Provider '{ProviderId}' not in cache during dependency check for #{Identifier}, skipping dispatch",
                            template.IssueProviderId, candidate.Identifier);
                        continue;
                    }

                    var depResult = await _dependencyChecker.CheckAsync(
                        candidate.Identifier, candidate.Description, provider, cycleStateCache, ct);
                    if (!depResult.IsReady)
                    {
                        _logger.Information("Issue #{Identifier} blocked by open issues: {BlockedBy}. Skipping dispatch.",
                            candidate.Identifier, depResult.BlockedBy);
                        PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedDependencyBlocked));
                        continue;
                    }
                }

                issue = candidate;
                break;
            }

            if (issue is null) return DispatchAttemptResult.Skip;

            ctx.TrackingReportIssue(issue.Identifier);
            ctx.ReportStatus($"🔄 Dispatching #{issue.Identifier} from '{template.Name}'");
            ctx.NotifyChange();

            var dispatchProject = ctx.TemplateProjectLookup.GetValueOrDefault(template.Id);
            _logger.Information("Dispatching issue {Issue} with project '{ProjectName}' (id={ProjectId}, template={TemplateId})",
                issue.Identifier, dispatchProject?.Name ?? "NULL", dispatchProject?.Id ?? "NULL", template.Id);

            var dispatched = await DispatchViaOrchestrationOrLegacyAsync(
                async ct => await _dispatchOrchestration!.PrepareDistributionRequestAsync(
                    new ImplementationDispatchOrchestrationRequest
                    {
                        IssueIdentifier = issue.Identifier,
                        IssueProviderId = template.IssueProviderId,
                        RepoProviderId = template.RepoProviderId,
                        BrainProviderId = template.BrainProviderId,
                        PipelineProviderId = template.PipelineProviderId,
                        InitiatedBy = "loop",
                        Project = dispatchProject ?? new PipelineProject { Id = "", Name = "Unknown" }
                    },
                    ct),
                () => JobDistributionRequest.FromTemplate(
                    template, issue, initiatedBy: "loop",
                    projectId: dispatchProject?.Id, projectName: dispatchProject?.Name),
                stopToken);

            if (dispatched)
                _logger.Information("Dispatched issue #{Issue} from template '{Template}'",
                    issue.Identifier, template.Name);

            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision",
                dispatched ? PipelineTelemetry.LoopDecisions.Dispatched : PipelineTelemetry.LoopDecisions.SkippedNoAgent));

            return new DispatchAttemptResult(dispatched);
        }, ctx.RemainingBudget, ctx.GetCurrentIssueIdentifier, stoppingToken, ct);
    }

    /// <summary>
    /// Dispatches one round of PR reviews (one per template). Filters by ReviewEnabled,
    /// dequeues candidates filtering by labels and duplicates, then dispatches.
    /// </summary>
    private async Task<(bool madeProgress, int consumed, int processed, int failed)> DispatchPrRoundAsync(
        RoundDispatchContext ctx,
        Dictionary<string, List<PullRequestSummary>> prQueues,
        CancellationToken stoppingToken,
        CancellationToken ct)
    {
        return await DispatchRoundAsync(ctx.PollableTemplates, async (template, stopToken) =>
        {
            if (!template.ReviewEnabled) return DispatchAttemptResult.Skip;
            if (!prQueues.TryGetValue(template.Id, out var queue) || queue.Count == 0)
                return DispatchAttemptResult.Skip;

            PullRequestSummary? pr = null;
            while (queue.Count > 0)
            {
                var candidate = queue[0];
                queue.RemoveAt(0);

                if (candidate.Labels.Contains(AgentLabels.Error) ||
                    candidate.Labels.Contains(AgentLabels.InProgress) ||
                    candidate.Labels.Contains(AgentLabels.Done) ||
                    candidate.Labels.Contains(AgentLabels.Cancelled))
                {
                    PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedFilteredByLabel));
                    continue;
                }
                if (_orchestration.IsIssueBeingProcessed(candidate.Identifier, template.IssueProviderId))
                {
                    PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                    continue;
                }
                if (ctx.ActiveIssueIdentifiers.Contains((candidate.Identifier, template.IssueProviderId)))
                {
                    PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                    continue;
                }

                pr = candidate;
                break;
            }

            if (pr is null) return DispatchAttemptResult.Skip;

            ctx.TrackingReportIssue(pr.Identifier);
            ctx.ReportStatus($"🔄 Dispatching PR #{pr.Identifier} review from '{template.Name}'");
            ctx.NotifyChange();

            var reviewProject = ctx.TemplateProjectLookup.GetValueOrDefault(template.Id);
            var dispatched = await DispatchViaOrchestrationOrLegacyAsync(
                async ct =>
                {
                    var reviewDispatchReq = new ReviewDispatchRequest
                    {
                        PrIdentifier = pr.Identifier,
                        PrBranchName = pr.BranchName,
                        PrTitle = pr.Title,
                        PrDescription = pr.Description,
                        PrAuthor = pr.Author,
                        PrUrl = pr.Url,
                        PrTargetBranch = pr.TargetBranch,
                        IssueProviderId = template.IssueProviderId,
                        RepoProviderId = template.RepoProviderId,
                        BrainProviderId = template.BrainProviderId,
                        InitiatedBy = "loop"
                    };
                    // TODO: Add a test where templateProjectLookup is missing an entry for a pollable template
                    // to guard against regression (KeyNotFoundException) and validate the fallback behavior.
                    return await _dispatchOrchestration!.PrepareReviewDistributionRequestAsync(
                        reviewDispatchReq,
                        reviewProject ?? new PipelineProject { Id = "", Name = "Unknown" },
                        ct);
                },
                () => JobDistributionRequest.FromTemplate(
                    template, pr, initiatedBy: "loop", useFullPrMetadata: false,
                    projectId: reviewProject?.Id, projectName: reviewProject?.Name),
                stopToken);

            if (dispatched)
                _logger.Information("Dispatched PR #{PrIdentifier} review from template '{Template}'",
                    pr.Identifier, template.Name);

            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision",
                dispatched ? PipelineTelemetry.LoopDecisions.Dispatched : PipelineTelemetry.LoopDecisions.SkippedNoAgent));

            return new DispatchAttemptResult(dispatched);
        }, ctx.RemainingBudget, ctx.GetCurrentIssueIdentifier, stoppingToken, ct);
    }

    /// <summary>
    /// Dispatches one round of template-based decomposition (one per template). Filters by DecompositionEnabled,
    /// checks concurrency limit, dequeues candidates with duplicate checks, then dispatches.
    /// Returns additional decomposition dispatch count for coordinator tracking.
    /// </summary>
    private async Task<(bool madeProgress, int consumed, int processed, int failed, int additionalDecompDispatches)> DispatchDecompositionRoundAsync(
        RoundDispatchContext ctx,
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>> decompositionQueues,
        PipelineConfiguration config,
        int activeDecompositionCount,
        CancellationToken stoppingToken,
        CancellationToken ct)
    {
        int additionalDecompDispatches = 0;

        var (madeProgress, consumed, processed, failed) = await DispatchRoundAsync(ctx.PollableTemplates, async (template, stopToken) =>
        {
            if (!template.DecompositionEnabled) return DispatchAttemptResult.Skip;
            if (!decompositionQueues.TryGetValue(template.Id, out var queue) || queue.Count == 0)
                return DispatchAttemptResult.Skip;

            // Re-check concurrency limit before each dispatch
            if (activeDecompositionCount + additionalDecompDispatches >= config.MaxConcurrentDecompositions)
            {
                _logger.Information("Decomposition concurrency limit reached ({Active}/{Max}), skipping remaining decomposition dispatch",
                    activeDecompositionCount + additionalDecompDispatches, config.MaxConcurrentDecompositions);
                return DispatchAttemptResult.Abort;
            }

            (IssueSummary Issue, PipelineRunType Phase)? epic = null;
            while (queue.Count > 0)
            {
                var candidate = queue[0];
                queue.RemoveAt(0);

                if (_orchestration.IsIssueBeingProcessed(candidate.Issue.Identifier, template.IssueProviderId))
                {
                    PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                    continue;
                }
                if (ctx.ActiveIssueIdentifiers.Contains((candidate.Issue.Identifier, template.IssueProviderId)))
                {
                    PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                    continue;
                }

                epic = candidate;
                break;
            }

            if (epic is null) return DispatchAttemptResult.Skip;

            var epicItem = epic.Value;
            var phaseLabel = epicItem.Phase == PipelineRunType.DecompositionAnalysis ? "analysis" : "decomposition";

            ctx.TrackingReportIssue(epicItem.Issue.Identifier);
            ctx.ReportStatus($"🧩 Dispatching epic #{epicItem.Issue.Identifier} {phaseLabel} from '{template.Name}'");
            ctx.NotifyChange();

            var decompProject = ctx.TemplateProjectLookup.GetValueOrDefault(template.Id);
            var dispatched = await DispatchViaOrchestrationOrLegacyAsync(
                async ct => await _dispatchOrchestration!.PrepareDecompositionDistributionRequestAsync(
                    new DecompositionDispatchOrchestrationRequest
                    {
                        EpicIdentifier = epicItem.Issue.Identifier,
                        EpicTitle = epicItem.Issue.Title ?? "",
                        PhaseType = epicItem.Phase,
                        IssueProviderId = template.IssueProviderId,
                        RepoProviderId = template.RepoProviderId,
                        BrainProviderId = template.BrainProviderId,
                        InitiatedBy = "loop",
                        // TODO: Add a test where templateProjectLookup is missing an entry for a pollable template
                        // to guard against regression and validate fallback PipelineProject behavior downstream.
                        Project = decompProject ?? new PipelineProject { Id = "", Name = "Unknown" }
                    },
                    ct),
                () => JobDistributionRequest.FromTemplate(
                    template, epicItem.Issue, epicItem.Phase, initiatedBy: "loop",
                    projectId: decompProject?.Id, projectName: decompProject?.Name),
                stopToken);

            if (dispatched)
            {
                additionalDecompDispatches++;
                _logger.Information("Dispatched epic #{EpicIdentifier} ({Phase}) from template '{Template}'",
                    epicItem.Issue.Identifier, epicItem.Phase, template.Name);
            }

            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision",
                dispatched ? PipelineTelemetry.LoopDecisions.Dispatched : PipelineTelemetry.LoopDecisions.SkippedNoAgent));

            return new DispatchAttemptResult(dispatched);
        }, ctx.RemainingBudget, ctx.GetCurrentIssueIdentifier, stoppingToken, ct);

        return (madeProgress, consumed, processed, failed, additionalDecompDispatches);
    }

    /// <summary>
    /// Dispatches project-level decomposition epics. Iterates project queues with one dispatch
    /// attempt per project per round (fair alternation). Does not use DispatchRoundAsync — manages
    /// its own iteration, try/catch, and counter tracking.
    /// Returns additional decomposition dispatch count for coordinator tracking.
    /// </summary>
    private async Task<(bool madeProgress, int consumed, int processed, int failed, int additionalDecompDispatches)> DispatchProjectLevelDecompositionRoundAsync(
        RoundDispatchContext ctx,
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>> projectLevelDecompositionQueues,
        PipelineConfiguration config,
        int activeDecompositionCount,
        CancellationToken stoppingToken,
        CancellationToken ct)
    {
        bool madeProgress = false;
        int consumed = 0;
        int processed = 0;
        int failed = 0;
        int additionalDecompDispatches = 0;

        foreach (var kvp in projectLevelDecompositionQueues.ToList())
        {
            if (ctx.RemainingBudget - consumed <= 0 || ct.IsCancellationRequested) break;
            if (activeDecompositionCount + additionalDecompDispatches >= config.MaxConcurrentDecompositions) break;

            var queue = kvp.Value;
            while (queue.Count > 0 && ctx.RemainingBudget - consumed > 0)
            {
                if (activeDecompositionCount + additionalDecompDispatches >= config.MaxConcurrentDecompositions) break;

                var candidate = queue[0];
                queue.RemoveAt(0);

                // Deduplication: skip if already being processed or queued
                if (_orchestration.IsIssueBeingProcessed(candidate.Issue.Identifier, candidate.Template.IssueProviderId))
                {
                    PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                    continue;
                }
                if (ctx.ActiveIssueIdentifiers.Contains((candidate.Issue.Identifier, candidate.Template.IssueProviderId)))
                {
                    PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>(ActivityTags.Decision, PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                    continue;
                }

                var phaseLabel = candidate.Phase == PipelineRunType.DecompositionAnalysis ? "analysis" : "decomposition";

                ctx.TrackingReportIssue(candidate.Issue.Identifier);
                ctx.ReportStatus($"🧩 Dispatching project-level epic #{candidate.Issue.Identifier} {phaseLabel} from '{candidate.Template.Name}'");
                ctx.NotifyChange();

                try
                {
                    var projLevelProject = ctx.TemplateProjectLookup.GetValueOrDefault(candidate.Template.Id);
                    var dispatched = await DispatchViaOrchestrationOrLegacyAsync(
                        async ct => await _dispatchOrchestration!.PrepareDecompositionDistributionRequestAsync(
                            new DecompositionDispatchOrchestrationRequest
                            {
                                EpicIdentifier = candidate.Issue.Identifier,
                                EpicTitle = candidate.Issue.Title ?? "",
                                PhaseType = candidate.Phase,
                                IssueProviderId = candidate.Template.IssueProviderId,
                                RepoProviderId = candidate.Template.RepoProviderId,
                                BrainProviderId = candidate.Template.BrainProviderId,
                                InitiatedBy = "loop",
                                Project = projLevelProject ?? new PipelineProject { Id = "", Name = "Unknown" },
                                DecompositionSource = "project-level"
                            },
                            ct),
                        () => JobDistributionRequest.FromTemplate(
                            candidate.Template, candidate.Issue, candidate.Phase,
                            initiatedBy: "loop", decompositionSource: "project-level",
                            projectId: projLevelProject?.Id, projectName: projLevelProject?.Name),
                        stoppingToken);

                    if (dispatched)
                    {
                        additionalDecompDispatches++;
                        consumed++;
                        madeProgress = true;
                        // Consistent with DispatchRoundAsync: successful dispatch counts as processed.
                        // TODO: Add a targeted unit test that exercises DispatchFairRoundRobinAsync with a successful
                        // project-level decomposition and asserts the returned ProcessedCount includes it.
                        processed++;
                        _logger.Information("Dispatched project-level epic #{EpicIdentifier} ({Phase}) via template '{Template}'",
                            candidate.Issue.Identifier, candidate.Phase, candidate.Template.Name);
                    }

                    PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision",
                        dispatched ? PipelineTelemetry.LoopDecisions.Dispatched : PipelineTelemetry.LoopDecisions.SkippedNoAgent));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Project-level decomposition dispatch failed for epic #{EpicIdentifier}: {Error}",
                        candidate.Issue.Identifier, ex.Message);
                    consumed++;
                    madeProgress = true;
                    // Consistent with DispatchRoundAsync: failures count as both processed and failed.
                    // TODO: Add a targeted unit test that triggers a dispatch failure for a project-level
                    // decomposition candidate and asserts DispatchResult contains the expected ProcessedCount
                    // and FailedCount. This is a behavioral change (previously neither was incremented on failure).
                    processed++;
                    failed++;
                }

                break; // One dispatch per project per round (fair alternation)
            }
        }

        // Remove empty project queues
        foreach (var key in projectLevelDecompositionQueues.Where(kvp => kvp.Value.Count == 0).Select(kvp => kvp.Key).ToList())
            projectLevelDecompositionQueues.Remove(key);

        return (madeProgress, consumed, processed, failed, additionalDecompDispatches);
    }

    /// <summary>
    /// Shared dispatch helper that iterates templates, invokes the dispatch delegate inside
    /// a try/catch, and manages counters and progress tracking.
    /// </summary>
    private async Task<(bool madeProgress, int consumed, int processed, int failed)> DispatchRoundAsync(
        IReadOnlyList<PipelineJobTemplate> pollableTemplates,
        Func<PipelineJobTemplate, CancellationToken, Task<DispatchAttemptResult>> tryDispatchOne,
        int remainingBudget,
        Func<string?> getCurrentIssueIdentifier,
        CancellationToken stoppingToken,
        CancellationToken ct)
    {
        bool madeProgress = false;
        int consumed = 0;
        int processed = 0;
        int failed = 0;

        foreach (var template in pollableTemplates)
        {
            if (remainingBudget - consumed <= 0) break;
            if (ct.IsCancellationRequested) break;

            DispatchAttemptResult result;
            try
            {
                result = await tryDispatchOne(template, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Dispatch failed for {Identifier} from template '{Template}'",
                    getCurrentIssueIdentifier(), template.Name);
                failed++;
                processed++;
                consumed++;
                madeProgress = true;
                continue;
            }

            if (result.AbortRemaining) break;
            if (!result.Attempted) continue;

            if (result.Dispatched)
            {
                processed++;
                consumed++;
                madeProgress = true;
            }
        }

        return (madeProgress, consumed, processed, failed);
    }

    /// <summary>
    /// Shared helper that encapsulates the DB-vs-legacy dispatch branching.
    /// </summary>
    private async Task<bool> DispatchViaOrchestrationOrLegacyAsync(
        Func<CancellationToken, Task<JobDistributionRequest?>> prepareDbRequest,
        Func<JobDistributionRequest> buildLegacyRequest,
        CancellationToken ct)
    {
        if (_dispatchOrchestration is not null)
        {
            var request = await prepareDbRequest(ct);
            if (request is null) return false;
            var outcome = await _dispatchOrchestration.DistributeAndFinalizeAsync(request, ct);
            return outcome.Success;
        }
        else
        {
            var minimalRequest = buildLegacyRequest();
            var result = await _workDistributor!.DistributeAsync(minimalRequest, ct);
            return result.Success;
        }
    }

    /// <summary>
    /// Checks whether any pollable template has eligible items remaining in its queue.
    /// </summary>
    internal static bool HasEligible<T>(
        IReadOnlyList<PipelineJobTemplate> pollableTemplates,
        Dictionary<string, List<T>> queues,
        Func<PipelineJobTemplate, bool> isEnabledForTemplate)
    {
        foreach (var template in pollableTemplates)
        {
            if (!isEnabledForTemplate(template)) continue;
            if (queues.TryGetValue(template.Id, out var queue) && queue.Count > 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Checks whether any project-level decomposition queue has eligible epics remaining.
    /// </summary>
    internal static bool HasEligibleProjectLevelDecomposition(
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>> projectLevelQueues)
    {
        foreach (var kvp in projectLevelQueues)
        {
            if (kvp.Value.Count > 0)
                return true;
        }
        return false;
    }
}
