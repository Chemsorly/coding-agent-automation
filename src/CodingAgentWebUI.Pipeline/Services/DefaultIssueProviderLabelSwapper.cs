using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Default implementation of <see cref="IIssueProviderLabelSwapper"/> for use within the Pipeline project.
/// Resolves the issue provider from config, removes all agent labels, and adds the new label.
/// Used as a fallback when no external swapper is injected.
/// </summary>
internal sealed class DefaultIssueProviderLabelSwapper : IIssueProviderLabelSwapper
{
    private readonly IProviderConfigStore _configStore;
    private readonly IProviderFactory _providerFactory;
    private readonly Serilog.ILogger _logger;

    public DefaultIssueProviderLabelSwapper(
        IProviderConfigStore configStore,
        IProviderFactory providerFactory,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _configStore = configStore;
        _providerFactory = providerFactory;
        _logger = logger;
    }

    public async Task SwapLabelAsync(
        string issueProviderConfigId,
        string issueIdentifier,
        string newLabel,
        CancellationToken ct)
    {
        // TODO: Add ArgumentNullException.ThrowIfNull for issueProviderConfigId, issueIdentifier, newLabel (review finding: nulls would be swallowed by catch block)
        try
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
                await issueProvider.RemoveLabelAsync(issueIdentifier, label, ct);

            if (!string.IsNullOrEmpty(newLabel))
                await issueProvider.AddLabelAsync(issueIdentifier, newLabel, ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "Failed to swap label to {Label} on issue {IssueIdentifier}",
                newLabel, issueIdentifier);
        }
    }
}
