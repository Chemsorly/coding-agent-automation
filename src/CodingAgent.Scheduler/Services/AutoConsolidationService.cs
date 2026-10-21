using CodingAgent.Api.Client;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.Extensions.Hosting;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.Services;

/// <summary>
/// Leader-gated background service that automatically triggers consolidation runs (brain consolidation,
/// refactoring scan) when their respective staleness counters reach the configured
/// thresholds. Both triggers default to 0 (off) — nothing changes until an operator sets a threshold.
/// </summary>
public sealed class AutoConsolidationService : BackgroundService
{
    private readonly IConsolidationService _consolidation;
    private readonly ConsolidationStalenessService _staleness;
    private readonly IPipelineConfigStore _configStore;
    private readonly IPipelineApiConfigClient _configClient;
    private readonly ILeaderGate? _leaderGate;
    private readonly ILogger _logger;
    private readonly TimeSpan _interval;

    public AutoConsolidationService(
        IConsolidationService consolidation,
        ConsolidationStalenessService staleness,
        IPipelineConfigStore configStore,
        IPipelineApiConfigClient configClient,
        ILeaderGate? leaderGate,
        ILogger logger,
        TimeSpan? interval = null)
    {
        ArgumentNullException.ThrowIfNull(consolidation);
        ArgumentNullException.ThrowIfNull(staleness);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(configClient);
        ArgumentNullException.ThrowIfNull(logger);

        _consolidation = consolidation;
        _staleness = staleness;
        _configStore = configStore;
        _configClient = configClient;
        _leaderGate = leaderGate;
        _logger = logger.ForContext<AutoConsolidationService>();
        _interval = interval ?? TimeSpan.FromMinutes(15);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.Information("AutoConsolidationService started — interval {Interval}", _interval);

        using var timer = new PeriodicTimer(_interval);

        while (true)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                    break;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_leaderGate is { IsLeader: false })
            {
                _logger.Debug("AutoConsolidationService: skipping tick — not the leader");
                continue;
            }

            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "AutoConsolidationService: unexpected error during tick — will retry next interval");
            }
        }
    }

    /// <summary>
    /// Runs a single auto-consolidation check. Exposed as <c>internal</c> so tests can invoke it directly
    /// without standing up a full <see cref="BackgroundService"/> timer loop.
    /// </summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        var config = await _configStore.LoadPipelineConfigAsync(ct);

        // Fast exit: both triggers are off.
        if (config.AutoBrainConsolidationAfterRuns == 0
            && config.AutoRefactoringScanAfterClosedIssues == 0)
            return;

        var allTemplates = await _configClient.GetAllTemplatesAsync(ct);
        var projects = await _configClient.GetProjectsAsync(ct);

        // Keep only enabled templates that belong to an enabled project, ordered:
        // projects by name (ordinal), then each project's TemplateIds insertion order.
        var enabledProjects = projects
            .Where(p => p.Enabled)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

        var templateById = allTemplates
            .Where(t => t.Enabled)
            .ToDictionary(t => t.Id, StringComparer.Ordinal);

        var orderedTemplatesWithProject = enabledProjects
            .SelectMany(p => p.TemplateIds
                .Where(templateById.ContainsKey)
                .Select(id => (Template: templateById[id], Project: p)))
            .ToList();

        // ── Brain consolidation ────────────────────────────────────────────
        if (config.AutoBrainConsolidationAfterRuns > 0)
        {
            var brainThreshold = config.AutoBrainConsolidationAfterRuns;

            // Group by BrainProviderId: all templates sharing a brain share one consolidation scope.
            var brainGroups = orderedTemplatesWithProject
                .Where(pair => ConsolidationTemplateFilter.SupportsBrainConsolidation(pair.Template)
                    && !ConsolidationTemplateFilter.IsBrainReadOnly(pair.Template, pair.Project, config))
                .GroupBy(pair => pair.Template.BrainProviderId!)
                .ToList();

            foreach (var group in brainGroups)
            {
                await TryTriggerBrainScopeAsync(group, brainThreshold, ct);
            }
        }

        // ── Refactoring scan ───────────────────────────────────────────────
        if (config.AutoRefactoringScanAfterClosedIssues > 0)
        {
            var refactoringThreshold = config.AutoRefactoringScanAfterClosedIssues;

            foreach (var (template, project) in orderedTemplatesWithProject
                .Where(pair => ConsolidationTemplateFilter.SupportsRefactoringDetection(pair.Template)))
            {
                await TryTriggerRefactoringDetectionScopeAsync(
                    template, project, config.MaxRefactoringProposals, refactoringThreshold, ct);
            }
        }
    }

    /// <summary>
    /// Checks and fires a single brain-consolidation scope. Handles per-scope staleness queries,
    /// the shared trigger skeleton, and per-scope error isolation so that one failing brain scope
    /// does not abort others in the same tick.
    /// </summary>
    private async Task TryTriggerBrainScopeAsync(
        IGrouping<string, (PipelineJobTemplate Template, PipelineProject Project)> group,
        int threshold,
        CancellationToken ct)
    {
        var brainId = group.Key;
        var scopeKey = ConsolidationService.ScopeKey(ConsolidationRunType.BrainConsolidation, brainId, null);
        try
        {
            // TODO: count is fetched before IsPausedAsync (inside TryTriggerIfThresholdReachedAsync).
            // On a paused scope this incurs one unnecessary staleness query per tick.
            // Fix: accept a count-fetching delegate in TryTriggerIfThresholdReachedAsync so
            // the pause check can short-circuit before the count is fetched.
            var count = await _staleness.CountBrainRunsSinceLastConsolidationAsync(brainId, ct);
            var templateId = new TemplateId(group.First().Template.Id);
            await TryTriggerIfThresholdReachedAsync(
                ConsolidationRunType.BrainConsolidation, scopeKey, count, threshold, templateId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "Auto consolidation: error checking {Type} for {Scope}",
                ConsolidationRunType.BrainConsolidation, scopeKey);
        }
    }

    /// <summary>
    /// Checks and fires a single refactoring-detection scope. Handles the open-proposal-limit guard,
    /// per-scope staleness queries, the shared trigger skeleton, and per-scope error isolation so that
    /// one failing template does not abort others in the same tick.
    /// </summary>
    private async Task TryTriggerRefactoringDetectionScopeAsync(
        PipelineJobTemplate template,
        PipelineProject? project,
        int maxRefactoringProposalsFallback,
        int threshold,
        CancellationToken ct)
    {
        var scopeKey = ConsolidationService.ScopeKey(ConsolidationRunType.RefactoringDetection, null, template.Id);
        try
        {
            // TODO: count queries are fetched before IsPausedAsync (inside TryTriggerIfThresholdReachedAsync).
            // On a paused scope this incurs up to two unnecessary staleness queries per tick
            // (CountOpenScanIssues + CountClosedIssuesSinceLastScan). Fix: accept a count-fetching
            // delegate in TryTriggerIfThresholdReachedAsync so the pause check can short-circuit first.
            var limit = project?.MaxRefactoringProposals ?? maxRefactoringProposalsFallback;
            var openCount = await _staleness.CountOpenScanIssuesAsync(template, ct);
            if (openCount >= limit)
            {
                _logger.Information(
                    "Auto consolidation: refactoring scan for {Scope} skipped, {Open} scan issues are still open (limit {Limit})",
                    scopeKey, openCount, limit);
                return;
            }

            var count = await _staleness.CountClosedIssuesSinceLastScanAsync(template, ct);
            await TryTriggerIfThresholdReachedAsync(
                ConsolidationRunType.RefactoringDetection, scopeKey, count, threshold,
                new TemplateId(template.Id), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "Auto consolidation: error checking {Type} for {Scope}",
                ConsolidationRunType.RefactoringDetection, scopeKey);
        }
    }

    /// <summary>
    /// Shared trigger skeleton: checks whether the scope is paused, compares <paramref name="count"/> to
    /// <paramref name="threshold"/>, and fires <see cref="IConsolidationService.TriggerAutomaticAsync"/>
    /// when the threshold is reached. Called from each of the three auto-consolidation sections in
    /// <see cref="RunOnceAsync"/>; exception handling remains in the caller's per-scope try/catch.
    /// </summary>
    private async Task TryTriggerIfThresholdReachedAsync(
        ConsolidationRunType type,
        string scopeKey,
        int count,
        int threshold,
        TemplateId? templateId,
        CancellationToken ct)
    {
        if (await _staleness.IsPausedAsync(scopeKey, ct))
        {
            _logger.Information(
                "Auto consolidation: {Type} for {Scope} is paused because its last run failed; a successful run resumes it",
                type, scopeKey);
            return;
        }

        if (count >= threshold)
        {
            _logger.Information(
                "Auto consolidation: starting {Type} for {Scope} ({Count} since the last run, threshold {Threshold})",
                type, scopeKey, count, threshold);

            var result = await _consolidation.TriggerAutomaticAsync(type, templateId, ct);

            if (result is null)
            {
                _logger.Debug(
                    "Auto consolidation: {Type} for {Scope} was not started (already running or rejected)",
                    type, scopeKey);
            }
        }
    }
}
