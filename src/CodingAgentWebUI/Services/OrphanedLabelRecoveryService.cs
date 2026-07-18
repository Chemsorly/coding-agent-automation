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
/// </summary>
public sealed class OrphanedLabelRecoveryService : BackgroundService
{
    private static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromSeconds(60);
    private const int MinimumSweepIntervalMinutes = 5;

    private readonly IOrchestratorRunService _runService;
    private readonly IProjectStore _projectStore;
    private readonly IProviderConfigStore _providerConfigStore;
    private readonly IProviderFactory _providerFactory;
    private readonly ILabelService _labelService;
    private readonly IPipelineConfigStore _configStore;
    private readonly ILogger _logger;
    private readonly TimeSpan _gracePeriod;

    public OrphanedLabelRecoveryService(
        IOrchestratorRunService runService,
        IProjectStore projectStore,
        IProviderConfigStore providerConfigStore,
        IProviderFactory providerFactory,
        ILabelService labelService,
        IPipelineConfigStore configStore,
        ILogger logger)
        : this(runService, projectStore, providerConfigStore, providerFactory, labelService, configStore, logger, DefaultGracePeriod)
    {
    }

    /// <summary>
    /// Internal constructor for testing — allows overriding the grace period to avoid 60s real-time waits.
    /// </summary>
    internal OrphanedLabelRecoveryService(
        IOrchestratorRunService runService,
        IProjectStore projectStore,
        IProviderConfigStore providerConfigStore,
        IProviderFactory providerFactory,
        ILabelService labelService,
        IPipelineConfigStore configStore,
        ILogger logger,
        TimeSpan gracePeriod)
    {
        _runService = runService;
        _projectStore = projectStore;
        _providerConfigStore = providerConfigStore;
        _providerFactory = providerFactory;
        _labelService = labelService;
        _configStore = configStore;
        _logger = logger.ForContext<OrphanedLabelRecoveryService>();
        _gracePeriod = gracePeriod;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.Information("Orphaned label recovery: waiting {GracePeriod} for agents to reconnect", _gracePeriod);
            await Task.Delay(_gracePeriod, stoppingToken);

            // First sweep immediately after grace period — wrapped in try-catch so transient
            // failures (DB timeout, provider unavailable) don't kill the service permanently.
            try
            {
                await RecoverOrphanedLabelsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "Orphaned label recovery: initial sweep failed — will continue to periodic loop");
            }

            // Load config for interval — also wrapped so a transient config load failure
            // falls back to the default interval rather than killing the service.
            int intervalMinutes;
            try
            {
                var config = await _configStore.LoadPipelineConfigAsync(stoppingToken);
                intervalMinutes = Math.Max(config.OrphanedLabelSweepIntervalMinutes, MinimumSweepIntervalMinutes);
                if (intervalMinutes != config.OrphanedLabelSweepIntervalMinutes)
                {
                    _logger.Warning("OrphanedLabelSweepIntervalMinutes ({Configured}) is below minimum, clamping to {Min} min",
                        config.OrphanedLabelSweepIntervalMinutes, MinimumSweepIntervalMinutes);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "Orphaned label recovery: failed to load config — using default interval");
                intervalMinutes = 30; // DefaultOrphanedLabelSweepIntervalMinutes
            }
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

    private async Task RecoverOrphanedLabelsAsync(CancellationToken ct)
    {
        var templates = await _projectStore.LoadAllTemplatesAsync(ct);
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
        var providerConfig = await _providerConfigStore.GetProviderConfigByIdAsync(providerConfigId, ProviderKind.Issue, ct);
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
                if (!_runService.IsIssueBeingProcessed(issue.Identifier, providerConfigId))
                {
                    _logger.Information(
                        "Orphaned label recovery: issue {Identifier} on provider {ProviderId} is orphaned — swapping to agent:error",
                        issue.Identifier, providerConfigId);

                    try
                    {
                        await _labelService.SwapLabelAsync(
                            providerConfigId, issue.Identifier, AgentLabels.Error, LabelTargetKind.Issue, ct);
                        recovered++;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Orphaned label recovery: failed to swap label for issue {Identifier}", issue.Identifier);
                    }
                }
            }

            if (!result.HasMore)
                break;

            page++;
        }

        return recovered;
    }
}
