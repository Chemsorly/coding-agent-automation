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
    /// Fair dispatch — three-way interleaved round-robin (issues → PRs → decomposition).
    /// </summary>
    internal async Task<DispatchResult> DispatchFairRoundRobinAsync(
        IReadOnlyList<PipelineJobTemplate> pollableTemplates,
        IReadOnlyList<(PipelineJobTemplate Template, PipelineProject Project)> flattenedTemplates,
        PipelineConfiguration config,
        int maxRunsPerCycle,
        HashSet<(string IssueIdentifier, ProviderConfigId IssueProviderConfigId)> activeIssueIdentifiers,
        Dictionary<string, List<IssueSummary>> issueQueues,
        Dictionary<string, List<PullRequestSummary>> prQueues,
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>> decompositionQueues,
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase, PipelineJobTemplate Template)>> projectLevelDecompositionQueues,
        Action<string> reportStatus,
        Action<string?> reportIssue,
        Action notifyChange,
        CancellationToken stoppingToken,
        CancellationToken ct)
    {
        int remaining = maxRunsPerCycle > 0 ? maxRunsPerCycle : int.MaxValue;
        int processedCount = 0;
        int failedCount = 0;

        // Per-cycle dependency state cache shared across all issue evaluations
        var cycleStateCache = new Dictionary<int, bool>();

        // Build template → project lookup for passing project context at dispatch time
        var templateProjectLookup = flattenedTemplates.ToDictionary(ft => ft.Template.Id, ft => ft.Project);

        // Count active decomposition runs for concurrency enforcement
        var activeDecompositionCount = _orchestration.GetAllActiveRuns()
            .Count(r => r.RunType is PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition);

        var currentTurn = DispatchTurn.Issues;

        // Track last reported issue identifier for error logging in DispatchRoundAsync
        string? lastReportedIssue = null;
        var trackingReportIssue = (string? id) => { lastReportedIssue = id; reportIssue(id); };

        while (remaining > 0)
        {
            if (ct.IsCancellationRequested) break;

            bool issueMadeProgress = false;
            bool prMadeProgress = false;
            bool decompMadeProgress = false;

            bool hasIssues = HasEligible(pollableTemplates, issueQueues, t => t.ImplementationEnabled);
            bool hasPrs = HasEligible(pollableTemplates, prQueues, t => t.ReviewEnabled);
            bool hasDecomp = (HasEligible(pollableTemplates, decompositionQueues, t => t.DecompositionEnabled)
                || HasEligibleProjectLevelDecomposition(projectLevelDecompositionQueues))
                && activeDecompositionCount < config.MaxConcurrentDecompositions;

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

            // ── Issue dispatch (one per template per pass) ──
            if (currentTurn == DispatchTurn.Issues && hasIssues)
            {
                var (progress, count, processed, failed) = await DispatchRoundAsync(pollableTemplates, async (template, stopToken) =>
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
                            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedFilteredByLabel));
                            continue;
                        }
                        if (_orchestration.IsIssueBeingProcessed(candidate.Identifier, template.IssueProviderId))
                        {
                            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                            continue;
                        }
                        if (activeIssueIdentifiers.Contains((candidate.Identifier, template.IssueProviderId)))
                        {
                            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
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
                                PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedDependencyBlocked));
                                continue;
                            }
                        }

                        issue = candidate;
                        break;
                    }

                    if (issue is null) return DispatchAttemptResult.Skip;

                    trackingReportIssue(issue.Identifier);
                    reportStatus($"🔄 Dispatching #{issue.Identifier} from '{template.Name}'");
                    notifyChange();

                    var dispatchProject = templateProjectLookup.GetValueOrDefault(template.Id);
                    _logger.Information("Dispatching issue {Issue} with project '{ProjectName}' (id={ProjectId}, template={TemplateId})",
                        issue.Identifier, dispatchProject?.Name ?? "NULL", dispatchProject?.Id ?? "NULL", template.Id);

                    var dispatched = await DispatchViaOrchestrationOrLegacyAsync(
                        async ct => await _dispatchOrchestration!.PrepareDistributionRequestAsync(
                            issue.Identifier,
                            template.IssueProviderId, template.RepoProviderId,
                            template.BrainProviderId, template.PipelineProviderId,
                            "loop", dispatchProject ?? new PipelineProject { Id = "", Name = "Unknown" },
                            ct: ct),
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
                }, remaining, () => lastReportedIssue, stoppingToken, ct);
                issueMadeProgress = progress;
                remaining -= count;
                processedCount += processed;
                failedCount += failed;
            }

            if (ct.IsCancellationRequested || remaining <= 0) break;

            // ── PR review dispatch (one per template per pass) ──
            if (currentTurn == DispatchTurn.PullRequests && hasPrs)
            {
                var (progress, count, processed, failed) = await DispatchRoundAsync(pollableTemplates, async (template, stopToken) =>
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
                            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedFilteredByLabel));
                            continue;
                        }
                        if (_orchestration.IsIssueBeingProcessed(candidate.Identifier, template.IssueProviderId))
                        {
                            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                            continue;
                        }
                        if (activeIssueIdentifiers.Contains((candidate.Identifier, template.IssueProviderId)))
                        {
                            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                            continue;
                        }

                        pr = candidate;
                        break;
                    }

                    if (pr is null) return DispatchAttemptResult.Skip;

                    trackingReportIssue(pr.Identifier);
                    reportStatus($"🔄 Dispatching PR #{pr.Identifier} review from '{template.Name}'");
                    notifyChange();

                    var reviewProject = templateProjectLookup.GetValueOrDefault(template.Id);
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
                }, remaining, () => lastReportedIssue, stoppingToken, ct);
                prMadeProgress = progress;
                remaining -= count;
                processedCount += processed;
                failedCount += failed;
            }

            if (ct.IsCancellationRequested || remaining <= 0) break;

            // ── Decomposition dispatch (one per template per pass) ──
            if (currentTurn == DispatchTurn.Decomposition && hasDecomp)
            {
                var (progress, count, processed, failed) = await DispatchRoundAsync(pollableTemplates, async (template, stopToken) =>
                {
                    if (!template.DecompositionEnabled) return DispatchAttemptResult.Skip;
                    if (!decompositionQueues.TryGetValue(template.Id, out var queue) || queue.Count == 0)
                        return DispatchAttemptResult.Skip;

                    // Re-check concurrency limit before each dispatch
                    if (activeDecompositionCount >= config.MaxConcurrentDecompositions)
                    {
                        _logger.Information("Decomposition concurrency limit reached ({Active}/{Max}), skipping remaining decomposition dispatch",
                            activeDecompositionCount, config.MaxConcurrentDecompositions);
                        return DispatchAttemptResult.Abort;
                    }

                    (IssueSummary Issue, PipelineRunType Phase)? epic = null;
                    while (queue.Count > 0)
                    {
                        var candidate = queue[0];
                        queue.RemoveAt(0);

                        if (_orchestration.IsIssueBeingProcessed(candidate.Issue.Identifier, template.IssueProviderId))
                        {
                            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                            continue;
                        }
                        if (activeIssueIdentifiers.Contains((candidate.Issue.Identifier, template.IssueProviderId)))
                        {
                            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                            continue;
                        }

                        epic = candidate;
                        break;
                    }

                    if (epic is null) return DispatchAttemptResult.Skip;

                    var epicItem = epic.Value;
                    var phaseLabel = epicItem.Phase == PipelineRunType.DecompositionAnalysis ? "analysis" : "decomposition";

                    trackingReportIssue(epicItem.Issue.Identifier);
                    reportStatus($"🧩 Dispatching epic #{epicItem.Issue.Identifier} {phaseLabel} from '{template.Name}'");
                    notifyChange();

                    var decompProject = templateProjectLookup.GetValueOrDefault(template.Id);
                    var dispatched = await DispatchViaOrchestrationOrLegacyAsync(
                        async ct => await _dispatchOrchestration!.PrepareDecompositionDistributionRequestAsync(
                            epicItem.Issue.Identifier,
                            epicItem.Issue.Title ?? "",
                            epicItem.Phase,
                            template.IssueProviderId,
                            template.RepoProviderId,
                            template.BrainProviderId,
                            "loop",
                            // TODO: Add a test where templateProjectLookup is missing an entry for a pollable template
                            // to guard against regression and validate fallback PipelineProject behavior downstream.
                            decompProject ?? new PipelineProject { Id = "", Name = "Unknown" },
                            ct: ct),
                        () => JobDistributionRequest.FromTemplate(
                            template, epicItem.Issue, epicItem.Phase, initiatedBy: "loop",
                            projectId: decompProject?.Id, projectName: decompProject?.Name),
                        stopToken);

                    if (dispatched)
                    {
                        activeDecompositionCount++;
                        _logger.Information("Dispatched epic #{EpicIdentifier} ({Phase}) from template '{Template}'",
                            epicItem.Issue.Identifier, epicItem.Phase, template.Name);
                    }

                    PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision",
                        dispatched ? PipelineTelemetry.LoopDecisions.Dispatched : PipelineTelemetry.LoopDecisions.SkippedNoAgent));

                    return new DispatchAttemptResult(dispatched);
                }, remaining, () => lastReportedIssue, stoppingToken, ct);
                decompMadeProgress = progress;
                remaining -= count;
                processedCount += processed;
                failedCount += failed;
            }

            // ── Project-level decomposition dispatch ──
            if (currentTurn == DispatchTurn.Decomposition && !decompMadeProgress && projectLevelDecompositionQueues.Count > 0
                && activeDecompositionCount < config.MaxConcurrentDecompositions)
            {
                foreach (var kvp in projectLevelDecompositionQueues.ToList())
                {
                    if (remaining <= 0 || ct.IsCancellationRequested) break;
                    if (activeDecompositionCount >= config.MaxConcurrentDecompositions) break;

                    var queue = kvp.Value;
                    while (queue.Count > 0 && remaining > 0)
                    {
                        if (activeDecompositionCount >= config.MaxConcurrentDecompositions) break;

                        var candidate = queue[0];
                        queue.RemoveAt(0);

                        // Deduplication: skip if already being processed or queued
                        if (_orchestration.IsIssueBeingProcessed(candidate.Issue.Identifier, candidate.Template.IssueProviderId))
                        {
                            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                            continue;
                        }
                        if (activeIssueIdentifiers.Contains((candidate.Issue.Identifier, candidate.Template.IssueProviderId)))
                        {
                            PipelineTelemetry.LoopDispatchDecisions.Add(1, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedAlreadyProcessing));
                            continue;
                        }

                        var phaseLabel = candidate.Phase == PipelineRunType.DecompositionAnalysis ? "analysis" : "decomposition";

                        trackingReportIssue(candidate.Issue.Identifier);
                        reportStatus($"🧩 Dispatching project-level epic #{candidate.Issue.Identifier} {phaseLabel} from '{candidate.Template.Name}'");
                        notifyChange();

                        try
                        {
                            var projLevelProject = templateProjectLookup.GetValueOrDefault(candidate.Template.Id);
                            var dispatched = await DispatchViaOrchestrationOrLegacyAsync(
                                async ct => await _dispatchOrchestration!.PrepareDecompositionDistributionRequestAsync(
                                    candidate.Issue.Identifier,
                                    candidate.Issue.Title ?? "",
                                    candidate.Phase,
                                    candidate.Template.IssueProviderId,
                                    candidate.Template.RepoProviderId,
                                    candidate.Template.BrainProviderId,
                                    "loop",
                                    projLevelProject ?? new PipelineProject { Id = "", Name = "Unknown" },
                                    decompositionSource: "project-level",
                                    ct: ct),
                                () => JobDistributionRequest.FromTemplate(
                                    candidate.Template, candidate.Issue, candidate.Phase,
                                    initiatedBy: "loop", decompositionSource: "project-level",
                                    projectId: projLevelProject?.Id, projectName: projLevelProject?.Name),
                                stoppingToken);

                            if (dispatched)
                            {
                                activeDecompositionCount++;
                                remaining--;
                                decompMadeProgress = true;
                                // TODO: processedCount++ here is a behavioral change — the original code did NOT
                                // increment ProcessedCount for project-level decomposition dispatches. This makes
                                // UI/metrics counts higher than before. Either remove this to preserve original
                                // behavior, or also add failedCount++ in the catch block below for consistency.
                                processedCount++;
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
                            remaining--;
                            decompMadeProgress = true;
                            // TODO: If processedCount++ is kept on success (above), add failedCount++ here
                            // for consistent accounting. Original code counted neither success nor failure
                            // for project-level dispatches.
                        }

                        break; // One dispatch per project per round (fair alternation)
                    }
                }

                // Remove empty project queues
                foreach (var key in projectLevelDecompositionQueues.Where(kvp => kvp.Value.Count == 0).Select(kvp => kvp.Key).ToList())
                    projectLevelDecompositionQueues.Remove(key);
            }

            // If no queue made progress, all are exhausted
            if (!issueMadeProgress && !prMadeProgress && !decompMadeProgress) break;

            // Advance to next turn for fair alternation
            currentTurn = NextTurn(currentTurn);
        }

        // Emit skipped_max_runs for items remaining in queues after budget exhaustion
        if (remaining <= 0)
        {
            var remainingItems = issueQueues.Values.Sum(q => q.Count)
                + prQueues.Values.Sum(q => q.Count)
                + decompositionQueues.Values.Sum(q => q.Count)
                + projectLevelDecompositionQueues.Values.Sum(q => q.Count);
            if (remainingItems > 0)
                PipelineTelemetry.LoopDispatchDecisions.Add(remainingItems, new KeyValuePair<string, object?>("decision", PipelineTelemetry.LoopDecisions.SkippedMaxRuns));
        }

        return new DispatchResult(processedCount, failedCount);
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
