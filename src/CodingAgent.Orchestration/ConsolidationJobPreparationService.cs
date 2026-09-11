using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration;

/// <summary>
/// Shared consolidation job preparation: resolves provider configs from template,
/// vends scoped GitHub tokens, determines correct permission scope, and resolves
/// the pipeline configuration via <see cref="PipelineConfigurationResolver"/> so that
/// per-project overrides (AgentTimeout, *ReviewEnabled, etc.) are applied.
/// Used by both ConsolidationDispatchService (SignalR) and DispatchService (K8s).
/// </summary>
public sealed class ConsolidationJobPreparationService : IConsolidationJobPreparationService
{
    private readonly IProviderConfigStore _providerConfigStore;
    private readonly IAgentProfileStore _agentProfileStore;
    private readonly IProjectStore _projectStore;
    private readonly ITokenVendingService _tokenVending;
    private readonly IPipelineConfigStore _pipelineConfigStore;
    private readonly ILogger _logger;

    public ConsolidationJobPreparationService(
        IProviderConfigStore providerConfigStore,
        IProjectStore projectStore,
        ITokenVendingService tokenVending,
        ILogger logger,
        IAgentProfileStore? agentProfileStore = null,
        IPipelineConfigStore? pipelineConfigStore = null)
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
        _pipelineConfigStore = pipelineConfigStore
            ?? providerConfigStore as IPipelineConfigStore
            ?? throw new ArgumentException(
                $"{nameof(providerConfigStore)} must implement IPipelineConfigStore when {nameof(pipelineConfigStore)} is not provided",
                nameof(providerConfigStore));
    }

    /// <summary>
    /// Convenience constructor for DI when IConfigurationStore is available (implements all sub-interfaces).
    /// </summary>
    public ConsolidationJobPreparationService(
        IConfigurationStore configStore,
        IProjectStore projectStore,
        ITokenVendingService tokenVending,
        ILogger logger)
        : this((IProviderConfigStore)configStore, projectStore, tokenVending, logger, configStore, configStore)
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
        string? brainProviderId = null;
        if (templateId is not null)
        {
            var template = await ResolveTemplateAsync(templateId.Value, ct);
            if (template is not null)
                (repoProviderId, brainProviderId) = await ResolveTemplateProviderConfigsAsync(rawConfigs, template, type, ct);
        }

        var vendedConfigs = await VendProviderConfigsAsync(rawConfigs, repoProviderId, type, ct);

        var pipelineConfiguration = await ResolvePipelineConfigurationAsync(
            templateId, repoProviderId, brainProviderId, vendedConfigs, ct);

        return new ConsolidationJobPreparationResult
        {
            ProviderConfigs = vendedConfigs,
            RepoProviderConfigId = repoProviderId,
            PipelineConfiguration = pipelineConfiguration
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
    /// Returns a tuple of (repoProviderId, brainProviderId).
    /// </summary>
    private async Task<(string repoProviderId, string? brainProviderId)> ResolveTemplateProviderConfigsAsync(
        List<ProviderConfig> rawConfigs,
        PipelineJobTemplate template,
        ConsolidationRunType type,
        CancellationToken ct)
    {
        var repoProviderId = "";
        string? brainProviderId = null;

        if (string.IsNullOrEmpty(template.RepoProviderId))
            return (repoProviderId, brainProviderId);

        repoProviderId = template.RepoProviderId;
        var repoConfigs = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
        var repoConfig = repoConfigs.TryGetProviderConfig(template.RepoProviderId);
        if (repoConfig is not null)
            rawConfigs.Add(repoConfig);

        // Add brain provider if configured
        if (!string.IsNullOrEmpty(template.BrainProviderId))
        {
            brainProviderId = template.BrainProviderId;
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

        return (repoProviderId, brainProviderId);
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

    /// <summary>
    /// Resolves the pipeline configuration for the consolidation job.
    /// When a template is available, applies the full resolution chain:
    /// global → project overrides → template overrides (via PipelineConfigurationResolver.ResolveAsync).
    /// When no template is available, returns the global config unchanged.
    /// </summary>
    private async Task<PipelineConfiguration> ResolvePipelineConfigurationAsync(
        TemplateId? templateId,
        string repoProviderId,
        string? brainProviderId,
        IReadOnlyList<ProviderConfig> vendedConfigs,
        CancellationToken ct)
    {
        // No template context — return global config without project/template overrides.
        if (templateId is null)
            return await _pipelineConfigStore.LoadPipelineConfigAsync(ct);

        // Resolve owning project so per-project overrides (AgentTimeout, *ReviewEnabled, etc.) apply.
        // A template may belong to no project (uncommon); both ApplyProjectOverrides and ResolveAsync
        // accept a null project and return the config unchanged in that case.
        var projects = await _projectStore.LoadProjectsAsync(ct);
        var project = projects.FirstOrDefault(p => p.TemplateIds.Contains(templateId.Value.Value));

        if (string.IsNullOrEmpty(repoProviderId))
        {
            // Template has no repo provider. Template-level overrides (blacklist, brain read-only) need a
            // repoProviderId, but project overrides do not — apply those directly so per-project settings
            // are not silently dropped. ProviderConfigId rejects an empty value, so the full ResolveAsync
            // chain cannot run here.
            var globalConfig = await _pipelineConfigStore.LoadPipelineConfigAsync(ct);
            return PipelineConfigurationResolver.ApplyProjectOverrides(globalConfig, project);
        }

        return await PipelineConfigurationResolver.ResolveAsync(
            _pipelineConfigStore.LoadPipelineConfigAsync,
            _projectStore.LoadAllTemplatesAsync,
            project,
            (ProviderConfigId)repoProviderId,
            brainProviderId,
            vendedConfigs,
            ct);
    }

    private async Task<PipelineJobTemplate?> ResolveTemplateAsync(TemplateId templateId, CancellationToken ct)
    {
        var templates = await _projectStore.LoadAllTemplatesAsync(ct);
        return templates.FirstOrDefault(t => t.Id == templateId.Value);
    }
}
