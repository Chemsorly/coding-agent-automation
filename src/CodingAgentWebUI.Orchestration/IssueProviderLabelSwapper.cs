using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration;

/// <summary>
/// Shared helper that encapsulates the label-swap-via-issue-provider pattern:
/// resolve config → create disposable provider → remove all agent labels → add new label.
/// All operations are best-effort (failures are caught and logged as warnings).
/// </summary>
public sealed class IssueProviderLabelSwapper : IIssueProviderLabelSwapper
{
    private readonly IProviderConfigStore _configStore;
    private readonly IProviderFactory _providerFactory;
    private readonly ILogger _logger;

    public IssueProviderLabelSwapper(
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
        string issueProviderConfigId,
        string issueIdentifier,
        string newLabel,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issueProviderConfigId);
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        ArgumentNullException.ThrowIfNull(newLabel);

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
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "Failed to swap label to {Label} on issue {IssueIdentifier}",
                newLabel, issueIdentifier);
        }
    }
}
