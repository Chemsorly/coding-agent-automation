using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration;

/// <summary>
/// Unified label management service that routes label operations to the correct provider
/// based on <see cref="LabelTargetKind"/>. Supersedes <see cref="IssueProviderLabelSwapper"/>
/// by adding PR label support while preserving identical issue label behavior.
/// All operations are best-effort (failures are caught and logged as warnings).
/// </summary>
public sealed class LabelSwapper : ILabelSwapper
{
    private readonly IProviderConfigStore _configStore;
    private readonly IProviderFactory _providerFactory;
    private readonly ILogger _logger;

    public LabelSwapper(
        IProviderConfigStore configStore,
        IProviderFactory providerFactory,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _configStore = configStore;
        _providerFactory = providerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task SwapLabelAsync(
        string providerConfigId,
        string identifier,
        string newLabel,
        LabelTargetKind targetKind,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(providerConfigId);
        ArgumentNullException.ThrowIfNull(identifier);
        ArgumentNullException.ThrowIfNull(newLabel);

        try
        {
            switch (targetKind)
            {
                case LabelTargetKind.Issue:
                    await SwapIssueLabelAsync(providerConfigId, identifier, newLabel, ct);
                    break;

                case LabelTargetKind.PullRequest:
                    await SwapPrLabelAsync(providerConfigId, identifier, newLabel, ct);
                    break;

                default:
                    _logger.Warning("Unknown LabelTargetKind {TargetKind}, skipping label swap", targetKind);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex,
                "Failed to swap label to {Label} on {TargetKind} {Identifier}",
                newLabel, targetKind, identifier);
        }
    }

    /// <inheritdoc />
    public async Task<bool> EnsureAgentLabelsAsync(
        string providerConfigId,
        LabelTargetKind targetKind,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(providerConfigId);

        try
        {
            switch (targetKind)
            {
                case LabelTargetKind.Issue:
                {
                    var issueConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Issue, ct);
                    var issueConfig = issueConfigs.FirstOrDefault(c => c.Id == providerConfigId);
                    if (issueConfig is null)
                    {
                        _logger.Warning(
                            "Issue provider config '{ConfigId}' not found for EnsureAgentLabelsAsync",
                            providerConfigId);
                        return false;
                    }

                    await using var issueProvider = _providerFactory.CreateIssueProvider(issueConfig);
                    return await issueProvider.EnsureAgentLabelsAsync(ct);
                }

                case LabelTargetKind.PullRequest:
                {
                    var repoConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
                    var repoConfig = repoConfigs.FirstOrDefault(c => c.Id == providerConfigId);
                    if (repoConfig is null)
                    {
                        _logger.Warning(
                            "Repository provider config '{ConfigId}' not found for EnsureAgentLabelsAsync (PR)",
                            providerConfigId);
                        return false;
                    }

                    await using var repoProvider = _providerFactory.CreateRepositoryProvider(repoConfig);
                    return await repoProvider.EnsureAgentLabelsForPullRequestsAsync(ct);
                }

                default:
                    _logger.Warning("Unknown LabelTargetKind {TargetKind} for EnsureAgentLabelsAsync", targetKind);
                    return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex,
                "Failed to ensure agent labels for {TargetKind} (config: {ConfigId})",
                targetKind, providerConfigId);
            return false;
        }
    }

    /// <summary>
    /// Swaps labels on an issue via IIssueProvider (existing behavior, identical to IssueProviderLabelSwapper).
    /// </summary>
    private async Task SwapIssueLabelAsync(
        string issueProviderConfigId,
        string issueIdentifier,
        string newLabel,
        CancellationToken ct)
    {
        var issueConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Issue, ct);
        var issueConfig = issueConfigs.FirstOrDefault(c => c.Id == issueProviderConfigId);
        if (issueConfig is null)
        {
            _logger.Warning(
                "Issue provider config '{ConfigId}' not found, skipping label swap for issue {IssueIdentifier}",
                issueProviderConfigId, issueIdentifier);
            return;
        }

        await using var issueProvider = _providerFactory.CreateIssueProvider(issueConfig);

        foreach (var label in AgentLabels.All)
        {
            // Skip removing the label we're about to add — avoids a redundant remove+add
            // that shows up as a confusing "added X and removed X" event on GitHub.
            if (string.Equals(label, newLabel, StringComparison.Ordinal))
                continue;
            await issueProvider.RemoveLabelAsync(issueIdentifier, label, ct);
        }

        if (!string.IsNullOrEmpty(newLabel))
            await issueProvider.AddLabelAsync(issueIdentifier, newLabel, ct);
    }

    /// <summary>
    /// Swaps labels on a pull request via IRepositoryProvider.
    /// </summary>
    private async Task SwapPrLabelAsync(
        string repoProviderConfigId,
        string prIdentifier,
        string newLabel,
        CancellationToken ct)
    {
        var repoConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
        var repoConfig = repoConfigs.FirstOrDefault(c => c.Id == repoProviderConfigId);
        if (repoConfig is null)
        {
            _logger.Warning(
                "Repository provider config '{ConfigId}' not found, skipping label swap for PR {PrIdentifier}",
                repoProviderConfigId, prIdentifier);
            return;
        }

        if (!int.TryParse(prIdentifier, out var prNumber))
        {
            _logger.Warning(
                "PR identifier '{PrIdentifier}' is not a valid integer, skipping label swap",
                prIdentifier);
            return;
        }

        await using var repoProvider = _providerFactory.CreateRepositoryProvider(repoConfig);

        foreach (var label in AgentLabels.All)
        {
            // Skip removing the label we're about to add — avoids a redundant remove+add
            // that shows up as a confusing "added X and removed X" event on GitHub.
            if (string.Equals(label, newLabel, StringComparison.Ordinal))
                continue;
            await repoProvider.RemovePrLabelAsync(prNumber, label, ct);
        }

        if (!string.IsNullOrEmpty(newLabel))
            await repoProvider.AddPrLabelAsync(prNumber, newLabel, ct);
    }
}
