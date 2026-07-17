using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Encapsulates provider config resolution and token vending for agent dispatch.
/// Extracted from <see cref="AgentJobDispatcher"/> and <see cref="DispatchOrchestrationService"/>
/// to eliminate duplicated <c>BuildAgentProviderConfigsAsync</c> / <c>PrepareProviderConfigsAsync</c> logic.
/// </summary>
public sealed class ProviderConfigPreparationService
{
    private readonly IProviderConfigStore _providerConfigStore;
    private readonly ITokenVendingService _tokenVending;
    private readonly ILogger _logger;

    internal ProviderConfigPreparationService(
        IProviderConfigStore providerConfigStore,
        ITokenVendingService tokenVending,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(providerConfigStore);
        ArgumentNullException.ThrowIfNull(tokenVending);
        ArgumentNullException.ThrowIfNull(logger);

        _providerConfigStore = providerConfigStore;
        _tokenVending = tokenVending;
        _logger = logger;
    }

    /// <summary>
    /// Builds the provider configs list and prepares tokens via the token vending service.
    /// </summary>
    internal async Task<IReadOnlyList<ProviderConfig>> PrepareProviderConfigsAsync(
        string repoProviderId,
        string agentProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        CancellationToken ct,
        IEnumerable<string>? additionalRepoProviderIds = null)
    {
        var rawConfigs = await BuildAgentProviderConfigsAsync(
            repoProviderId, agentProviderId, brainProviderId, pipelineProviderId, ct, additionalRepoProviderIds);
        return await _tokenVending.PrepareAgentConfigsAsync(rawConfigs, repoProviderId, ct);
    }

    /// <summary>
    /// Builds the list of provider configs to send to the agent.
    /// Excludes issue provider configs (agents don't get issue access).
    /// </summary>
    private async Task<IReadOnlyList<ProviderConfig>> BuildAgentProviderConfigsAsync(
        string repoProviderId, string agentProviderId,
        string? brainProviderId, string? pipelineProviderId,
        CancellationToken ct,
        IEnumerable<string>? additionalRepoProviderIds = null)
    {
        var configs = new List<ProviderConfig>();

        var repoConfigs = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
        var repoConfig = await ProviderConfigResolver.ResolveAsync(
            _providerConfigStore, repoProviderId, ProviderKind.Repository, repoConfigs, required: true, _logger, ct);
        configs.Add(repoConfig!);

        // Include additional repo provider configs for cross-repo decomposition.
        // These are needed so the agent can clone secondary repos for code exploration.
        if (additionalRepoProviderIds is not null)
        {
            var addedIds = new HashSet<string> { repoProviderId }; // primary already added
            foreach (var additionalId in additionalRepoProviderIds)
            {
                if (string.IsNullOrEmpty(additionalId) || !addedIds.Add(additionalId))
                    continue; // skip null/empty or duplicates

                var additionalConfig = await ProviderConfigResolver.ResolveAsync(
                    _providerConfigStore, additionalId, ProviderKind.Repository, repoConfigs, required: false, _logger, ct);
                if (additionalConfig is not null)
                    configs.Add(additionalConfig);
            }
        }

        var agentConfigs = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Agent, ct);
        var agentConfig = await ProviderConfigResolver.ResolveAsync(
            _providerConfigStore, agentProviderId, ProviderKind.Agent, agentConfigs, required: true, _logger, ct);
        configs.Add(agentConfig!);

        if (!string.IsNullOrEmpty(brainProviderId))
        {
            var brainConfig = await ProviderConfigResolver.ResolveAsync(
                _providerConfigStore, brainProviderId, ProviderKind.Repository, repoConfigs, required: false, _logger, ct);
            if (brainConfig is not null)
                configs.Add(brainConfig);
        }

        if (!string.IsNullOrEmpty(pipelineProviderId))
        {
            var pipelineConfigs = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Pipeline, ct);
            var pipelineConfig = await ProviderConfigResolver.ResolveAsync(
                _providerConfigStore, pipelineProviderId, ProviderKind.Pipeline, pipelineConfigs, required: false, _logger, ct);
            if (pipelineConfig is not null)
                configs.Add(pipelineConfig);
        }

        return configs.AsReadOnly();
    }
}
