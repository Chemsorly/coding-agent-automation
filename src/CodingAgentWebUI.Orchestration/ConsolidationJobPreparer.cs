using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration;

/// <summary>
/// Shared consolidation job preparation: resolves provider configs from template,
/// vends scoped GitHub tokens, and determines correct permission scope.
/// Used by both ConsolidationDispatcher (SignalR) and DispatchService (K8s).
/// </summary>
public sealed class ConsolidationJobPreparer : IConsolidationJobPreparer
{
    private readonly IProviderConfigStore _providerConfigStore;
    private readonly IAgentProfileStore _agentProfileStore;
    private readonly IProjectStore _projectStore;
    private readonly ITokenVendingService _tokenVending;
    private readonly ILogger _logger;

    public ConsolidationJobPreparer(
        IProviderConfigStore providerConfigStore,
        IProjectStore projectStore,
        ITokenVendingService tokenVending,
        ILogger logger,
        IAgentProfileStore? agentProfileStore = null)
    {
        ArgumentNullException.ThrowIfNull(providerConfigStore);
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(tokenVending);
        ArgumentNullException.ThrowIfNull(logger);

        _providerConfigStore = providerConfigStore;
        _agentProfileStore = agentProfileStore ?? (providerConfigStore as IAgentProfileStore)!;
        _projectStore = projectStore;
        _tokenVending = tokenVending;
        _logger = logger;
    }

    /// <summary>
    /// Convenience constructor for DI when IConfigurationStore is available (implements all sub-interfaces).
    /// </summary>
    public ConsolidationJobPreparer(
        IConfigurationStore configStore,
        IProjectStore projectStore,
        ITokenVendingService tokenVending,
        ILogger logger)
        : this((IProviderConfigStore)configStore, projectStore, tokenVending, logger, configStore)
    {
    }

    /// <inheritdoc />
    public async Task<ConsolidationJobPreparationResult> PrepareAsync(
        ConsolidationRunType type,
        string? templateId,
        IReadOnlyList<string> agentLabels,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agentLabels);

        // 1. Resolve agent provider config via profile
        var rawConfigs = new List<ProviderConfig>();
        var agentConfigs = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Agent, ct);
        var profiles = await _agentProfileStore.LoadAgentProfilesAsync(ct);
        var profileResolver = new ProfileResolver();
        var profile = profileResolver.Resolve(profiles, agentLabels);

        if (profile is not null)
        {
            var agentConfig = agentConfigs.FirstOrDefault(c => c.Id == profile.AgentProviderConfigId);
            if (agentConfig is not null)
            {
                rawConfigs.Add(agentConfig);
                _logger.Debug(
                    "ConsolidationJobPreparer: resolved agent provider via profile '{ProfileId}' for labels [{Labels}]",
                    profile.Id, string.Join(", ", agentLabels));
            }
        }
        else
        {
            // No matching profile — use first available agent config as fallback
            var fallback = agentConfigs.FirstOrDefault();
            if (fallback is not null)
                rawConfigs.Add(fallback);

            if (profiles.Count > 0)
            {
                _logger.Warning(
                    "ConsolidationJobPreparer: no profile matches labels [{Labels}], using fallback agent config",
                    string.Join(", ", agentLabels));
            }
        }

        // 2. Resolve template for repo/brain/issue providers
        var repoProviderId = "";
        PipelineJobTemplate? template = null;

        if (templateId is not null)
            template = await ResolveTemplateAsync(templateId, ct);

        if (template is not null)
        {
            // 3. Add repo provider
            if (!string.IsNullOrEmpty(template.RepoProviderId))
            {
                repoProviderId = template.RepoProviderId;
                var repoConfigs = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
                var repoConfig = repoConfigs.FirstOrDefault(c => c.Id == template.RepoProviderId);
                if (repoConfig is not null)
                    rawConfigs.Add(repoConfig);

                // 4. Add brain provider if configured
                if (!string.IsNullOrEmpty(template.BrainProviderId))
                {
                    var brainConfig = repoConfigs.FirstOrDefault(c => c.Id == template.BrainProviderId);
                    if (brainConfig is not null)
                        rawConfigs.Add(brainConfig);
                }
            }

            // 5. Add issue provider for refactoring detection
            if (type == ConsolidationRunType.RefactoringDetection && !string.IsNullOrEmpty(template.IssueProviderId))
            {
                var issueConfig = await _providerConfigStore.GetProviderConfigByIdAsync(
                    template.IssueProviderId, ProviderKind.Issue, ct);
                if (issueConfig is not null)
                    rawConfigs.Add(issueConfig);
            }
        }

        // 6. Vend tokens with correct permission scope
        IReadOnlyList<ProviderConfig> vendedConfigs;
        if (rawConfigs.Count > 0)
        {
            var includeIssuePermission = type == ConsolidationRunType.RefactoringDetection;
            vendedConfigs = await _tokenVending.PrepareAgentConfigsAsync(
                rawConfigs, repoProviderId, ct, includeIssuePermission);
        }
        else
        {
            vendedConfigs = rawConfigs.AsReadOnly();
        }

        return new ConsolidationJobPreparationResult
        {
            ProviderConfigs = vendedConfigs,
            RepoProviderConfigId = repoProviderId
        };
    }

    private async Task<PipelineJobTemplate?> ResolveTemplateAsync(string templateId, CancellationToken ct)
    {
        var templates = await _projectStore.LoadAllTemplatesAsync(ct);
        return templates.FirstOrDefault(t => t.Id == templateId);
    }
}
