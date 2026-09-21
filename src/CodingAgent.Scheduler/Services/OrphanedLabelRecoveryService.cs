using CodingAgent.Api.Client;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.LeaderElection;
using CodingAgent.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.Services;

/// <summary>
/// Background service that periodically detects and repairs two categories of label problems:
/// <list type="bullet">
///   <item><b>Orphaned issues</b> — issues still labelled <c>agent:in-progress</c> with no
///   active WorkItem. These are relabelled to <c>agent:error</c>.</item>
///   <item><b>Dual-label issues</b> — issues carrying more than one <c>agent:*</c> status label
///   simultaneously (e.g. <c>agent:in-progress</c> + <c>agent:done</c>), caused by
///   <c>AgentLabelOperations.SwapAsync</c> exhausting retries on the remove step. These are
///   resolved to a single label according to <see cref="AgentLabels.DualLabelResolutionPrecedence"/>.</item>
/// </list>
/// Runs an initial sweep after a 60-second grace period, then sweeps at a configurable
/// interval (default 30 minutes).
/// Updated in Spec 045 to use <see cref="IPipelineApiConfigClient"/> instead of direct
/// store interfaces (Req 1.2 F5).
/// Leader-gated (Spec 047 gap fix): only the leader Scheduler replica runs sweeps so that
/// multiple replicas do not redundantly hammer the issue provider API.
/// </summary>
public sealed class OrphanedLabelRecoveryService : BackgroundService
{
    private static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromSeconds(60);
    private const int MinimumSweepIntervalMinutes = 5;

    private readonly IOrchestratorRunService _runService;
    private readonly IPipelineApiConfigClient _configClient;
    private readonly IPipelineApiWorkItemClient _workItemClient;
    private readonly IProviderFactory _providerFactory;
    private readonly ILabelService _labelService;
    private readonly ILeaderGate? _leaderGate;
    private readonly ILogger _logger;
    private readonly TimeSpan _gracePeriod;

    public OrphanedLabelRecoveryService(
        IOrchestratorRunService runService,
        IPipelineApiConfigClient configClient,
        IPipelineApiWorkItemClient workItemClient,
        IProviderFactory providerFactory,
        ILabelService labelService,
        ILeaderGate? leaderGate,
        ILogger logger)
        : this(runService, configClient, workItemClient, providerFactory, labelService, leaderGate, logger, DefaultGracePeriod)
    {
    }

