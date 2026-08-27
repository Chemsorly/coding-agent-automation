using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration;

/// <summary>
/// Shared consolidation job preparation: resolves provider configs from template,
/// vends scoped GitHub tokens, and determines correct permission scope.
/// Used by both ConsolidationDispatchService (SignalR) and DispatchService (K8s).
/// </summary>
public sealed class ConsolidationJobPreparationService : IConsolidationJobPreparationService
{
    private readonly IProviderConfigStore _providerConfigStore;
    private readonly IAgentProfileStore _agentProfileStore;
    private readonly IProjectStore _projectStore;
    private readonly ITokenVendingService _tokenVending;
    private readonly ILogger _logger;

    public ConsolidationJobPreparationService(
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
        _agentProfileStore = agentProfileStore
            ?? providerConfigStore as IAgentProfileStore
            ?? throw new ArgumentException(
                $"{nameof(providerConfigStore)} must implement IAgentProfileStore when {nameof(agentProfileStore)} is not provided",
                nameof(providerConfigStore));
        _projectStore = projectStore;
        _tokenVending = tokenVending;
        _logger = logger;
    }

    /// <summary>
    /// Convenience constructor for DI when IConfigurationStore is available (implements all sub-interfaces).
    /// </summary>
    public ConsolidationJobPreparationService(
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
        TemplateId? templateId,
        IReadOnlyList<string> agentLabels,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agentLabels);

        var rawConfigs = new List<ProviderConfig>();
        await ResolveAgentProviderConfigAsync(rawConfigs, agentLabels, ct);

        var repoProviderId = "";
        if (templateId is not null)
        {
            var template = await ResolveTemplateAsync(templateId.Value, ct);
            if (template is not null)
                repoProviderId = await ResolveTemplateProviderConfigsAsync(rawConfigs, template, type, ct);
        }

        var vendedConfigs = await VendProviderConfigsAsync(rawConfigs, repoProviderId, type, ct);

        return new ConsolidationJobPreparationResult
        {
            ProviderConfigs = vendedConfigs,
            RepoProviderConfigId = repoProviderId
        };
    }

    /// <summary>
    /// Resolves the agent provider config via profile or fallback, appending to rawConfigs.
    /// </summary>
    private async Task ResolveAgentProviderConfigAsync(
        List<ProviderConfig> rawConfigs,
        IReadOnlyList<string> agentLabels,
        CancellationToken ct)
    {
        var agentConfigs = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Agent, ct);
        var profiles = await _agentProfileStore.LoadAgentProfilesAsync(ct);
        var profileResolver = new ProfileResolver();
        var profile = profileResolver.Resolve(profiles, agentLabels);

        if (profile is not null)
        {
            var agentConfig = agentConfigs.TryGetProviderConfig(profile.AgentProviderConfigId);
            if (agentConfig is not null)
            {
                rawConfigs.Add(agentConfig);
                _logger.Debug(
                    "ConsolidationJobPreparationService: resolved agent provider via profile '{ProfileId}' for labels [{Labels}]",
                    profile.Id, string.Join(", ", agentLabels));
            }
            return;
        }

        // No matching profile — in Kubernetes mode the dispatched pod's provider type must
        // match the resolved agent config. Without a matching profile we cannot know which
        // provider to use, so we log a warning and skip adding an agent config. The dispatch
        // will fail with a provider-resolution error rather than silently using the wrong provider.
        _logger.Warning(
            "ConsolidationJobPreparationService: no profile matches labels [{Labels}] — " +
            "no agent provider config will be injected. Ensure an AgentProfile with matchLabels " +
            "matching the job's AgentSelector exists.",
            string.Join(", ", agentLabels));
    }

    /// <summary>
    /// Resolves repo, brain, and issue provider configs from the template, appending to rawConfigs.
    /// Returns the repoProviderId.
    /// </summary>
    private async Task<string> ResolveTemplateProviderConfigsAsync(
        List<ProviderConfig> rawConfigs,
        PipelineJobTemplate template,
        ConsolidationRunType type,
        CancellationToken ct)
    {
        var repoProviderId = "";

        if (string.IsNullOrEmpty(template.RepoProviderId))
            return repoProviderId;

        repoProviderId = template.RepoProviderId;
        var repoConfigs = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
        var repoConfig = repoConfigs.TryGetProviderConfig(template.RepoProviderId);
        if (repoConfig is not null)
            rawConfigs.Add(repoConfig);

        // Add brain provider if configured
        if (!string.IsNullOrEmpty(template.BrainProviderId))
        {
            var brainConfig = repoConfigs.TryGetProviderConfig(template.BrainProviderId);
            if (brainConfig is not null)
                rawConfigs.Add(brainConfig);
        }

        // Add issue provider for refactoring detection
        if (type == ConsolidationRunType.RefactoringDetection && !string.IsNullOrEmpty(template.IssueProviderId))
        {
            var issueConfig = await _providerConfigStore.GetProviderConfigByIdAsync(
                template.IssueProviderId, ProviderKind.Issue, ct);
            if (issueConfig is not null)
                rawConfigs.Add(issueConfig);
        }

        return repoProviderId;
    }

    /// <summary>Vends tokens with correct permission scope and returns the prepared configs.</summary>
    private async Task<IReadOnlyList<ProviderConfig>> VendProviderConfigsAsync(
        List<ProviderConfig> rawConfigs,
        string repoProviderId,
        ConsolidationRunType type,
        CancellationToken ct)
    {
        if (rawConfigs.Count == 0)
            return rawConfigs.AsReadOnly();

        var includeIssuePermission = type == ConsolidationRunType.RefactoringDetection;
        return await _tokenVending.PrepareAgentConfigsAsync(
            rawConfigs, repoProviderId, ct, includeIssuePermission);
    }

    private async Task<PipelineJobTemplate?> ResolveTemplateAsync(TemplateId templateId, CancellationToken ct)
    {
        var templates = await _projectStore.LoadAllTemplatesAsync(ct);
        return templates.FirstOrDefault(t => t.Id == templateId.Value);
    }
}
