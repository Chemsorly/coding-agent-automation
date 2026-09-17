using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Api;

/// <summary>
/// Fetches fresh mutable config (provider configs with vended tokens, steering content,
/// quality gate configs, reviewer configs, MCP servers, and full issue context) at agent
/// assignment time rather than using the snapshot frozen in <c>WorkItems.Payload</c>.
/// <para>
/// This fixes the stale-config problem described in issue #2171:
/// <c>WorkItems.Payload</c> now stores only identity fields; all mutable config is resolved
/// fresh each time <c>GET /api/work-items/{id}/assignment</c> is called.
/// </para>
/// <para>
/// For <c>TaskType=Consolidation</c> work items, enrichment delegates to
/// <see cref="IConsolidationJobPreparationService.PrepareAsync"/> instead of
/// <see cref="DispatchInfrastructure.PrepareDispatchCoreAsync"/> (issue #2583).
/// Issue-provider infrastructure and quality-gate configs are not fetched for consolidation
/// agents, which do not process issues and have no quality gates.
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
    private readonly IConsolidationJobPreparationService _consolidationPreparer;
    private readonly ILogger _logger;

    public AssignmentEnricher(
        DispatchInfrastructure infra,
        IAgentProfileStore agentProfileStore,
        IConsolidationJobPreparationService consolidationPreparer,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(infra);
        ArgumentNullException.ThrowIfNull(agentProfileStore);
        ArgumentNullException.ThrowIfNull(consolidationPreparer);
        ArgumentNullException.ThrowIfNull(logger);

        _infra = infra;
        _agentProfileStore = agentProfileStore;
        _consolidationPreparer = consolidationPreparer;
        _logger = logger;
    }

    /// <summary>
    /// Protected constructor for test subclasses that override <see cref="EnrichAsync"/>.
    /// The dependency parameters are not used when <see cref="EnrichAsync"/> is fully overridden,
    /// so <c>null</c> values are accepted without validation.
    /// </summary>
    // NOTE: [WARNING] If a test subclass calls base.EnrichAsync (rather than overriding it),
    // EnrichCoreAsync will throw NullReferenceException on _infra/_agentProfileStore with no
    // helpful message. Consider adding a guard in EnrichCoreAsync (e.g., throw InvalidOperationException
    // with a clear message when _infra is null) to fail fast with a diagnostic message rather than
    // an opaque NRE. Also, logger is not null-guarded consistently with the public constructor.
    protected internal AssignmentEnricher(
        ILogger logger,
        IConsolidationJobPreparationService? consolidationPreparer = null)
    {
        _infra = null!;
        _agentProfileStore = null!;
        _consolidationPreparer = consolidationPreparer ?? NoOpConsolidationPreparer.Instance;
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
    /// reviewers, MCP servers, and issue context; or <c>null</c> if the profile cannot be
    /// resolved or <see cref="DispatchInfrastructure.PrepareDispatchCoreAsync"/> returns null
    /// (permanent configuration failure — profile not found, provider config removed, etc.).
    /// </returns>
    /// <exception cref="Exception">
    /// Propagates any exception thrown by <see cref="EnrichCoreAsync"/> that is not an
    /// <see cref="OperationCanceledException"/>. Transient failures (DB timeout, network error)
    /// are logged at <c>Error</c> level and re-thrown so the caller can return HTTP 503,
    /// allowing the agent to retry rather than proceeding with an invalid job spec.
    /// </exception>
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
            _logger.Error(ex,
                "AssignmentEnricher: failed to enrich assignment for WorkItem with IssueIdentifier {IssueIdentifier}; returning 503 so agent can retry",
                identity.IssueIdentifier);
            throw;
        }
    }

    private async Task<JobDistributionRequest?> EnrichCoreAsync(
        JobDistributionRequest identity,
        PipelineProject project,
        CancellationToken ct)
    {
        // ── Route consolidation items to the consolidation-specific enrichment path ──
        if (identity.TaskType == WorkItemTaskType.Consolidation)
            return await EnrichConsolidationCoreAsync(identity, project, ct);

        // ── Step 1: Resolve agent profile from AgentSelector ──────────────────────
        // AgentSelector is the sorted comma-joined MatchLabels from the resolved profile.
        // Splitting it back gives us the required labels for fresh profile resolution.
        var selectorLabels = (identity.AgentSelector ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var profiles = await _agentProfileStore.LoadAgentProfilesAsync(ct);
        // NOTE: [WARNING] ProfileResolver.ResolveByRequiredLabels is called as a static method, but
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
            // NOTE: [WARNING] MergeMcpServers is called as a static method on DispatchOrchestrationService —
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

    /// <summary>
    /// Consolidation-specific enrichment path. Delegates to
    /// <see cref="IConsolidationJobPreparationService.PrepareAsync"/> to resolve provider
    /// configs, vend short-lived tokens, and determine the per-template pipeline configuration.
    /// Issue-fetch infrastructure (<see cref="DispatchInfrastructure.PrepareDispatchCoreAsync"/>)
    /// is intentionally bypassed — consolidation agents do not process issues and have no
    /// quality gates.
    /// </summary>
    private async Task<JobDistributionRequest?> EnrichConsolidationCoreAsync(
        JobDistributionRequest identity,
        PipelineProject project,
        CancellationToken ct)
    {
        // ── Step 1: Resolve agent labels from AgentSelector ───────────────────────
        // TODO: [WARNING] agentLabels is a mutable List<string> passed as IReadOnlyList<string> to
        // PrepareAsync. The callee receives a reference to the concrete list and can cast it back to
        // List<string> and mutate it. Use agentLabels.AsReadOnly() before passing to guarantee
        // immutability at the call site (mirrors the intent of the IReadOnlyList<string> parameter type).
        var agentLabels = (identity.AgentSelector ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        // ── Step 2: Resolve agent profile ────────────────────────────────────────
        // Required to populate ResolvedProfileId, AgentProviderConfigId, and McpServers.
        // Note: ConsolidationJobPreparationService.PrepareAsync also calls LoadAgentProfilesAsync
        // internally (ResolveAgentProviderConfigAsync). The double-load is accepted; the store
        // is cached so the performance impact is negligible.
        var profiles = await _agentProfileStore.LoadAgentProfilesAsync(ct);
        var profile = ProfileResolver.ResolveByRequiredLabels(profiles, agentLabels);

        if (profile is null)
        {
            _logger.Warning(
                "AssignmentEnricher: no profile matches selector [{Selector}] for consolidation assignment; cannot enrich",
                identity.AgentSelector ?? "");
            return null;
        }

        // ── Step 3: Resolve provider configs and vend tokens via consolidation service ──
        var templateId = string.IsNullOrEmpty(identity.ConsolidationTemplateId)
            ? (TemplateId?)null
            : (TemplateId)identity.ConsolidationTemplateId;

        var preparation = await _consolidationPreparer.PrepareAsync(
            identity.ConsolidationRunType ?? ConsolidationRunType.BrainConsolidation,
            templateId,
            agentLabels,
            ct);

        // ── Step 4: Build enriched JobDistributionRequest ─────────────────────────
        // QualityGateConfigs and ReviewerConfigs are intentionally not set —
        // consolidation agents do not have quality gates.
        // IssueDetail, ParsedIssue, IssueComments are identity-preserved (null in minimal
        // payload) — consolidation agents do not fetch or process issues.
        var providerConfigs = preparation.ProviderConfigs ?? [];
        return identity with
        {
            ProviderConfigs = providerConfigs,
            RepoProviderConfigId = preparation.RepoProviderConfigId,
            BrainProviderConfigId = preparation.BrainProviderConfigId,
            PipelineConfiguration = preparation.PipelineConfiguration,
            ResolvedProfileId = profile.Id,
            AgentProviderConfigId = profile.AgentProviderConfigId,
            McpServers = DispatchOrchestrationService.MergeMcpServers(profile.McpServers, project.McpServers),
            ProjectSteeringContent = project.SteeringContent,
            RepoSteeringContent = providerConfigs.TryGetProviderConfig(preparation.RepoProviderConfigId)?.SteeringContent,
        };
    }

    /// <summary>
    /// Sentinel implementation used by the protected test constructor when no real
    /// <see cref="IConsolidationJobPreparationService"/> is supplied. Throws
    /// <see cref="InvalidOperationException"/> if <see cref="PrepareAsync"/> is ever reached,
    /// providing a fail-fast diagnostic instead of a silent null-dereference.
    /// </summary>
    private sealed class NoOpConsolidationPreparer : IConsolidationJobPreparationService
    {
        public static readonly NoOpConsolidationPreparer Instance = new();

        // This method throws synchronously from a Task-returning method. The current production
        // call site (EnrichConsolidationCoreAsync) does await the result, so the exception
        // propagates correctly through the awaiter. The throw is intentional fail-fast behaviour.
        public Task<ConsolidationJobPreparationResult> PrepareAsync(
            ConsolidationRunType type,
            TemplateId? templateId,
            IReadOnlyList<string> agentLabels,
            CancellationToken ct)
            => throw new InvalidOperationException(
                "IConsolidationJobPreparationService is not available in this context. " +
                "A test subclass constructed via the protected constructor called base.EnrichAsync " +
                "for a Consolidation task type without supplying a real preparer.");
    }
}
