using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Background service that runs once on startup (after a 60-second grace period) to detect
/// orphaned issues still labelled <c>agent:in-progress</c> that are not tracked by
/// <see cref="OrchestratorRunService"/>. Such issues are relabelled to <c>agent:error</c>.
/// </summary>
public sealed class OrphanedLabelRecoveryService : BackgroundService
{
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(60);

    private readonly OrchestratorRunService _runService;
    private readonly IProjectStore _projectStore;
    private readonly IProviderConfigStore _providerConfigStore;
    private readonly IProviderFactory _providerFactory;
    private readonly ILabelSwapper _labelSwapper;
    private readonly ILogger _logger;

    public OrphanedLabelRecoveryService(
        OrchestratorRunService runService,
        IProjectStore projectStore,
        IProviderConfigStore providerConfigStore,
        IProviderFactory providerFactory,
        ILabelSwapper labelSwapper,
        ILogger logger)
    {
        _runService = runService;
        _projectStore = projectStore;
        _providerConfigStore = providerConfigStore;
        _providerFactory = providerFactory;
        _labelSwapper = labelSwapper;
        _logger = logger.ForContext<OrphanedLabelRecoveryService>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.Information("Orphaned label recovery: waiting {GracePeriod} for agents to reconnect", GracePeriod);
            await Task.Delay(GracePeriod, stoppingToken);

            await RecoverOrphanedLabelsAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.Information("Orphaned label recovery cancelled during grace period");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Orphaned label recovery failed — best-effort, continuing");
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
                        await _labelSwapper.SwapLabelAsync(
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
