using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Background service that periodically detects orphaned issues still labelled
/// <c>agent:in-progress</c> that are not tracked by <see cref="OrchestratorRunService"/>.
/// Such issues are relabelled to <c>agent:error</c>.
/// Runs an initial sweep after a 60-second grace period, then sweeps at a configurable
/// interval (default 30 minutes).
/// Updated in Spec 045 to use <see cref="IPipelineApiConfigClient"/> instead of direct
/// store interfaces (Req 1.2 F5).
/// </summary>
public sealed class OrphanedLabelRecoveryService : BackgroundService
{
    private static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromSeconds(60);
    private const int MinimumSweepIntervalMinutes = 5;

    private readonly IOrchestratorRunService _runService;
    private readonly IPipelineApiConfigClient _configClient;
    private readonly IProviderFactory _providerFactory;
    private readonly ILabelService _labelService;
    private readonly ILogger _logger;
    private readonly TimeSpan _gracePeriod;

    public OrphanedLabelRecoveryService(
        IOrchestratorRunService runService,
        IPipelineApiConfigClient configClient,
        IProviderFactory providerFactory,
        ILabelService labelService,
        ILogger logger)
        : this(runService, configClient, providerFactory, labelService, logger, DefaultGracePeriod)
    {
    }

    /// <summary>
    /// Internal constructor for testing — allows overriding the grace period to avoid 60s real-time waits.
    /// </summary>
    internal OrphanedLabelRecoveryService(
        IOrchestratorRunService runService,
        IPipelineApiConfigClient configClient,
        IProviderFactory providerFactory,
        ILabelService labelService,
        ILogger logger,
        TimeSpan gracePeriod)
    {
        _runService = runService;
        _configClient = configClient;
        _providerFactory = providerFactory;
        _labelService = labelService;
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

    private async Task RecoverOrphanedLabelsAsync(CancellationToken ct)
    {
        var templates = await _configClient.GetAllTemplatesAsync(ct);
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
            try
            {
                recoveredCount += await ScanProviderAsync(providerConfigId, ct);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Orphaned label recovery: failed to scan provider {ProviderId}", providerConfigId);
            }
        }

        _logger.Information("Orphaned label recovery complete: {Count} issue(s) recovered", recoveredCount);
    }

    private async Task<int> ScanProviderAsync(string providerConfigId, CancellationToken ct)
    {
        var allProviders = await _configClient.GetProviderConfigsAsync(ProviderKind.Issue, ct);
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
                    recovered++;
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
        if (!await IsOrphanedIssueAsync(issue, issueProvider, providerConfigId, ct))
            return false;

        // Genuinely orphaned — swap to agent:error
        _logger.Information(
            "Orphaned label recovery: issue {Identifier} on provider {ProviderId} is orphaned — swapping to agent:error",
            issue.Identifier, providerConfigId);

        return await TrySwapToErrorAsync(issue, providerConfigId, ct);
    }

    private async Task<bool> IsOrphanedIssueAsync(
        IssueSummary issue, IIssueProvider issueProvider, string providerConfigId, CancellationToken ct)
    {
        // Defense 2: Check if this issue had a run complete recently (grace period).
        // This is a cheap in-memory check that avoids the expensive API call below.
        if (_runService.WasRecentlyCompleted(issue.Identifier, providerConfigId))
        {
            _logger.Debug(
                "Orphaned label recovery: issue {Identifier} completed recently, skipping",
                issue.Identifier);
            return false;
        }

        // Defense 1: Re-fetch current labels to handle GitHub API eventual consistency.
        // The ListOpenIssuesAsync result may be stale — the label may have already
        // been swapped to a terminal state (agent:done, agent:error, etc.)
        IssueDetail currentIssue;
        try
        {
            currentIssue = await issueProvider.GetIssueAsync(issue.Identifier, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // TODO: Consider adding a retry with backoff for transient failures (rate limiting, 5xx)
            _logger.Warning(ex, "Orphaned label recovery: failed to fetch current labels for issue {Identifier}, skipping", issue.Identifier);
            return false;
        }

        if (AgentLabels.TerminalLabels.Any(tl => currentIssue.Labels.Contains(tl)))
        {
            _logger.Debug(
                "Orphaned label recovery: issue {Identifier} already has terminal label, skipping",
                issue.Identifier);
            return false;
        }

        return true;
    }

    private async Task<bool> TrySwapToErrorAsync(
        IssueSummary issue, string providerConfigId, CancellationToken ct)
    {
        try
        {
            await _labelService.SwapLabelAsync(
                providerConfigId, issue.Identifier, AgentLabels.Error, LabelTargetKind.Issue, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Orphaned label recovery: failed to swap label for issue {Identifier}", issue.Identifier);
            return false;
        }
    }
}
