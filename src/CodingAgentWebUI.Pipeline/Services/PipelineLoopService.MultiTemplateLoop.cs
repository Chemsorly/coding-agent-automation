using System.Collections.Concurrent;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;

namespace CodingAgentWebUI.Pipeline.Services;

public sealed partial class PipelineLoopService
{
    /// <summary>
    /// Generic cache reconciliation: evicts stale entries (disposes before removing),
    /// creates missing entries via the factory delegate.
    /// </summary>
    private async Task ReconcileCacheAsync<TProvider>(
        Dictionary<string, TProvider> cache,
        HashSet<string> neededIds,
        IReadOnlyList<ProviderConfig> providerConfigs,
        Func<ProviderConfig, TProvider> factory,
        string providerKindLabel,
        CancellationToken ct) where TProvider : IAsyncDisposable
    {
        // Evict stale entries
        var staleKeys = cache.Keys.Where(k => !neededIds.Contains(k)).ToList();
        foreach (var key in staleKeys)
        {
            if (cache.TryGetValue(key, out var provider))
            {
                try { await provider.DisposeAsync(); }
                catch (Exception ex) { _logger.Warning(ex, "Failed to dispose cached {Kind} provider {ProviderId}", providerKindLabel, key); }
                cache.Remove(key);
            }
        }

        // Create missing entries
        foreach (var neededId in neededIds)
        {
            ct.ThrowIfCancellationRequested();

            if (!cache.ContainsKey(neededId))
            {
                var config = providerConfigs.FirstOrDefault(c => c.Id == neededId);
                if (config is not null)
                {
                    try
                    {
                        cache[neededId] = factory(config);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Failed to create {Kind} provider for {ProviderId}", providerKindLabel, neededId);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Reconciles the provider cache with the needed set of IssueProviderIds.
    /// </summary>
    private Task ReconcileProviderCacheAsync(
        HashSet<string> neededIssueProviderIds,
        IReadOnlyList<ProviderConfig> issueProviderConfigs,
        CancellationToken ct)
        => ReconcileCacheAsync(_providerCache, neededIssueProviderIds, issueProviderConfigs,
            _providerFactory.CreateIssueProvider, "issue", ct);

    /// <summary>
    /// Reconciles the repository provider cache with the needed set of RepoProviderIds.
    /// </summary>
    private Task ReconcileRepoProviderCacheAsync(
        HashSet<string> neededRepoProviderIds,
        IReadOnlyList<ProviderConfig> repoProviderConfigs,
        CancellationToken ct)
        => ReconcileCacheAsync(_repoProviderCache, neededRepoProviderIds, repoProviderConfigs,
            _providerFactory.CreateRepositoryProvider, "repo", ct);

    /// <summary>
    /// Evicts a provider from the cache due to an auth error. Disposes and removes it
    /// so the next cycle recreates a fresh instance.
    /// </summary>
    private async Task EvictProviderOnAuthErrorAsync(string issueProviderId)
    {
        if (_providerCache.TryGetValue(issueProviderId, out var provider))
        {
            try { await provider.DisposeAsync(); }
            catch (Exception ex) { _logger.Warning(ex, "Failed to dispose provider {ProviderId} after auth error", issueProviderId); }
            _providerCache.Remove(issueProviderId);
        }
    }

    /// <summary>
    /// Multi-template round-robin loop. Reads config snapshot at cycle start, reconciles
    /// provider cache, polls each enabled template, then dispatches issues fairly.
    /// </summary>
    private async Task RunMultiTemplateLoopAsync(CancellationToken stoppingToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _loopCts?.Token ?? CancellationToken.None);
        var ct = linkedCts.Token;

        while (!_stopRequested && !ct.IsCancellationRequested)
        {
            // Step 1: Snapshot — read config at cycle start (immutable for cycle duration)
            var config = await _pipelineConfigStore.LoadPipelineConfigAsync(ct);
            var pollInterval = config.ClosedLoopPollInterval;
            var maxRunsPerCycle = config.ClosedLoopMaxRunsPerCycle;
            var maxConsecutiveFailures = config.ClosedLoopMaxConsecutivePollFailures;
            var maxPagesToFetch = config.ClosedLoopMaxPagesToFetch;

            var enabledTemplates = config.PipelineJobTemplates.Where(t => t.Enabled).ToList();

            // Filter out rate-limited templates
            var now = DateTimeOffset.UtcNow;
            var pollableTemplates = enabledTemplates.Where(t =>
            {
                if (_templateStatuses.TryGetValue(t.Id, out var status) && status.RateLimitResetAt.HasValue)
                    return now >= status.RateLimitResetAt.Value;
                return true;
            }).ToList();

            CurrentCycleTemplateCount = enabledTemplates.Count;

            // Step 2: Provider cache reconciliation
            var neededIds = enabledTemplates.Select(t => t.IssueProviderId).ToHashSet();
            var issueProviderConfigs = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Issue, ct);
            await ReconcileProviderCacheAsync(neededIds, issueProviderConfigs, ct);

            // Reconcile repo provider cache for templates with ReviewEnabled or DecompositionEnabled
            var neededRepoIds = enabledTemplates
                .Where(t => t.ReviewEnabled || t.DecompositionEnabled)
                .Select(t => t.RepoProviderId)
                .ToHashSet();
            if (neededRepoIds.Count > 0)
            {
                try
                {
                    var repoProviderConfigs = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
                    await ReconcileRepoProviderCacheAsync(neededRepoIds, repoProviderConfigs, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to reconcile repo provider cache, PR polling will be skipped this cycle");
                }
            }

            // Step 3: Poll once per pollable template (issues + PRs + decomposition)
            var issueQueues = new Dictionary<string, List<IssueSummary>>();
            var prQueues = new Dictionary<string, List<PullRequestSummary>>();
            var decompositionQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>();
            for (int i = 0; i < pollableTemplates.Count; i++)
            {
                if (_stopRequested || ct.IsCancellationRequested) break;

                var template = pollableTemplates[i];
                CurrentCycleTemplateIndex = i;
                StatusMessage = $"🔄 Polling template '{template.Name}' ({i + 1} of {pollableTemplates.Count})";

                // Mark as currently polling
                _templateStatuses[template.Id] = (_templateStatuses.TryGetValue(template.Id, out var prev) ? prev : ConfigStatusSnapshot.Empty)
                    with { IsCurrentlyPolling = true };
                NotifyChange();

                try
                {
                    // ── Issue polling (only when ImplementationEnabled) ──
                    if (template.ImplementationEnabled)
                    {
                        if (!_providerCache.TryGetValue(template.IssueProviderId, out var provider))
                        {
                            // Provider not in cache (config issue) — skip issues
                            _templateStatuses[template.Id] = new ConfigStatusSnapshot
                            {
                                LastPollTime = DateTimeOffset.UtcNow,
                                LastError = $"Issue provider '{template.IssueProviderId}' not found in cache.",
                                IsCurrentlyPolling = false
                            };
                            issueQueues[template.Id] = new List<IssueSummary>();
                        }
                        else
                        {
                            var issues = await FetchAgentNextIssuesForProviderAsync(provider, maxPagesToFetch, ct);
                            issueQueues[template.Id] = issues;
                        }
                    }
                    else
                    {
                        issueQueues[template.Id] = new List<IssueSummary>();
                    }

                    // ── PR polling (only when ReviewEnabled) ──
                    // Wrapped in its own try-catch so that a PR polling failure does not
                    // discard the already-fetched issue queue for this template.
                    prQueues[template.Id] = new List<PullRequestSummary>();
                    if (template.ReviewEnabled)
                    {
                        try
                        {
                            if (!_repoProviderCache.TryGetValue(template.RepoProviderId, out var repoProvider))
                            {
                                _logger.Warning("Template '{TemplateName}': repo provider '{RepoProviderId}' not found in cache, skipping PR polling",
                                    template.Name, template.RepoProviderId);
                            }
                            else
                            {
                                var prs = await FetchAgentNextPullRequestsAsync(repoProvider, maxPagesToFetch, ct);
                                prQueues[template.Id] = prs;
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            _logger.Warning(ex, "Template '{TemplateName}' PR polling failed, issue polling unaffected: {Error}",
                                template.Name, ex.Message);
                        }
                    }

                    // ── Decomposition polling (only when DecompositionEnabled) ──
                    // Wrapped in its own try-catch so that a decomposition polling failure does not
                    // discard the already-fetched issue/PR queues for this template.
                    decompositionQueues[template.Id] = new List<(IssueSummary, PipelineRunType)>();
                    if (template.DecompositionEnabled)
                    {
                        try
                        {
                            if (!_providerCache.TryGetValue(template.IssueProviderId, out var decompProvider))
                            {
                                _logger.Warning("Template '{TemplateName}': issue provider '{IssueProviderId}' not found in cache, skipping decomposition polling",
                                    template.Name, template.IssueProviderId);
                            }
                            else
                            {
                                // Validate that RepoProviderId references an existing provider config (Req 1.3)
                                // IssueProviderId is already validated by the provider cache lookup above.
                                if (!_repoProviderCache.ContainsKey(template.RepoProviderId))
                                {
                                    _logger.Warning("Template '{TemplateName}': decomposition skipped — RepoProviderId '{RepoProviderId}' references non-existent provider config",
                                        template.Name, template.RepoProviderId);
                                }
                                else
                                {
                                    // Poll for agent:epic issues (Phase 1 candidates)
                                    var epicIssues = await FetchEpicIssuesAsync(decompProvider, AgentLabels.Epic, maxPagesToFetch, ct);
                                    foreach (var epic in epicIssues)
                                        decompositionQueues[template.Id].Add((epic, PipelineRunType.DecompositionAnalysis));

                                    // Poll for agent:epic-approved issues (Phase 2 candidates)
                                    var approvedIssues = await FetchEpicIssuesAsync(decompProvider, AgentLabels.EpicApproved, maxPagesToFetch, ct);
                                    foreach (var approved in approvedIssues)
                                        decompositionQueues[template.Id].Add((approved, PipelineRunType.Decomposition));
                                }
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            _logger.Warning(ex, "Template '{TemplateName}' decomposition polling failed, issue/PR polling unaffected: {Error}",
                                template.Name, ex.Message);
                        }
                    }

                    // Success — update status
                    var issueCount = issueQueues[template.Id].Count;
                    var prCount = prQueues[template.Id].Count;
                    var decompCount = decompositionQueues[template.Id].Count;
                    _templateStatuses[template.Id] = new ConfigStatusSnapshot
                    {
                        LastPollTime = DateTimeOffset.UtcNow,
                        LastPollIssueCount = issueCount + prCount + decompCount,
                        LastError = null,
                        ConsecutiveFailures = 0,
                        RateLimitResetAt = null,
                        IsCurrentlyPolling = false
                    };
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (RateLimitExceededException ex)
                {
                    _logger.Warning("Template '{TemplateName}' rate limited until {ResetAt}", template.Name, ex.ResetAt);
                    var prevStatus = _templateStatuses.TryGetValue(template.Id, out var s) ? s : ConfigStatusSnapshot.Empty;
                    _templateStatuses[template.Id] = prevStatus with
                    {
                        LastPollTime = DateTimeOffset.UtcNow,
                        RateLimitResetAt = ex.ResetAt,
                        IsCurrentlyPolling = false
                    };
                    issueQueues[template.Id] = new List<IssueSummary>();
                    prQueues[template.Id] = new List<PullRequestSummary>();
                    decompositionQueues[template.Id] = new List<(IssueSummary, PipelineRunType)>();
                }
                catch (Exception ex) when (IsAuthError(ex))
                {
                    _logger.Warning(ex, "Template '{TemplateName}' auth error, evicting cached provider", template.Name);
                    await EvictProviderOnAuthErrorAsync(template.IssueProviderId);
                    var prevStatus = _templateStatuses.TryGetValue(template.Id, out var s) ? s : ConfigStatusSnapshot.Empty;
                    _templateStatuses[template.Id] = prevStatus with
                    {
                        LastPollTime = DateTimeOffset.UtcNow,
                        LastError = ex.Message,
                        ConsecutiveFailures = prevStatus.ConsecutiveFailures + 1,
                        IsCurrentlyPolling = false
                    };
                    issueQueues[template.Id] = new List<IssueSummary>();
                    prQueues[template.Id] = new List<PullRequestSummary>();
                    decompositionQueues[template.Id] = new List<(IssueSummary, PipelineRunType)>();
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Template '{TemplateName}' poll failed: {Error}", template.Name, ex.Message);
                    var prevStatus = _templateStatuses.TryGetValue(template.Id, out var s) ? s : ConfigStatusSnapshot.Empty;
                    _templateStatuses[template.Id] = prevStatus with
                    {
                        LastPollTime = DateTimeOffset.UtcNow,
                        LastError = ex.Message,
                        ConsecutiveFailures = prevStatus.ConsecutiveFailures + 1,
                        IsCurrentlyPolling = false
                    };
                    issueQueues[template.Id] = new List<IssueSummary>();
                    prQueues[template.Id] = new List<PullRequestSummary>();
                    decompositionQueues[template.Id] = new List<(IssueSummary, PipelineRunType)>();
                }
            }

            if (_stopRequested || ct.IsCancellationRequested) break;

            // Step 4: Circuit breaker — trip only when ALL enabled templates are failing
            var allFailing = enabledTemplates.Count > 0 && enabledTemplates.All(t =>
            {
                if (_templateStatuses.TryGetValue(t.Id, out var s))
                    return s.ConsecutiveFailures >= maxConsecutiveFailures;
                return false;
            });

            if (allFailing)
            {
                IsCircuitBroken = true;
                StatusMessage = $"⚠️ Loop paused — all {enabledTemplates.Count} templates failing.";
                NotifyChange();
                _logger.Warning("Circuit breaker tripped: all {Count} enabled templates have {Threshold}+ consecutive failures",
                    enabledTemplates.Count, maxConsecutiveFailures);

                _resumeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                try { await _resumeSignal.Task.WaitAsync(ct); }
                catch (OperationCanceledException) { break; }
                if (_stopRequested) break;
                continue;
            }

            // Step 5: Fair dispatch — three-way interleaved round-robin (issues → PRs → decomposition)
            // Alternates between dispatching one round of issues (one per template), one round
            // of PRs (one per template), and one round of decomposition (one per template) to
            // ensure all three queue types get fair access to the budget.
            // Within each round, templates are served round-robin (one item per template per pass).
            {
                int remaining = maxRunsPerCycle > 0 ? maxRunsPerCycle : int.MaxValue;

                // Per-cycle dependency state cache shared across all issue evaluations
                var cycleStateCache = new Dictionary<int, bool>();

                // Count active decomposition runs for concurrency enforcement
                var activeDecompositionCount = _orchestration.GetAllActiveRuns()
                    .Count(r => r.RunType is PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition);

                // Three-way turn tracking: 0 = issues, 1 = PRs, 2 = decomposition
                int currentTurn = 0;

                while (remaining > 0)
                {
                    if (_stopRequested || ct.IsCancellationRequested) break;

                    bool issueMadeProgress = false;
                    bool prMadeProgress = false;
                    bool decompMadeProgress = false;

                    bool hasIssues = HasEligibleIssues(pollableTemplates, issueQueues);
                    bool hasPrs = HasEligiblePrs(pollableTemplates, prQueues);
                    bool hasDecomp = HasEligibleDecomposition(pollableTemplates, decompositionQueues)
                        && activeDecompositionCount < config.MaxConcurrentDecompositions;

                    // Determine which queue to dispatch from this iteration.
                    // If the current turn's queue is empty, try the next non-empty queue.
                    int startTurn = currentTurn;
                    bool foundTurn = false;
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        int tryTurn = (startTurn + attempt) % 3;
                        if ((tryTurn == 0 && hasIssues) || (tryTurn == 1 && hasPrs) || (tryTurn == 2 && hasDecomp))
                        {
                            currentTurn = tryTurn;
                            foundTurn = true;
                            break;
                        }
                    }
                    if (!foundTurn) break; // All queues exhausted

                    // ── Issue dispatch (one per template per pass) ──
                    if (currentTurn == 0 && hasIssues)
                    {
                        var (progress, count) = await DispatchRoundAsync(pollableTemplates, async (template, stopToken) =>
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
                                    continue;
                                if (_orchestration.IsIssueBeingProcessed(candidate.Identifier))
                                    continue;
                                if (_jobDispatcher!.IsIssueBeingProcessedOrQueued(candidate.Identifier))
                                    continue;

                                if (_dependencyChecker != null)
                                {
                                    if (!_providerCache.TryGetValue(template.IssueProviderId, out var provider))
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
                                        continue;
                                    }
                                }

                                issue = candidate;
                                break;
                            }

                            if (issue is null) return DispatchAttemptResult.Skip;

                            CurrentIssueIdentifier = issue.Identifier;
                            StatusMessage = $"🔄 Dispatching #{issue.Identifier} from '{template.Name}'";
                            NotifyChange();

                            var dispatched = await _jobDispatcher!.TryDispatchAsync(
                                issue.Identifier,
                                template.IssueProviderId, template.RepoProviderId,
                                template.BrainProviderId, template.PipelineProviderId,
                                initiatedBy: "loop",
                                stopToken,
                                issueTitle: issue.Title);

                            if (dispatched)
                                _logger.Information("Dispatched issue #{Issue} from template '{Template}'",
                                    issue.Identifier, template.Name);

                            return new DispatchAttemptResult(dispatched);
                        }, remaining, stoppingToken, ct);
                        issueMadeProgress = progress;
                        remaining -= count;
                    }

                    if (_stopRequested || ct.IsCancellationRequested || remaining <= 0) break;

                    // ── PR review dispatch (one per template per pass) ──
                    if (currentTurn == 1 && hasPrs)
                    {
                        var (progress, count) = await DispatchRoundAsync(pollableTemplates, async (template, stopToken) =>
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
                                    continue;
                                if (_orchestration.IsIssueBeingProcessed(candidate.Identifier))
                                    continue;
                                if (_jobDispatcher!.IsIssueBeingProcessedOrQueued(candidate.Identifier))
                                    continue;

                                pr = candidate;
                                break;
                            }

                            if (pr is null) return DispatchAttemptResult.Skip;

                            CurrentIssueIdentifier = pr.Identifier;
                            StatusMessage = $"🔄 Dispatching PR #{pr.Identifier} review from '{template.Name}'";
                            NotifyChange();

                            var dispatched = await _jobDispatcher!.TryDispatchReviewAsync(
                                new ReviewDispatchRequest
                                {
                                    PrIdentifier = pr.Identifier,
                                    PrBranchName = pr.BranchName,
                                    PrTitle = pr.Title,
                                    PrDescription = pr.Description,
                                    PrUrl = pr.Url,
                                    PrTargetBranch = pr.TargetBranch,
                                    IssueProviderId = template.IssueProviderId,
                                    RepoProviderId = template.RepoProviderId,
                                    BrainProviderId = template.BrainProviderId,
                                    InitiatedBy = "loop"
                                },
                                stopToken);

                            if (dispatched)
                                _logger.Information("Dispatched PR #{PrIdentifier} review from template '{Template}'",
                                    pr.Identifier, template.Name);

                            return new DispatchAttemptResult(dispatched);
                        }, remaining, stoppingToken, ct);
                        prMadeProgress = progress;
                        remaining -= count;
                    }

                    if (_stopRequested || ct.IsCancellationRequested || remaining <= 0) break;

                    // ── Decomposition dispatch (one per template per pass) ──
                    if (currentTurn == 2 && hasDecomp)
                    {
                        var (progress, count) = await DispatchRoundAsync(pollableTemplates, async (template, stopToken) =>
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

                                if (_orchestration.IsIssueBeingProcessed(candidate.Issue.Identifier))
                                    continue;
                                if (_jobDispatcher!.IsIssueBeingProcessedOrQueued(candidate.Issue.Identifier))
                                    continue;

                                epic = candidate;
                                break;
                            }

                            if (epic is null) return DispatchAttemptResult.Skip;

                            var epicItem = epic.Value;
                            var phaseLabel = epicItem.Phase == PipelineRunType.DecompositionAnalysis ? "analysis" : "decomposition";

                            CurrentIssueIdentifier = epicItem.Issue.Identifier;
                            StatusMessage = $"🧩 Dispatching epic #{epicItem.Issue.Identifier} {phaseLabel} from '{template.Name}'";
                            NotifyChange();

                            var dispatched = await _jobDispatcher!.TryDispatchDecompositionAsync(
                                epicItem.Issue.Identifier,
                                epicItem.Issue.Title,
                                epicItem.Phase,
                                template.IssueProviderId,
                                template.RepoProviderId,
                                template.BrainProviderId,
                                initiatedBy: "loop",
                                stopToken);

                            if (dispatched)
                            {
                                activeDecompositionCount++;
                                _logger.Information("Dispatched epic #{EpicIdentifier} ({Phase}) from template '{Template}'",
                                    epicItem.Issue.Identifier, epicItem.Phase, template.Name);
                            }

                            return new DispatchAttemptResult(dispatched);
                        }, remaining, stoppingToken, ct);
                        decompMadeProgress = progress;
                        remaining -= count;
                    }

                    // If no queue made progress, all are exhausted
                    if (!issueMadeProgress && !prMadeProgress && !decompMadeProgress) break;

                    // Advance to next turn for fair alternation
                    currentTurn = (currentTurn + 1) % 3;
                }
            }

            CurrentIssueIdentifier = null;
            if (_stopRequested || ct.IsCancellationRequested) break;

            // Step 6: Wait before next cycle
            StatusMessage = $"🔄 Cycle complete. Polling {enabledTemplates.Count} templates every {(int)pollInterval.TotalSeconds}s.";
            NotifyChange();
            await DelayOrStop(pollInterval, ct);
        }
    }

    /// <summary>
    /// Result of a single-template dispatch attempt within <see cref="DispatchRoundAsync"/>.
    /// </summary>
    private readonly record struct DispatchAttemptResult(bool Dispatched, bool Attempted = true, bool AbortRemaining = false)
    {
        /// <summary>No candidate found for this template — skip it.</summary>
        public static readonly DispatchAttemptResult Skip = new(false, Attempted: false);

        /// <summary>Abort remaining templates (e.g., concurrency limit reached).</summary>
        public static readonly DispatchAttemptResult Abort = new(false, Attempted: false, AbortRemaining: true);
    }

    /// <summary>
    /// Shared dispatch helper that iterates templates, invokes the dispatch delegate inside
    /// a try/catch, and manages counters and progress tracking. Eliminates the triplicated
    /// dispatch pattern for issues, PRs, and decomposition.
    /// The delegate is responsible for setting <see cref="CurrentIssueIdentifier"/>,
    /// <see cref="StatusMessage"/>, and calling <see cref="NotifyChange"/> before dispatching.
    /// </summary>
    /// <returns>Whether any dispatch was attempted (progress) and how many items were consumed.</returns>
    private async Task<(bool madeProgress, int consumed)> DispatchRoundAsync(
        List<PipelineJobTemplate> pollableTemplates,
        Func<PipelineJobTemplate, CancellationToken, Task<DispatchAttemptResult>> tryDispatchOne,
        int remainingBudget,
        CancellationToken stoppingToken,
        CancellationToken ct)
    {
        bool madeProgress = false;
        int consumed = 0;

        foreach (var template in pollableTemplates)
        {
            if (remainingBudget - consumed <= 0) break;
            if (_stopRequested || ct.IsCancellationRequested) break;

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
                    CurrentIssueIdentifier, template.Name);
                FailedCount++;
                ProcessedCount++;
                consumed++;
                madeProgress = true;
                continue;
            }

            if (result.AbortRemaining) break;
            if (!result.Attempted) continue;

            if (result.Dispatched)
            {
                ProcessedCount++;
                consumed++;
                madeProgress = true;
            }
        }

        return (madeProgress, consumed);
    }

    /// <summary>
    /// Checks whether any pollable template has eligible issues remaining in its queue.
    /// Used by the fair alternation logic to allow the other queue to consume remaining budget.
    /// </summary>
    private static bool HasEligibleIssues(
        List<PipelineJobTemplate> pollableTemplates,
        Dictionary<string, List<IssueSummary>> issueQueues)
    {
        foreach (var template in pollableTemplates)
        {
            if (!template.ImplementationEnabled) continue;
            if (issueQueues.TryGetValue(template.Id, out var queue) && queue.Count > 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Checks whether any pollable template has eligible PRs remaining in its queue.
    /// Used by the fair alternation logic to allow the other queue to consume remaining budget.
    /// </summary>
    private static bool HasEligiblePrs(
        List<PipelineJobTemplate> pollableTemplates,
        Dictionary<string, List<PullRequestSummary>> prQueues)
    {
        foreach (var template in pollableTemplates)
        {
            if (!template.ReviewEnabled) continue;
            if (prQueues.TryGetValue(template.Id, out var queue) && queue.Count > 0)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Checks whether any pollable template has eligible decomposition epics remaining in its queue.
    /// Used by the fair alternation logic to allow the other queues to consume remaining budget.
    /// </summary>
    private static bool HasEligibleDecomposition(
        List<PipelineJobTemplate> pollableTemplates,
        Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>> decompositionQueues)
    {
        foreach (var template in pollableTemplates)
        {
            if (!template.DecompositionEnabled) continue;
            if (decompositionQueues.TryGetValue(template.Id, out var queue) && queue.Count > 0)
                return true;
        }
        return false;
    }

    /// <summary>Determines if an exception is an auth-related error (401/403/credential).</summary>
    private static bool IsAuthError(Exception ex)
    {
        if (ex is HttpRequestException httpEx)
        {
            var statusCode = httpEx.StatusCode;
            return statusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden;
        }
        // Check for common auth-related exception messages
        var msg = ex.Message.ToLowerInvariant();
        return msg.Contains("unauthorized") || msg.Contains("forbidden") || msg.Contains("credential");
    }

    /// <summary>Fetches all pages from a paginated API up to maxPages.</summary>
    private static async Task<List<T>> FetchAllPagesAsync<T>(
        Func<int, int, CancellationToken, Task<PagedResult<T>>> fetchPage,
        int maxPages,
        CancellationToken ct)
    {
        var result = new List<T>();
        int page = 1;
        const int pageSize = PipelineConstants.DefaultPageSize;

        while (true)
        {
            var pagedResult = await fetchPage(page, pageSize, ct);
            result.AddRange(pagedResult.Items);
            if (!pagedResult.HasMore) break;
            if (page >= maxPages) break;
            page++;
        }

        return result;
    }

    /// <summary>Fetches agent:next issues from a specific provider (used in multi-template mode).</summary>
    private async Task<List<IssueSummary>> FetchAgentNextIssuesForProviderAsync(
        IIssueProvider provider, int maxPages, CancellationToken ct)
    {
        var result = await FetchAllPagesAsync<IssueSummary>(
            (page, pageSize, token) => provider.ListOpenIssuesAsync(page, pageSize, new[] { AgentLabels.Next }, token),
            maxPages, ct);

        // FIFO: oldest first
        result.Sort((a, b) => (a.CreatedAt ?? DateTime.MaxValue).CompareTo(b.CreatedAt ?? DateTime.MaxValue));
        return result;
    }

    /// <summary>
    /// Fetches agent:next pull requests from a repository provider, filters out ineligible PRs,
    /// and orders by CreatedAt ascending (FIFO). PRs without CreatedAt sort last.
    /// </summary>
    private async Task<List<PullRequestSummary>> FetchAgentNextPullRequestsAsync(
        IRepositoryProvider repoProvider, int maxPages, CancellationToken ct)
    {
        var result = await FetchAllPagesAsync<PullRequestSummary>(
            (page, pageSize, token) => repoProvider.ListOpenPullRequestsAsync(page, pageSize, new[] { AgentLabels.Next }, token),
            maxPages, ct);

        // Filter: skip PRs with terminal/in-progress status labels
        result.RemoveAll(pr =>
            pr.Labels.Contains(AgentLabels.Error) ||
            pr.Labels.Contains(AgentLabels.InProgress) ||
            pr.Labels.Contains(AgentLabels.Done) ||
            pr.Labels.Contains(AgentLabels.Cancelled));

        // FIFO: oldest first, PRs without CreatedAt go last
        result.Sort((a, b) => (a.CreatedAt ?? DateTime.MaxValue).CompareTo(b.CreatedAt ?? DateTime.MaxValue));
        return result;
    }

    /// <summary>
    /// Fetches epic issues with a specific label from a provider, applies eligibility filters,
    /// and orders by CreatedAt ascending (FIFO). Used for decomposition polling.
    /// </summary>
    /// <param name="provider">The issue provider to query.</param>
    /// <param name="label">The label to filter by (agent:epic or agent:epic-approved).</param>
    /// <param name="maxPages">Maximum pages to fetch.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task<List<IssueSummary>> FetchEpicIssuesAsync(
        IIssueProvider provider, string label, int maxPages, CancellationToken ct)
    {
        var result = await FetchAllPagesAsync<IssueSummary>(
            (page, pageSize, token) => provider.ListOpenIssuesAsync(page, pageSize, new[] { label }, token),
            maxPages, ct);

        // Apply eligibility filters based on the label type:
        if (label == AgentLabels.Epic)
        {
            // Phase 1: skip if also has agent:epic-review, agent:in-progress, agent:error, or agent:done
            result.RemoveAll(issue =>
                issue.Labels.Contains(AgentLabels.EpicReview) ||
                issue.Labels.Contains(AgentLabels.InProgress) ||
                issue.Labels.Contains(AgentLabels.Error) ||
                issue.Labels.Contains(AgentLabels.Done));
        }
        else if (label == AgentLabels.EpicApproved)
        {
            // Phase 2: skip if also has agent:in-progress, agent:error, or agent:done
            result.RemoveAll(issue =>
                issue.Labels.Contains(AgentLabels.InProgress) ||
                issue.Labels.Contains(AgentLabels.Error) ||
                issue.Labels.Contains(AgentLabels.Done));
        }

        // FIFO: oldest first
        result.Sort((a, b) => (a.CreatedAt ?? DateTime.MaxValue).CompareTo(b.CreatedAt ?? DateTime.MaxValue));
        return result;
    }

    private async Task DelayOrStop(TimeSpan interval, CancellationToken ct)
    {
        try
        {
            await Task.Delay(interval, ct);
        }
        catch (OperationCanceledException) { }
    }
}
