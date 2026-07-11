using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Groups dispatch-time resolution concerns: profile, quality gate, and reviewer resolution.
/// Extracted from <see cref="AgentJobDispatcher"/> to reduce constructor parameter count.
/// </summary>
public sealed class DispatchResolutionService
{
    private readonly ProfileResolver _profileResolver;
    private readonly QualityGateResolver _qualityGateResolver;
    private readonly ReviewerResolver _reviewerResolver;
    private readonly ILogger _logger;

    internal IAgentProfileStore AgentProfileStore { get; }
    internal IQualityGateConfigStore QualityGateConfigStore { get; }
    internal IReviewerConfigStore ReviewerConfigStore { get; }
    internal IProviderConfigStore ProviderConfigStore { get; }
    internal IPipelineConfigStore PipelineConfigStore { get; }
    internal IProjectStore ProjectStore { get; }

    public DispatchResolutionService(
        ProfileResolver profileResolver,
        QualityGateResolver qualityGateResolver,
        ReviewerResolver reviewerResolver,
        IAgentProfileStore agentProfileStore,
        IQualityGateConfigStore qualityGateConfigStore,
        IReviewerConfigStore reviewerConfigStore,
        IProviderConfigStore providerConfigStore,
        IPipelineConfigStore pipelineConfigStore,
        IProjectStore projectStore,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(profileResolver);
        ArgumentNullException.ThrowIfNull(qualityGateResolver);
        // TODO: Fix missing null check — reviewerResolver is never validated (duplicate reviewerConfigStore check below). Replace this line with ArgumentNullException.ThrowIfNull(reviewerResolver).
        ArgumentNullException.ThrowIfNull(reviewerConfigStore);
        ArgumentNullException.ThrowIfNull(agentProfileStore);
        ArgumentNullException.ThrowIfNull(qualityGateConfigStore);
        ArgumentNullException.ThrowIfNull(reviewerConfigStore);
        ArgumentNullException.ThrowIfNull(providerConfigStore);
        ArgumentNullException.ThrowIfNull(pipelineConfigStore);
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(logger);

        _profileResolver = profileResolver;
        _qualityGateResolver = qualityGateResolver;
        _reviewerResolver = reviewerResolver;
        AgentProfileStore = agentProfileStore;
        QualityGateConfigStore = qualityGateConfigStore;
        ReviewerConfigStore = reviewerConfigStore;
        ProviderConfigStore = providerConfigStore;
        PipelineConfigStore = pipelineConfigStore;
        ProjectStore = projectStore;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the agent profile by loading all profiles and matching against the agent's labels.
    /// Returns <c>null</c> (with a warning log) if no profile matches.
    /// </summary>
    public async Task<AgentProfile?> ResolveProfileAsync(AgentEntry agent, CancellationToken ct)
    {
        var profiles = await AgentProfileStore.LoadAgentProfilesAsync(ct);
        var profile = _profileResolver.Resolve(profiles, agent.Labels);
        if (profile is null)
        {
            var labelsStr = string.Join(", ", agent.Labels);
            _logger.Warning("No profile matches agent {AgentId} labels [{Labels}]", agent.AgentId, labelsStr);
        }

        return profile;
    }

    /// <summary>
    /// Resolves quality gate configurations matching the job's required labels.
    /// </summary>
    public async Task<IReadOnlyList<QualityGateConfiguration>> ResolveQualityGatesAsync(
        IReadOnlyList<string> requiredLabels, CancellationToken ct)
    {
        var allQgcs = await QualityGateConfigStore.LoadQualityGateConfigsAsync(ct);
        return _qualityGateResolver.Resolve(allQgcs, requiredLabels);
    }

    /// <summary>
    /// Resolves reviewer configurations matching the job's required labels.
    /// </summary>
    public async Task<IReadOnlyList<ReviewerConfiguration>> ResolveReviewersAsync(
        IReadOnlyList<string> requiredLabels, CancellationToken ct)
    {
        var allReviewerConfigs = await ReviewerConfigStore.LoadReviewerConfigsAsync(ct);
        return _reviewerResolver.Resolve(allReviewerConfigs, requiredLabels);
    }
}
