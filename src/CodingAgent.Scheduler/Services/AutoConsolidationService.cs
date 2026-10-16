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
/// refactoring scan, harness suggestions) when their respective staleness counters reach the configured
/// thresholds. All three triggers default to 0 (off) — nothing changes until an operator sets a threshold.
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

        // Fast exit: all three triggers are off.
        if (config.AutoBrainConsolidationAfterRuns == 0
            && config.AutoHarnessSuggestionsAfterFeedback == 0
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
                var brainId = group.Key;
                var scopeKey = ConsolidationService.ScopeKey(ConsolidationRunType.BrainConsolidation, brainId, null);

                try
                {
                    if (await _staleness.IsPausedAsync(scopeKey, ct))
                    {
                        _logger.Information(
                            "Auto consolidation: {Type} for {Scope} is paused because its last run failed; a successful run resumes it",
                            ConsolidationRunType.BrainConsolidation, scopeKey);
                        continue;
                    }

                    var count = await _staleness.CountBrainRunsSinceLastConsolidationAsync(brainId, ct);
                    if (count >= brainThreshold)
                    {
                        _logger.Information(
                            "Auto consolidation: starting {Type} for {Scope} ({Count} since the last run, threshold {Threshold})",
                            ConsolidationRunType.BrainConsolidation, scopeKey, count, brainThreshold);

                        var firstTemplate = group.First().Template;
                        var result = await _consolidation.TriggerAutomaticAsync(
                            ConsolidationRunType.BrainConsolidation, new TemplateId(firstTemplate.Id), ct);

                        if (result is null)
                        {
                            _logger.Debug(
                                "Auto consolidation: {Type} for {Scope} was not started (already running or rejected)",
                                ConsolidationRunType.BrainConsolidation, scopeKey);
                        }
                    }
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
        }

        // ── Refactoring scan ───────────────────────────────────────────────
        if (config.AutoRefactoringScanAfterClosedIssues > 0)
        {
            var refactoringThreshold = config.AutoRefactoringScanAfterClosedIssues;

            foreach (var (template, project) in orderedTemplatesWithProject
                .Where(pair => ConsolidationTemplateFilter.SupportsRefactoringDetection(pair.Template)))
            {
                var scopeKey = ConsolidationService.ScopeKey(ConsolidationRunType.RefactoringDetection, null, template.Id);

                try
                {
                    if (await _staleness.IsPausedAsync(scopeKey, ct))
                    {
                        _logger.Information(
                            "Auto consolidation: {Type} for {Scope} is paused because its last run failed; a successful run resumes it",
                            ConsolidationRunType.RefactoringDetection, scopeKey);
                        continue;
                    }

                    var limit = project?.MaxRefactoringProposals ?? config.MaxRefactoringProposals;
                    var openCount = await _staleness.CountOpenScanIssuesAsync(template, ct);
                    if (openCount >= limit)
                    {
                        _logger.Information(
                            "Auto consolidation: refactoring scan for {Scope} skipped, {Open} scan issues are still open (limit {Limit})",
                            scopeKey, openCount, limit);
                        continue;
                    }

                    var count = await _staleness.CountClosedIssuesSinceLastScanAsync(template, ct);
                    if (count >= refactoringThreshold)
                    {
                        _logger.Information(
                            "Auto consolidation: starting {Type} for {Scope} ({Count} since the last run, threshold {Threshold})",
                            ConsolidationRunType.RefactoringDetection, scopeKey, count, refactoringThreshold);

                        var result = await _consolidation.TriggerAutomaticAsync(
                            ConsolidationRunType.RefactoringDetection, new TemplateId(template.Id), ct);

                        if (result is null)
                        {
                            _logger.Debug(
                                "Auto consolidation: {Type} for {Scope} was not started (already running or rejected)",
                                ConsolidationRunType.RefactoringDetection, scopeKey);
                        }
                    }
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
        }

        // ── Harness suggestions ────────────────────────────────────────────
        if (config.AutoHarnessSuggestionsAfterFeedback > 0)
        {
            var harnessThreshold = config.AutoHarnessSuggestionsAfterFeedback;
            var scopeKey = ConsolidationService.ScopeKey(ConsolidationRunType.HarnessSuggestions, null, null);

            try
            {
                if (await _staleness.IsPausedAsync(scopeKey, ct))
                {
                    _logger.Information(
                        "Auto consolidation: {Type} for {Scope} is paused because its last run failed; a successful run resumes it",
                        ConsolidationRunType.HarnessSuggestions, scopeKey);
                    return;
                }

                var count = await _staleness.CountFeedbackSinceLastHarnessSuggestionsAsync(ct);
                if (count >= harnessThreshold)
                {
                    _logger.Information(
                        "Auto consolidation: starting {Type} for {Scope} ({Count} since the last run, threshold {Threshold})",
                        ConsolidationRunType.HarnessSuggestions, scopeKey, count, harnessThreshold);

                    var result = await _consolidation.TriggerAutomaticAsync(
                        ConsolidationRunType.HarnessSuggestions, null, ct);

                    if (result is null)
                    {
                        _logger.Debug(
                            "Auto consolidation: {Type} for {Scope} was not started (already running or rejected)",
                            ConsolidationRunType.HarnessSuggestions, scopeKey);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex,
                    "Auto consolidation: error checking {Type} for {Scope}",
                    ConsolidationRunType.HarnessSuggestions, scopeKey);
            }
        }
    }
}