    /// <summary>
    /// Internal constructor for testing — allows overriding the grace period to avoid 60s real-time waits.
    /// </summary>
    internal OrphanedLabelRecoveryService(
        IOrchestratorRunService runService,
        IPipelineApiConfigClient configClient,
        IPipelineApiWorkItemClient workItemClient,
        IProviderFactory providerFactory,
        ILabelService labelService,
        ILeaderGate? leaderGate,
        ILogger logger,
        TimeSpan gracePeriod)
    {
        _runService = runService;
        _configClient = configClient;
        _workItemClient = workItemClient;
        _providerFactory = providerFactory;
        _labelService = labelService;
        _leaderGate = leaderGate;
        _logger = logger.ForContext<OrphanedLabelRecoveryService>();
        _gracePeriod = gracePeriod == default ? DefaultGracePeriod : gracePeriod;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.Information("Orphaned label recovery: waiting {GracePeriod} for agents to reconnect", _gracePeriod);
            await Task.Delay(_gracePeriod, stoppingToken);

            await RunInitialSweepAsync(stoppingToken);

            var intervalMinutes = await LoadSweepIntervalAsync(stoppingToken);
            _logger.Information("Orphaned label recovery: sweep interval set to {Interval} min", intervalMinutes);

            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await RecoverOrphanedLabelsAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex, "Orphaned label recovery sweep failed — will retry next interval");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.Information("Orphaned label recovery service stopping");
        }
    }

    /// <summary>
    /// Runs the first sweep immediately after the grace period.
    /// Wrapped in try-catch so transient failures do not kill the service permanently.
    /// </summary>
    private async Task RunInitialSweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverOrphanedLabelsAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Orphaned label recovery: initial sweep failed — will continue to periodic loop");
        }
    }

    /// <summary>
    /// Loads the sweep interval from the pipeline config, clamping to the minimum.
    /// Falls back to 30 minutes on transient config load failure.
    /// </summary>
    private async Task<int> LoadSweepIntervalAsync(CancellationToken stoppingToken)
    {
        try
        {
            var config = await _configClient.GetPipelineConfigAsync(stoppingToken);
            var intervalMinutes = Math.Max(config.OrphanedLabelSweepIntervalMinutes, MinimumSweepIntervalMinutes);
            if (intervalMinutes != config.OrphanedLabelSweepIntervalMinutes)
            {
                _logger.Warning("OrphanedLabelSweepIntervalMinutes ({Configured}) is below minimum, clamping to {Min} min",
                    config.OrphanedLabelSweepIntervalMinutes, MinimumSweepIntervalMinutes);
            }
            return intervalMinutes;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Orphaned label recovery: failed to load config — using default interval");
            return 30; // DefaultOrphanedLabelSweepIntervalMinutes
        }
    }

    /// <summary>
    /// Exposed for testing: runs a single recovery sweep synchronously without
    /// going through the BackgroundService timer. In production, sweeps are
    /// triggered by <see cref="ExecuteAsync"/>.
    /// </summary>
    internal Task SweepOnceForTestAsync(CancellationToken ct) => RecoverOrphanedLabelsAsync(ct);

    private async Task RecoverOrphanedLabelsAsync(CancellationToken ct)
    {
        // Gate check: skip when not the leader so multiple replicas don't redundantly
        // hammer the issue provider API. Null gate = unconditional (dev / single-replica).
        // Placing the check here (rather than at each call site) ensures both the initial
        // sweep and every periodic tick are gated consistently.
        if (_leaderGate is { IsLeader: false })
        {
            _logger.Debug("OrphanedLabelRecovery: skipping sweep — not the leader");
            return;
        }

        // Link LeaderToken so that in-flight label writes are cancelled immediately on
        // leadership loss, preventing the former leader from racing the new leader.
        // ILeaderGate.LeaderToken is documented as: "cancelled when leadership is lost —
        // pass this token linked with the host stop token to in-flight work."
        using var linked = _leaderGate is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, _leaderGate.LeaderToken)
            : null;
        var sweepCt = linked?.Token ?? ct;

        var templates = await _configClient.GetAllTemplatesAsync(sweepCt);
        if (templates.Count == 0)
        {
            _logger.Information("Orphaned label recovery: no templates configured, skipping");
            return;
        }

        // Deduplicate issue provider config IDs
        var issueProviderIds = templates
            .Select(t => t.IssueProviderId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .ToList();

        _logger.Information("Orphaned label recovery: scanning {Count} issue provider(s)", issueProviderIds.Count);

        var recoveredCount = 0;

        foreach (var providerConfigId in issueProviderIds)
        {
            // Track identifiers resolved by Pass 1 so Pass 2 can skip them and avoid calling
            // SwapLabelAsync twice for the same issue in a single sweep.
            // An issue carrying [agent:in-progress, agent:done] appears in both the Pass 1
            // agent:in-progress query and the Pass 2 agent:done query. Without this guard,
            // both passes would call TrySwapToDualLabelResolutionAsync for the same issue,
            // producing two back-to-back swap calls (duplicate-add + not-found-remove on the
            // second call, with unpredictable behaviour depending on the provider).
            var pass1ResolvedIdentifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                recoveredCount += await ScanProviderAsync(providerConfigId, pass1ResolvedIdentifiers, sweepCt);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Orphaned label recovery: failed to scan provider {ProviderId}", providerConfigId);
            }

            try
            {
                recoveredCount += await ScanProviderForDualLabelIssuesAsync(providerConfigId, pass1ResolvedIdentifiers, sweepCt);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Dual-label recovery: failed to scan provider {ProviderId}", providerConfigId);
            }
        }

        // TODO: recoveredCount conflates orphan recovery and dual-label resolution into a single counter.
        // Operational logs cannot distinguish between the two recovery types without additional instrumentation.
        // Consider splitting into separate counters (orphanRecoveredCount / dualLabelResolvedCount) and
        // logging them individually for better observability.
        _logger.Information("Orphaned label recovery complete: {Count} issue(s) recovered", recoveredCount);
    }

    private async Task<int> ScanProviderAsync(string providerConfigId, HashSet<string> resolvedIdentifiers, CancellationToken ct)
    {
        var allProviders = await _configClient.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, ct);
        var providerConfig = allProviders.FirstOrDefault(p => p.Id == providerConfigId);
        if (providerConfig is null)
        {
            _logger.Warning("Orphaned label recovery: provider config {ProviderId} not found", providerConfigId);
            return 0;
        }

        await using var issueProvider = _providerFactory.CreateIssueProvider(providerConfig);

        var recovered = 0;
        var page = 1;
        const int pageSize = 100;
        var labels = new[] { AgentLabels.InProgress };

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var result = await issueProvider.ListOpenIssuesAsync(page, pageSize, labels, ct);

            foreach (var issue in result.Items)
            {
                if (!_runService.IsIssueBeingProcessed(issue.Identifier, providerConfigId)
                    && await TryRecoverSingleIssueAsync(issue, issueProvider, providerConfigId, ct))
                {
                    recovered++;
                    // Record every issue resolved (either as orphan or dual-label) so that
                    // Pass 2 can skip it and avoid a double-swap within the same sweep.
                    resolvedIdentifiers.Add(issue.Identifier);
                }
            }

            if (!result.HasMore)
                break;

            page++;
        }

        return recovered;
    }

    private async Task<bool> TryRecoverSingleIssueAsync(
        IssueSummary issue, IIssueProvider issueProvider, string providerConfigId, CancellationToken ct)
    {
        // Defense 2: cheap in-memory grace-period check before any API calls.
        if (_runService.WasRecentlyCompleted(issue.Identifier, providerConfigId))
        {
            _logger.Debug("Orphaned label recovery: issue {Identifier} completed recently, skipping", issue.Identifier);
            return false;
        }

        // Defense 3: authoritative WorkItem check — skip if a live agent is running.
        try
        {
            if (await _workItemClient.IsIssueDistributedAsync(issue.Identifier, providerConfigId, ct))
            {
                _logger.Debug("Orphaned label recovery: issue {Identifier} has an active WorkItem, skipping", issue.Identifier);
                return false;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "Orphaned label recovery: IsIssueDistributed check failed for issue {Identifier}, skipping to avoid false-positive",
                issue.Identifier);
            return false;
        }

        // Defense 1: re-fetch current labels — ListOpenIssuesAsync result may be stale.
        IssueDetail currentIssue;
        try
        {
            currentIssue = await issueProvider.GetIssueAsync(issue.Identifier, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Orphaned label recovery: failed to fetch current labels for issue {Identifier}, skipping", issue.Identifier);
            return false;
        }

        // Pass 1 — dual-label check: if the issue has multiple non-generated agent:* labels,
        // route to the dual-label resolver instead of the orphan resolver.
        var agentLabels = currentIssue.Labels
            .Where(l => AgentLabels.DualLabelResolutionPrecedence.Contains(l, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (agentLabels.Count >= 2)
        {
            _logger.Information(
                "Dual-label recovery (pass 1): issue {Identifier} on provider {ProviderId} has {Count} agent labels [{Labels}] — resolving",
                issue.Identifier, providerConfigId, agentLabels.Count, string.Join(", ", agentLabels));
            return await TryResolveDualLabelIssueAsync(currentIssue, issue, providerConfigId, ct);
        }

        // Standard orphan check: issue should still have agent:in-progress and no terminal label.
        if (AgentLabels.TerminalLabels.Any(tl => currentIssue.Labels.Contains(tl, StringComparer.OrdinalIgnoreCase)))
        {
            _logger.Debug("Orphaned label recovery: issue {Identifier} already has terminal label, skipping", issue.Identifier);
            return false;
        }

        // Genuinely orphaned — swap to agent:error.
        _logger.Information(
            "Orphaned label recovery: issue {Identifier} on provider {ProviderId} is orphaned — swapping to agent:error",
            issue.Identifier, providerConfigId);
        return await TrySwapToErrorAsync(issue, providerConfigId, ct);
    }

    private async Task<bool> TrySwapToErrorAsync(
        IssueSummary issue, string providerConfigId, CancellationToken ct)
    {
        await _labelService.TrySwapLabelAsync(
            providerConfigId, issue.Identifier, AgentLabels.Error, LabelTargetKind.Issue,
            _logger, "OrphanedLabelRecovery.TrySwapToErrorAsync", ct);
        // TrySwapLabelAsync swallows non-OCE exceptions and logs Warning on failure.
        // Either the swap succeeded or it failed non-fatally — both are treated as "attempted".
        // Note: recoveredCount semantics have shifted from "confirmed success" to "attempted";
        // the Warning log from TrySwapLabelAsync covers the failure case for diagnostics.
        // TODO: [WARNING] The bool return value is now always true regardless of outcome (non-OCE exceptions
        // are swallowed; OCE propagates before reaching return true). The return value no longer distinguishes
        // "swap succeeded" from "swap failed non-fatally", which undermines the original contract. Callers
        // that inspect the bool to determine success will always see true. Consider changing the return type
        // to void/Task if the value is no longer meaningful, or document the "attempted" semantics explicitly
        // in callers that previously relied on the false-on-failure path.
        return true;
    }

    // ── Dual-label sweep (Pass 2) ─────────────────────────────────────────

    /// <summary>
    /// Pass 2 of the dual-label sweep: queries issues with <c>agent:done</c> (terminal issues
    /// that won't surface in the orphan pass) and resolves any that carry an additional
    /// <c>agent:*</c> status label alongside it.
    /// </summary>
    // TODO: Pass 2 only queries agent:done issues. Dual-label combinations that include neither
    // agent:in-progress nor agent:done (e.g. agent:error + agent:needs-refinement, agent:next + agent:error)
    // will appear in neither Pass 1 nor Pass 2 and will never be detected by the sweep. These cases are
    // uncommon (they require a swap where both add and remove target non-in-progress/done labels), but they
    // are structurally possible. Consider adding additional targeted passes or a broader scan to cover them.
    private async Task<int> ScanProviderForDualLabelIssuesAsync(string providerConfigId, HashSet<string> pass1ResolvedIdentifiers, CancellationToken ct)
    {
        var allProviders = await _configClient.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, ct);
        var providerConfig = allProviders.FirstOrDefault(p => p.Id == providerConfigId);
        if (providerConfig is null)
        {
            _logger.Warning("Dual-label recovery: provider config {ProviderId} not found", providerConfigId);
            return 0;
        }

        await using var issueProvider = _providerFactory.CreateIssueProvider(providerConfig);

        var resolved = 0;
        var page = 1;
        const int pageSize = 100;
        var labels = new[] { AgentLabels.Done };

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var result = await issueProvider.ListOpenIssuesAsync(page, pageSize, labels, ct);

            foreach (var issue in result.Items)
            {
                // Skip issues already resolved by Pass 1 in this sweep.
                // An issue carrying [agent:in-progress, agent:done] appears in both the Pass 1
                // agent:in-progress query and this Pass 2 agent:done query. Pass 1 runs first and
                // records resolved identifiers; skipping here prevents a double-swap (two back-to-back
                // SwapLabelAsync calls) for the same issue within a single sweep.
                if (pass1ResolvedIdentifiers.Contains(issue.Identifier))
                {
                    _logger.Debug(
                        "Dual-label recovery: issue {Identifier} already resolved by Pass 1 in this sweep, skipping",
                        issue.Identifier);
                    continue;
                }

                // TODO: Defense 2 (WasRecentlyCompleted) is intentionally omitted here — Pass 2 queries
                // agent:done issues, which are already terminal and will not be in the recent-completion
                // grace window in normal operation. However, for consistency with the defense-in-depth
                // pattern applied in Pass 1, consider adding the cheap in-memory WasRecentlyCompleted check
                // here to avoid the GetIssueAsync round-trip for issues in the grace period.

                // Skip if a live agent is actively processing this issue (Defense 3).
                // Fail-safe: skip on API error to avoid interfering with a legitimate run.
                bool isDistributed;
                try
                {
                    isDistributed = await _workItemClient.IsIssueDistributedAsync(issue.Identifier, providerConfigId, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.Warning(ex,
                        "Dual-label recovery: IsIssueDistributed check failed for issue {Identifier}, skipping",
                        issue.Identifier);
                    continue;
                }

                if (isDistributed)
                {
                    _logger.Debug("Dual-label recovery: issue {Identifier} has an active WorkItem, skipping", issue.Identifier);
                    continue;
                }

                // Defense 1: re-fetch to avoid acting on stale list data.
                // TODO: Every agent:done issue returned by ListOpenIssuesAsync incurs a GetIssueAsync
                // call even if it only has a single label (the common steady-state for completed work).
                // TryResolveDualLabelIssueAsync does guard with agentLabels.Count < 2, but the API
                // round-trip happens unconditionally. Under load (many agent:done issues), this generates
                // avoidable API traffic. Consider pre-filtering in the list result using the summary labels
                // before calling GetIssueAsync: skip if the summary shows only one agent:* label
                // (accepting that this is a best-effort optimisation subject to stale list data).
                IssueDetail currentIssue;
                try
                {
                    currentIssue = await issueProvider.GetIssueAsync(issue.Identifier, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Dual-label recovery: failed to fetch current labels for issue {Identifier}, skipping", issue.Identifier);
                    continue;
                }

                if (await TryResolveDualLabelIssueAsync(currentIssue, issue, providerConfigId, ct))
                    resolved++;
            }

            if (!result.HasMore)
                break;

            page++;
        }

        return resolved;
    }

    /// <summary>
    /// Resolves a dual-label issue by determining which label to keep (via
    /// <see cref="AgentLabels.DualLabelResolutionPrecedence"/>) and swapping to it.
    /// Returns <c>false</c> if the issue has fewer than 2 qualifying labels (idempotent).
    /// </summary>
    /// <param name="currentIssue">Re-fetched issue detail (not the stale list result).</param>
    /// <param name="issueSummary">Original summary used for the swap call identifier.</param>
    /// <param name="providerConfigId">Provider the issue belongs to.</param>
    /// <param name="ct">Cancellation token.</param>
    // TODO: This method uses issueSummary.Identifier for the SwapLabelAsync call rather than
    // currentIssue.Identifier. Both should be identical in practice, but if a provider ever returns
    // a different identifier between list and detail endpoints (e.g. due to a mapping quirk), the swap
    // would target the stale list identifier rather than the authoritative detail identifier. Consider
    // using currentIssue.Identifier in the TrySwapToDualLabelResolutionAsync call to make the re-fetch
    // authoritative end-to-end. Pass 2 (ScanProviderForDualLabelIssuesAsync) is more at risk than
    // Pass 1 because Pass 2's issue variable comes from a separate list query with no shared ancestry
    // with currentIssue.
    private async Task<bool> TryResolveDualLabelIssueAsync(
        IssueDetail currentIssue, IssueSummary issueSummary, string providerConfigId, CancellationToken ct)
    {
        // Collect agent:* labels present on this issue, excluding agent:generated (orthogonal).
        var agentLabels = currentIssue.Labels
            .Where(l => AgentLabels.DualLabelResolutionPrecedence.Contains(l, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (agentLabels.Count < 2)
            return false; // Already a single label — idempotent.

        // Find the label with the highest precedence (lowest index in DualLabelResolutionPrecedence).
        var labelToKeep = AgentLabels.DualLabelResolutionPrecedence
            .FirstOrDefault(p => agentLabels.Contains(p, StringComparer.OrdinalIgnoreCase));

        if (labelToKeep is null)
            return false; // Defensive: no matching precedence entry.

        _logger.Information(
            "Dual-label recovery: issue {Identifier} on provider {ProviderId} has labels [{Labels}] — keeping {Keep}",
            currentIssue.Identifier, providerConfigId,
            string.Join(", ", agentLabels), labelToKeep);

        // TODO: TrySwapToDualLabelResolutionAsync calls SwapLabelAsync (add-before-remove). When the
        // winning label (labelToKeep) is already present on the issue (e.g. agent:done in the common
        // [agent:in-progress, agent:done] case), the add step may be a no-op or may throw a 422 from the
        // provider API depending on how SwapLabelAsync handles duplicate-add. If the provider throws on
        // duplicate-add, the catch block in TrySwapToDualLabelResolutionAsync logs a warning and returns
        // false, leaving the issue unresolved until the next sweep. Verify that the IIssueProvider
        // implementation (e.g. GitHub) treats adding an already-present label as a no-op rather than an error.
        return await TrySwapToDualLabelResolutionAsync(issueSummary, providerConfigId, labelToKeep, ct);
    }

    /// <summary>
    /// Swaps the issue's label to <paramref name="labelToKeep"/>, removing all other agent labels.
    /// Uses <see cref="LabelServiceExtensions.TrySwapLabelAsync"/> so that non-OCE exceptions are
    /// swallowed (logged as Warning) rather than propagated.
    /// Note: <c>recoveredCount</c> increments on "attempted" (not confirmed success), consistent
    /// with <see cref="TrySwapToErrorAsync"/>. The Warning log covers the failure case.
    /// </summary>
    // TODO: This method calls SwapLabelAsync (add-before-remove), the same operation that caused the
    // original dual-label state. If the GitHub API is transiently unavailable during the remove step,
    // SwapAsync will exhaust retries and silently swallow the error (throwOnRemoveExhaustion: false),
    // leaving the issue in a dual-label state again (potentially with a different pair of labels).
    // The sweep will retry on the next tick, but a prolonged API outage causes indefinite oscillation.
    // This is a known failure mode that cannot be avoided without a different removal API. The sweep's
    // retry-on-next-tick behaviour is the intended recovery path.
    private async Task<bool> TrySwapToDualLabelResolutionAsync(
        IssueSummary issue, string providerConfigId, string labelToKeep, CancellationToken ct)
    {
        await _labelService.TrySwapLabelAsync(
            providerConfigId, issue.Identifier, labelToKeep, LabelTargetKind.Issue,
            _logger, "OrphanedLabelRecovery.TrySwapToDualLabelResolutionAsync", ct);
        // TrySwapLabelAsync swallows non-OCE exceptions and logs Warning on failure.
        // Either the swap succeeded or it failed non-fatally — both are treated as "attempted".
        return true;
    }
}
