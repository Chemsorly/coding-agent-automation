using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Api;

/// <summary>
/// Fetches fresh mutable config (provider configs with vended tokens, steering content,
/// quality gate configs, reviewer configs, MCP servers, and full issue context) at agent
/// assignment time rather than using the snapshot frozen in <c>WorkItems.Payload</c>.
/// <para>
/// This fixes the stale-config problem described in issue #2171:
/// <c>WorkItems.Payload</c> now stores only identity fields; all mutable config is resolved
/// fresh each time <c>GET /api/work-items/{id}/assignment</c> is called.
/// </para>
/// </summary>
/// <remarks>
/// Registered as a singleton in the API host. All dependencies are thread-safe singletons.
/// Not sealed to allow Moq-based test mocking in unit tests.
/// </remarks>
public class AssignmentEnricher
{
    private readonly DispatchInfrastructure _infra;
    private readonly IAgentProfileStore _agentProfileStore;
    private readonly ILogger _logger;

    public AssignmentEnricher(
        DispatchInfrastructure infra,
        IAgentProfileStore agentProfileStore,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(infra);
        ArgumentNullException.ThrowIfNull(agentProfileStore);
        ArgumentNullException.ThrowIfNull(logger);

        _infra = infra;
        _agentProfileStore = agentProfileStore;
        _logger = logger;
    }

    /// <summary>
    /// Protected constructor for test subclasses that override <see cref="EnrichAsync"/>.
    /// The dependency parameters are not used when <see cref="EnrichAsync"/> is fully overridden,
    /// so <c>null</c> values are accepted without validation.
    /// </summary>
    // TODO: [WARNING] If a test subclass calls base.EnrichAsync (rather than overriding it),
    // EnrichCoreAsync will throw NullReferenceException on _infra/_agentProfileStore with no
    // helpful message. Consider adding a guard in EnrichCoreAsync (e.g., throw InvalidOperationException
    // with a clear message when _infra is null) to fail fast with a diagnostic message rather than
    // an opaque NRE. Also, logger is not null-guarded consistently with the public constructor.
    protected internal AssignmentEnricher(ILogger logger)
    {
        _infra = null!;
        _agentProfileStore = null!;
        _logger = logger ?? Serilog.Log.Logger;
    }

