using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Builds and prepares provider configs for agent dispatch.
/// Extracted from <see cref="AgentJobDispatcher"/> and <see cref="DispatchOrchestrationService"/>
/// to eliminate duplication.
/// <para>
/// The superset signature supports optional <paramref name="additionalRepoProviderIds"/> for
/// cross-repo decomposition (used by <see cref="AgentJobDispatcher"/>). Callers that don't
/// need cross-repo support simply omit the parameter.
/// </para>
/// </summary>
public sealed class ProviderConfigBuilder
{
    private readonly IConfigurationStore _configStore;
    private readonly ITokenVendingService _tokenVending;

    internal ProviderConfigBuilder(IConfigurationStore configStore, ITokenVendingService tokenVending)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(tokenVending);

        _configStore = configStore;
        _tokenVending = tokenVending;
    }

    /// <summary>
    /// Builds the provider configs list and prepares tokens via the token vending service.
    /// </summary>
    public async Task<IReadOnlyList<ProviderConfig>> PrepareProviderConfigsAsync(
        string repoProviderId,
        string agentProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        ILogger logger,
        CancellationToken ct,
        IEnumerable<string>? additionalRepoProviderIds = null)
    {
        var rawConfigs = await BuildAgentProviderConfigsAsync(
            repoProviderId, agentProviderId, brainProviderId, pipelineProviderId, logger, ct, additionalRepoProviderIds);
        return await _tokenVending.PrepareAgentConfigsAsync(rawConfigs, repoProviderId, ct);
    }

    /// <summary>
    /// Builds the list of provider configs to send to the agent.
    /// Excludes issue provider configs (agents don't get issue access).
    /// </summary>
    public async Task<IReadOnlyList<ProviderConfig>> BuildAgentProviderConfigsAsync(
        string repoProviderId,
        string agentProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        ILogger logger,
        CancellationToken ct,
        IEnumerable<string>? additionalRepoProviderIds = null)
    {
        var configs = new List<ProviderConfig>();

        var repoConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
        var repoConfig = await ProviderConfigResolver.ResolveAsync(
            _configStore, repoProviderId, ProviderKind.Repository, repoConfigs, required: true, logger, ct);
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
                    _configStore, additionalId, ProviderKind.Repository, repoConfigs, required: false, logger, ct);
                if (additionalConfig is not null)
                    configs.Add(additionalConfig);
            }
        }

        var agentConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Agent, ct);
        var agentConfig = await ProviderConfigResolver.ResolveAsync(
            _configStore, agentProviderId, ProviderKind.Agent, agentConfigs, required: true, logger, ct);
        configs.Add(agentConfig!);

        if (!string.IsNullOrEmpty(brainProviderId))
        {
            var brainConfig = await ProviderConfigResolver.ResolveAsync(
                _configStore, brainProviderId, ProviderKind.Repository, repoConfigs, required: false, logger, ct);
            if (brainConfig is not null)
                configs.Add(brainConfig);
        }

        if (!string.IsNullOrEmpty(pipelineProviderId))
        {
            var pipelineConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Pipeline, ct);
            var pipelineConfig = await ProviderConfigResolver.ResolveAsync(
                _configStore, pipelineProviderId, ProviderKind.Pipeline, pipelineConfigs, required: false, logger, ct);
            if (pipelineConfig is not null)
                configs.Add(pipelineConfig);
        }

        return configs.AsReadOnly();
    }
}
