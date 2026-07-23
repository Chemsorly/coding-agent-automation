using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration;

/// <summary>
/// Unified label management service that routes label operations to the correct provider
/// based on <see cref="LabelTargetKind"/>.
/// All operations are best-effort (failures are caught and logged as warnings).
/// </summary>
public sealed class LabelService : ILabelService
{
    private readonly IProviderConfigStore _configStore;
    private readonly IProviderFactory _providerFactory;
    private readonly ILogger _logger;

    public LabelService(
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
        ProviderConfigId providerConfigId,
        IssueIdentifier identifier,
        string newLabel,
        LabelTargetKind targetKind,
        CancellationToken ct)
    {
        await SwapLabelAsync(providerConfigId, identifier, newLabel, targetKind, expectedCurrentLabel: null, ct);
    }

    /// <inheritdoc />
    public async Task SwapLabelAsync(
        ProviderConfigId providerConfigId,
        IssueIdentifier identifier,
        string newLabel,
        LabelTargetKind targetKind,
        string? expectedCurrentLabel,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(identifier.Value);
        ArgumentNullException.ThrowIfNull(newLabel);
        // TODO: Validate providerConfigId.Value is not null/empty. The previous string parameter
        // had ArgumentNullException.ThrowIfNull(providerConfigId) which is now lost because structs
        // can't be null, but default(ProviderConfigId) with Value = null can still flow through.

        // Validate the transition if the caller provides the expected current label.
        // This is observational only — invalid transitions log a warning but do NOT block.
        // TODO: Align guard condition with AgentLabelOperations.SwapAsync which also checks
        //       !string.IsNullOrEmpty(newLabel). Current divergence is benign (IsValidTransition
        //       returns true for empty targets) but may confuse future maintainers.
        // TODO: Consider that downstream AgentLabelOperations.SwapAsync does not receive
        //       expectedCurrentLabel from SwapIssueLabelAsync/SwapPrLabelAsync callers — validation
        //       only happens here. If the entry point changes, this could mask issues.
        if (expectedCurrentLabel is not null)
        {
            LabelStateMachine.ValidateTransition(expectedCurrentLabel, newLabel, identifier);
        }

        _logger.Information(
            "Label swap: {Identifier} → {NewLabel} (target={TargetKind}, provider={ProviderConfigId})",
            identifier, newLabel, targetKind, providerConfigId.Value);

        try
        {
            switch (targetKind)
            {
                case LabelTargetKind.Issue:
                    await SwapIssueLabelAsync(providerConfigId.Value, identifier, newLabel, ct);
                    break;

                case LabelTargetKind.PullRequest:
                    await SwapPrLabelAsync(providerConfigId.Value, identifier, newLabel, ct);
                    break;

                default:
                    _logger.Warning("Unknown LabelTargetKind {TargetKind}, skipping label swap", targetKind);
                    break;
            }

            _logger.Debug("Label swap completed: {Identifier} → {NewLabel}", identifier, newLabel);
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
        ProviderConfigId providerConfigId,
        LabelTargetKind targetKind,
        CancellationToken ct)
    {

        try
        {
            switch (targetKind)
            {
                case LabelTargetKind.Issue:
                {
                    var issueConfig = await _configStore.GetProviderConfigByIdAsync(providerConfigId.Value, ProviderKind.Issue, ct);
                    if (issueConfig is null)
                    {
                        _logger.Warning(
                            "Issue provider config '{ConfigId}' not found for EnsureAgentLabelsAsync",
                            providerConfigId.Value);
                        return false;
                    }

                    await using var issueProvider = _providerFactory.CreateIssueProvider(issueConfig);
                    return await issueProvider.EnsureAgentLabelsAsync(ct);
                }

                case LabelTargetKind.PullRequest:
                {
                    var repoConfig = await _configStore.GetProviderConfigByIdAsync(providerConfigId.Value, ProviderKind.Repository, ct);
                    if (repoConfig is null)
                    {
                        _logger.Warning(
                            "Repository provider config '{ConfigId}' not found for EnsureAgentLabelsAsync (PR)",
                            providerConfigId.Value);
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
                targetKind, providerConfigId.Value);
            return false;
        }
    }

    /// <summary>
    /// Swaps labels on an issue via IIssueProvider.
    /// </summary>
    private async Task SwapIssueLabelAsync(
        string issueProviderConfigId,
        string issueIdentifier,
        string newLabel,
        CancellationToken ct)
    {
        var issueConfig = await _configStore.GetProviderConfigByIdAsync(issueProviderConfigId, ProviderKind.Issue, ct);
        if (issueConfig is null)
        {
            _logger.Warning(
                "Issue provider config '{ConfigId}' not found, skipping label swap for issue {IssueIdentifier}",
                issueProviderConfigId, issueIdentifier);
            return;
        }

        await using var issueProvider = _providerFactory.CreateIssueProvider(issueConfig);

        await AgentLabelOperations.SwapAsync(
            (label, c) => issueProvider.RemoveLabelAsync(issueIdentifier, label, c),
            (label, c) => issueProvider.AddLabelAsync(issueIdentifier, label, c),
            newLabel,
            ct);
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
        var repoConfig = await _configStore.GetProviderConfigByIdAsync(repoProviderConfigId, ProviderKind.Repository, ct);
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

        await AgentLabelOperations.SwapAsync(
            (label, c) => repoProvider.RemovePrLabelAsync(prNumber, label, c),
            (label, c) => repoProvider.AddPrLabelAsync(prNumber, label, c),
            newLabel,
            ct);
    }
}
