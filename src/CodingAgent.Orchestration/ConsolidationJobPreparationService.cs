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
    private readonly IProjectStore _projectStore;
    private readonly ITokenVendingService _tokenVending;
    private readonly IPipelineConfigStore _pipelineConfigStore;
    private readonly ILogger _logger;

    public ConsolidationJobPreparationService(
        IProviderConfigStore providerConfigStore,
        IProjectStore projectStore,
        ITokenVendingService tokenVending,
        ILogger logger,
        IPipelineConfigStore? pipelineConfigStore = null)
    {
        ArgumentNullException.ThrowIfNull(providerConfigStore);
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(tokenVending);
        ArgumentNullException.ThrowIfNull(logger);

        _providerConfigStore = providerConfigStore;
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
        : this((IProviderConfigStore)configStore, projectStore, tokenVending, logger, configStore)
    {
    }

    /// <inheritdoc />
    public async Task<ConsolidationJobPreparationResult> PrepareAsync(
        ConsolidationRunType type,
        TemplateId? templateId,
        AgentProfile? profile,
        CancellationToken ct)
    {
        var rawConfigs = new List<ProviderConfig>();
        await ResolveAgentProviderConfigAsync(rawConfigs, profile, ct);

        var repoProviderId = "";
        string? brainProviderId = null;
        if (templateId is not null)
        {
            var template = await ResolveTemplateAsync(templateId.Value, ct);
            if (template is not null)
                (repoProviderId, brainProviderId) = await ResolveTemplateProviderConfigsAsync(rawConfigs, template, type, ct);
        }

        // Resolve the pipeline configuration BEFORE vending tokens so that BrainReadOnly is known
        // when we choose the token permission scope. ResolvePipelineConfigurationAsync only reads
        // the repo blacklist from the provider configs; vending (Token/TokenExpiresAt/PrivateKeyBase64)
        // does not affect that field, so passing raw configs is safe.
        var pipelineConfiguration = await ResolvePipelineConfigurationAsync(
            templateId, repoProviderId, rawConfigs, ct);

        // Vend tokens: pass the brain config as read-only when BrainReadOnly is true.
        IReadOnlySet<string>? readOnlyConfigIds = pipelineConfiguration.BrainReadOnly && !string.IsNullOrEmpty(brainProviderId)
            ? new HashSet<string>(StringComparer.Ordinal) { brainProviderId }
            : null;
        var vendedConfigs = await VendProviderConfigsAsync(rawConfigs, repoProviderId, type, ct, readOnlyConfigIds);

        return new ConsolidationJobPreparationResult
        {
            ProviderConfigs = vendedConfigs,
            RepoProviderConfigId = repoProviderId,
            BrainProviderConfigId = brainProviderId,
            PipelineConfiguration = pipelineConfiguration
        };
    }

    /// <summary>
    /// Resolves the agent provider config from the supplied profile, appending to rawConfigs.
    /// When the profile is null (work item queued before this change, or profile deleted/disabled),
    /// no agent provider config is injected and a warning is logged.
    /// </summary>
    private async Task ResolveAgentProviderConfigAsync(
        List<ProviderConfig> rawConfigs,
        AgentProfile? profile,
        CancellationToken ct)
    {
        if (profile is not null)
        {
            var agentConfigs = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Agent, ct);
            var agentConfig = agentConfigs.TryGetProviderConfig(profile.AgentProviderConfigId);
            if (agentConfig is not null)
            {
                rawConfigs.Add(agentConfig);
                _logger.Debug(
                    "ConsolidationJobPreparationService: resolved agent provider via profile '{ProfileId}'",
                    profile.Id);
            }
            return;
        }

        // No profile — cannot determine which agent provider to use.
        // TODO: [WARNING] Requirement 8 says the warning should name the work item's selector, but this
        // method no longer receives the selector (it accepts AgentProfile? directly). The inline comment
        // "The caller logs the work item's selector" is also inaccurate: EnrichConsolidationCoreAsync
        // returns null before calling PrepareAsync when profile is null, so in practice this branch is
        // only reachable from direct calls (tests or future callers outside the current enrichment path).
        _logger.Warning(
            "ConsolidationJobPreparationService: no agent profile supplied — " +
            "no agent provider config will be injected.");
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
            rawConfigs.Add(RepositorySettings.WithTemplateSettings(repoConfig, template));

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
        CancellationToken ct,
        IReadOnlySet<string>? readOnlyConfigIds = null)
    {
        if (rawConfigs.Count == 0)
            return rawConfigs.AsReadOnly();

        var includeIssuePermission = type == ConsolidationRunType.RefactoringDetection;
        return await _tokenVending.PrepareAgentConfigsAsync(
            rawConfigs, repoProviderId, ct, includeIssuePermission, readOnlyConfigIds);
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
            // TODO: ClampSubIssueCap is not applied here because it is private to PipelineConfigurationResolver
            // and ProviderConfigId rejects an empty string, preventing the full ResolveAsync chain from running.
            // A project-level override that sets MaxDecompositionSubIssues to 0 would bypass the clamp on this
            // code path. Fix by either exposing ClampSubIssueCap as an internal/public static method on
            // PipelineConfigurationResolver, or by constructing a dummy ProviderConfigId that allows ResolveAsync
            // to run when no real repo provider is configured.
            return PipelineConfigurationResolver.ApplyProjectOverrides(globalConfig, project);
        }

        return await PipelineConfigurationResolver.ResolveAsync(
            _pipelineConfigStore.LoadPipelineConfigAsync,
            _projectStore.LoadAllTemplatesAsync,
            project,
            (ProviderConfigId)repoProviderId,
            vendedConfigs,
            ct);
    }

    private async Task<PipelineJobTemplate?> ResolveTemplateAsync(TemplateId templateId, CancellationToken ct)
    {
        var templates = await _projectStore.LoadAllTemplatesAsync(ct);
        return templates.FirstOrDefault(t => t.Id == templateId.Value);
    }
}