    /// <summary>
    /// Enriches a minimal-payload <see cref="JobDistributionRequest"/> with fresh config resolved
    /// from the database at the time of assignment.
    /// </summary>
    /// <param name="identity">
    /// The minimal identity payload deserialized from <c>WorkItems.Payload</c>.
    /// Must contain at minimum: <see cref="JobDistributionRequest.IssueIdentifier"/>,
    /// <see cref="JobDistributionRequest.IssueProviderConfigId"/>,
    /// <see cref="JobDistributionRequest.RepoProviderConfigId"/>,
    /// and <see cref="JobDistributionRequest.AgentSelector"/>.
    /// </param>
    /// <param name="project">
    /// The resolved project for this work item, or a minimal stub if the project ID is null.
    /// Required by <see cref="DispatchInfrastructure.PrepareDispatchCoreAsync"/> for config
    /// resolution and steering content.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The enriched <see cref="JobDistributionRequest"/> with fresh provider configs, QGs,
    /// reviewers, MCP servers, and issue context; or <c>null</c> if enrichment fails
    /// (e.g., provider config not found, profile resolution fails).
    /// </returns>
    public virtual async Task<JobDistributionRequest?> EnrichAsync(
        JobDistributionRequest identity,
        PipelineProject project,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(project);

        try
        {
            return await EnrichCoreAsync(identity, project, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // TODO: [WARNING] This catch-all swallows both permanent failures (provider not found)
            // and transient infrastructure failures (DB timeout, network error). For transient
            // failures, returning 503 and letting the agent retry would be more correct than silently
            // serving an incomplete assignment. Consider distinguishing exception types and propagating
            // transient failures so the caller can return 503 instead of a degraded 200.
            _logger.Warning(ex,
                "AssignmentEnricher: failed to enrich assignment for WorkItem with IssueIdentifier {IssueIdentifier}; falling back to identity payload",
                identity.IssueIdentifier);
            return null;
        }
    }

    private async Task<JobDistributionRequest?> EnrichCoreAsync(
        JobDistributionRequest identity,
        PipelineProject project,
        CancellationToken ct)
    {
        // ── Step 1: Resolve agent profile from AgentSelector ──────────────────────
        // AgentSelector is the sorted comma-joined MatchLabels from the resolved profile.
        // Splitting it back gives us the required labels for fresh profile resolution.
        var selectorLabels = (identity.AgentSelector ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var profiles = await _agentProfileStore.LoadAgentProfilesAsync(ct);
        // TODO: [WARNING] ProfileResolver.ResolveByRequiredLabels is called as a static method, but
        // ProfileResolver is registered as a singleton in the DI container. If the instance ever
        // gains injected state or config, this static call will silently bypass it.
        var profile = ProfileResolver.ResolveByRequiredLabels(profiles, selectorLabels);

        if (profile is null)
        {
            _logger.Warning(
                "AssignmentEnricher: no profile matches selector [{Selector}]; cannot enrich assignment",
                identity.AgentSelector ?? "");
            return null;
        }

        // ── Step 2: Prepare dispatch core (QGs, reviewers, issue context, provider configs, pipeline config) ──
        var coreRequest = new DispatchCoreRequest(
            RequiredLabels: selectorLabels,
            IssueIdentifier: identity.IssueIdentifier,
            IssueProviderId: new ProviderConfigId(identity.IssueProviderConfigId),
            RepoProviderId: new ProviderConfigId(identity.RepoProviderConfigId),
            AgentProviderId: profile.AgentProviderConfigId ?? "",
            BrainProviderId: identity.BrainProviderConfigId,
            PipelineProviderId: identity.PipelineProviderConfigId,
            Project: project,
            Logger: _logger);

        var core = await _infra.PrepareDispatchCoreAsync(coreRequest, ct);
        if (core is null)
        {
            _logger.Warning(
                "AssignmentEnricher: PrepareDispatchCoreAsync returned null for IssueIdentifier {IssueIdentifier}",
                identity.IssueIdentifier);
            return null;
        }

        var (resolvedQgcs, resolvedReviewerConfigs, issueContext, providerConfigs, config,
            forceRefresh, stalenessSignal, refreshCount) = core.Value;

        // ── Step 3: Build enriched JobDistributionRequest from identity + fresh data ──
        return identity with
        {
            // Fresh-fetched mutable fields
            ProviderConfigs = providerConfigs,
            PipelineConfiguration = config,
            QualityGateConfigs = resolvedQgcs,
            ReviewerConfigs = resolvedReviewerConfigs,
            McpServers = DispatchOrchestrationService.MergeMcpServers(profile.McpServers, project.McpServers),
            // TODO: [WARNING] MergeMcpServers is called as a static method on DispatchOrchestrationService —
            // a layering concern. If the method ever acquires side effects or shared state, concurrent
            // GetAssignment calls from this singleton could produce unexpected results. Consider
            // extracting this into a standalone static utility or a dedicated service.
            ResolvedProfileId = profile.Id,
            AgentProviderConfigId = profile.AgentProviderConfigId,
            ProjectSteeringContent = project.SteeringContent,
            RepoSteeringContent = providerConfigs
                .TryGetProviderConfig(identity.RepoProviderConfigId)?.SteeringContent,

            // Fresh-fetched issue context
            IssueDetail = issueContext.IssueDetail,
            ParsedIssue = issueContext.ParsedIssue,
            IssueComments = issueContext.IssueComments,
            ExistingAnalysis = issueContext.ExistingAnalysis,
            ForceRefreshAnalysis = forceRefresh,
            StalenessSignal = stalenessSignal,
            AnalysisRefreshCount = refreshCount,
        };
    }
}
