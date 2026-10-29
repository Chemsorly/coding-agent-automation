using System.Diagnostics;
using CodingAgent.Api.Triage;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
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
/// <para>
/// Also owns project-secret injection for the assignment payload via
/// <see cref="InjectProjectSecretsAsync"/>, which uses <see cref="ConsolidationTemplateResolver"/>
/// for template-ownership resolution (issue #2914).
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
    private readonly IProjectStore _projectStore;
    private readonly ConsolidationTemplateResolver _consolidationTemplateResolver;
    private readonly AgentRoutingStore _routing;
    private readonly JobTemplateStore _jobTemplates;
    private readonly IPipelineRunHistoryService? _runHistory;
    private readonly ITriageStore? _triageStore;
    private readonly ILogger _logger;

    /// <param name="routing">The global routing table, for work items queued before spec 053 (req 4.6).</param>
    /// <param name="jobTemplates">The Helm job templates: the tool of the routed profile's template.</param>
    /// <param name="consolidationContext">
    /// Where consolidation assignments read their run context from (see <see cref="ConsolidationRunContextSources"/>).
    /// When null, consolidation assignments go out without it.
    /// </param>
    /// <param name="triageStore">
    /// Where triage assignments read the operator's report and the triage context from. When null, triage
    /// assignments cannot be enriched.
    /// </param>
    public AssignmentEnricher(
        DispatchInfrastructure infra,
        IAgentProfileStore agentProfileStore,
        IConsolidationJobPreparationService consolidationPreparer,
        IProjectStore projectStore,
        ConsolidationTemplateResolver consolidationTemplateResolver,
        AgentRoutingStore routing,
        JobTemplateStore jobTemplates,
        ILogger logger,
        ConsolidationRunContextSources? consolidationContext = null,
        ITriageStore? triageStore = null)
    {
        ArgumentNullException.ThrowIfNull(infra);
        ArgumentNullException.ThrowIfNull(agentProfileStore);
        ArgumentNullException.ThrowIfNull(consolidationPreparer);
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(consolidationTemplateResolver);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(jobTemplates);
        ArgumentNullException.ThrowIfNull(logger);

        _infra = infra;
        _agentProfileStore = agentProfileStore;
        _consolidationPreparer = consolidationPreparer;
        _projectStore = projectStore;
        _consolidationTemplateResolver = consolidationTemplateResolver;
        _routing = routing;
        _jobTemplates = jobTemplates;
        _runHistory = consolidationContext?.RunHistory;
        _triageStore = triageStore;
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
        // TODO: [WARNING] _projectStore and _consolidationTemplateResolver are intentionally null here
        // because this protected constructor is for test subclasses that override EnrichAsync entirely.
        // If any such subclass calls InjectProjectSecretsAsync without passing a storeOverride, both
        // _projectStore and storeOverride will be null, causing secret injection to silently return the
        // original message with no log event. This is a security-adjacent silent failure: the method
        // purpose is to inject secrets, but a misconfigured caller gets a no-op with no diagnostic.
        // Consider adding a Log.Warning inside InjectProjectSecretsAsync when both store paths are null,
        // or enforcing storeOverride is non-null when called from a null-_projectStore instance.
        _projectStore = null!;
        _consolidationTemplateResolver = null!;
        _routing = null!;
        _jobTemplates = null!;
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
    /// <see cref="EnrichmentResult.Enriched"/> with fresh provider configs, QGs, reviewers, MCP servers and issue
    /// context; <see cref="EnrichmentResult.Refused"/> when the work item's route can no longer run (spec 053 req
    /// 4.5–4.7: its profile is gone or disabled, its job template changed or has no tool, no route resolves for an
    /// item queued before spec 053, or the agent config's Id collides); <see cref="EnrichmentResult.Unavailable"/> for
    /// every other reason to serve no assignment (DD6).
    /// </returns>
    /// <exception cref="Exception">
    /// Propagates any exception thrown by <see cref="EnrichCoreAsync"/> that is not an
    /// <see cref="OperationCanceledException"/>. Transient failures (DB timeout, network error)
    /// are recorded at <c>Error</c> level (issue identifier only — the caller logs the exception) and
    /// re-thrown so the caller can return HTTP 503, allowing the agent to retry rather than proceeding
    /// with an invalid job spec.
    /// </exception>
    public virtual async Task<EnrichmentResult> EnrichAsync(
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
            // Records which issue failed; the exception itself is logged once, by the GetAssignment endpoint
            // that handles it.
            _logger.Error(
                "AssignmentEnricher: failed to enrich assignment for WorkItem with IssueIdentifier {IssueIdentifier}; returning 503 so agent can retry",
                identity.IssueIdentifier);
            throw;
        }
    }

    /// <summary>The route a claim runs on: the profile, the route, the agent config built from both, the template.</summary>
    private sealed record ClaimRoute(
        AgentProfile Profile, AgentRouteSnapshot Route, ProviderConfig AgentConfig, PipelineJobTemplate? Template);

    /// <summary>
    /// Spec 053 design "Claim": the pinned route, or for a work item queued before spec 053 the current routing
    /// (source <c>claim</c>); then the profile must exist and be enabled, its job template key must still be the one
    /// the item was queued for, and the template must have a tool. Never re-resolves a pinned route, and never picks a
    /// profile no route names (D15).
    /// </summary>
    private async Task<(ClaimRoute? Route, EnrichmentResult.Refused? Refusal)> ResolveClaimRouteAsync(
        JobDistributionRequest identity, CancellationToken ct)
    {
        var templates = await _projectStore.LoadAllTemplatesAsync(ct);
        var template = identity.TaskType == WorkItemTaskType.Consolidation
            ? templates.FirstOrDefault(t => string.Equals(t.Id, identity.ConsolidationTemplateId, StringComparison.Ordinal))
            : RepositorySettings.TemplateFor(identity.RepoProviderConfigId, templates);
        var profiles = await _agentProfileStore.LoadAgentProfilesAsync(ct);
        var toolsByKey = _jobTemplates.ToolsByKey();

        AgentRouteSnapshot route;
        if (identity.Route is { } pinned)
        {
            route = pinned;
        }
        else
        {
            var routing = await _routing.LoadAsync(ct) ?? AgentRouting.Empty;
            var resolved = AgentRouteResolver.Resolve(identity.TaskType, template, routing, profiles, toolsByKey);
            if (resolved.IsRefused)
                return Refuse(identity, template, resolved.RefusalReason!, resolved.RefusalText!);
            route = resolved.Snapshot! with { Source = AgentRouteSource.Claim };
            _logger.Information(
                "Routed {TaskType} work for {TemplateName} at claim to {AgentProfile} · {Model} · {Effort} ({RouteSource}; queued before agent routing, profile {AgentProfileId}, template {TemplateId})",
                identity.TaskType, template?.Name ?? "no template", route.ProfileName, route.Model, route.Effort,
                AgentRouteLabels.Source(route.Source), route.ProfileId, template?.Id);
        }

        var profile = profiles.FirstOrDefault(p => string.Equals(p.Id, route.ProfileId, StringComparison.Ordinal) && p.Enabled);
        if (profile is null)
        {
            return Refuse(identity, template, AgentRouteRefusalReason.ProfileUnavailable,
                $"agent profile '{(string.IsNullOrEmpty(route.ProfileName) ? route.ProfileId : route.ProfileName)}' no longer exists");
        }

        var key = profile.JobTemplateKey();
        if (!string.Equals(JobTemplateStore.NormalizeLabels(key), JobTemplateStore.NormalizeLabels(identity.AgentSelector ?? ""), StringComparison.Ordinal))
        {
            return Refuse(identity, template, AgentRouteRefusalReason.TemplateChanged,
                $"the route now names job template {key}; the work was queued for {identity.AgentSelector}");
        }

        if (!toolsByKey.TryGetValue(key, out var providerType) || AgentToolCatalog.Find(providerType) is not { } tool)
            return Refuse(identity, template, AgentRouteRefusalReason.NoTool, AgentRouteResolver.NoToolText(key, profile));

        var agentConfig = AgentProviderConfigFactory.Build(profile, tool.Id, route.Model, route.Effort);
        PipelineTelemetry.SetRouteAttributes(Activity.Current, route.ProfileName, route.Model, route.Effort, AgentRouteLabels.Source(route.Source));
        return (new ClaimRoute(profile, route, agentConfig, template), null);
    }

    /// <summary>
    /// Req 4.7: the built agent config's Id must be unique among the assignment's provider configs, because the agent
    /// looks its config up by Id and takes the first match.
    /// </summary>
    private (ClaimRoute? Route, EnrichmentResult.Refused? Refusal)? CheckConfigCollision(
        JobDistributionRequest identity, ClaimRoute claim, IReadOnlyList<ProviderConfig> providerConfigs)
    {
        if (providerConfigs.Count(c => string.Equals(c.Id, claim.AgentConfig.Id, StringComparison.Ordinal)) <= 1)
            return null;
        _logger.Error(
            "AssignmentEnricher: agent config Id {AgentConfigId} of {IssueIdentifier} collides with another provider config of the assignment",
            claim.AgentConfig.Id, identity.IssueIdentifier);
        return Refuse(identity, claim.Template, AgentRouteRefusalReason.ConfigCollision,
            $"agent config Id {claim.AgentConfig.Id} collides with another provider config");
    }

    private (ClaimRoute? Route, EnrichmentResult.Refused? Refusal) Refuse(
        JobDistributionRequest identity, PipelineJobTemplate? template, string reason, string text)
    {
        _logger.Warning(
            "Refused {TaskType} work for {TemplateName} at claim: {RefusalText} ({RefusalReason}, issue {IssueIdentifier}, template {TemplateId})",
            identity.TaskType, template?.Name ?? "no template", text, reason, identity.IssueIdentifier, template?.Id);
        PipelineTelemetry.RecordRoutingRefusal(identity.TaskType, PipelineTelemetry.RoutingStages.Claim, reason);
        PipelineTelemetry.SetRouteRefusal(Activity.Current, reason, text);
        return (null, new EnrichmentResult.Refused(reason, text));
    }

    private async Task<EnrichmentResult> EnrichCoreAsync(
        JobDistributionRequest identity,
        PipelineProject project,
        CancellationToken ct)
    {
        // ── Step 1: The route (spec 053): pinned at creation, or routed now for an item queued before ──
        var (claim, refusal) = await ResolveClaimRouteAsync(identity, ct);
        if (refusal is not null)
            return refusal;

        // ── Route consolidation items to the consolidation-specific enrichment path ──
        if (identity.TaskType == WorkItemTaskType.Consolidation)
            return await EnrichConsolidationCoreAsync(identity, project, claim!, ct);
        var profile = claim!.Profile;

        // ── Step 2: Re-derive a decomposition's scope from the current configuration ──
        DecompositionProjectContext? projectContext = null;
        if (identity.TaskType == WorkItemTaskType.Decomposition)
        {
            var scope = await DecompositionScopeAsync(identity, project, ct);
            if (!scope.Usable)
                return EnrichmentResult.Unavailable.Instance;
            projectContext = scope.ProjectContext;
        }

        // ── Step 2a: A triage reads every enabled repository of its project, wherever it was reported ──
        // An operator triage's report comes from its triage, not from a tracker.
        IssueDetail? prebuiltSubject = identity.TaskType == WorkItemTaskType.Review ? ReviewedPullRequest(identity) : null;
        string? triageContextMarkdown = null;
        if (identity.TaskType == WorkItemTaskType.Triage)
        {
            var triage = await PrepareTriageAsync(identity, project, ct);
            if (triage is null)
                return EnrichmentResult.Unavailable.Instance;
            (projectContext, prebuiltSubject, triageContextMarkdown) = triage.Value;
        }

        // ── Step 2b: The project review: the project's reviewers and its other repositories, which they read ──
        // Like a decomposition's scope, it is taken from the configuration at claim time.
        IReadOnlyList<ReviewAgent> projectReviewers =
            identity.TaskType is WorkItemTaskType.Implementation or WorkItemTaskType.Review
                ? project.ActiveProjectReviewers()
                : [];
        IReadOnlyList<RepositoryTarget>? projectReviewRepositories = projectReviewers.Count > 0
            ? await _infra.BuildProjectReviewRepositoriesAsync(project, identity.RepoProviderConfigId, _logger, ct)
            : null;

        // ── Step 3: Prepare dispatch core (QGs, reviewers, issue context, provider configs, pipeline config) ──
        // The repository template's stack labels pick the quality gates and reviewers (spec 053 DD7); the agent config
        // is built from the routed profile and route (DD5). A project epic's other repositories, or the project
        // review's, get clone-only provider configs so the agent can read them.
        var coreRequest = new DispatchCoreRequest(
            StackLabels: claim.Template?.RequiredLabels ?? [],
            IssueIdentifier: identity.IssueIdentifier,
            IssueProviderId: new ProviderConfigId(identity.IssueProviderConfigId),
            RepoProviderId: new ProviderConfigId(identity.RepoProviderConfigId),
            AgentConfig: claim.AgentConfig,
            BrainProviderId: identity.BrainProviderConfigId,
            PipelineProviderId: identity.PipelineProviderConfigId,
            Project: project,
            Logger: _logger,
            AdditionalRepoProviderIds: (projectContext?.Repositories ?? projectReviewRepositories)?
                .Select(r => r.RepoProviderId).OfType<string>().ToList(),
            PullRequest: prebuiltSubject,
            NewestComments: identity.TaskType == WorkItemTaskType.Triage);

        var core = await _infra.PrepareDispatchCoreAsync(coreRequest, ct);
        if (core is null)
        {
            _logger.Warning(
                "AssignmentEnricher: PrepareDispatchCoreAsync returned null for IssueIdentifier {IssueIdentifier}",
                identity.IssueIdentifier);
            return EnrichmentResult.Unavailable.Instance;
        }

        var (resolvedQgcs, resolvedReviewerConfigs, issueContext, providerConfigs, config,
            forceRefresh, stalenessSignal, refreshCount) = core.Value;
        if (CheckConfigCollision(identity, claim, providerConfigs) is { Refusal: { } collision })
            return collision;

        await RecordTrackerTriageAttemptAsync(identity, project, issueContext.IssueDetail, ct);

        // ── Step 4: Build enriched JobDistributionRequest from identity + fresh data ──
        return new EnrichmentResult.Enriched(identity with
        {
            // Fresh-fetched mutable fields
            ProviderConfigs = providerConfigs,
            ProjectContext = projectContext,
            PipelineConfiguration = config,
            QualityGateConfigs = resolvedQgcs,
            ReviewerConfigs = resolvedReviewerConfigs,
            ProjectReviewers = projectReviewers,
            ProjectReviewRepositories = projectReviewRepositories,
            McpServers = McpServerMerge.Merge(profile.McpServers, project.McpServers),
            ResolvedProfileId = profile.Id,
            AgentProviderConfigId = claim.AgentConfig.Id,
            Route = claim.Route,
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
            TriageContextMarkdown = triageContextMarkdown,
        });
    }

    /// <summary>
    /// A decomposition's scope, re-derived from the current configuration. Only a project epic (in its project's epic
    /// tracker) gets the project's repo list. The scope is taken from the configuration at claim time, not from the
    /// payload, so a WorkItem queued before a configuration change or a deploy gets the scope its epic has now.
    /// Not usable when the assignment cannot be enriched.
    /// </summary>
    private async Task<(bool Usable, DecompositionProjectContext? ProjectContext)> DecompositionScopeAsync(
        JobDistributionRequest identity, PipelineProject project, CancellationToken ct)
    {
        if (project.IsEpicTracker(identity.IssueProviderConfigId))
        {
            var projectContext = await _infra.BuildProjectEpicContextAsync(project, _logger, ct);
            if (projectContext is null)
            {
                // Without the repo list the project epic's sub-issues would land in the epic tracker
                _logger.Error(
                    "AssignmentEnricher: project epic {IssueIdentifier} has no usable repository list in project {ProjectId}; cannot enrich assignment",
                    identity.IssueIdentifier, project.Id);
                return (false, null);
            }
            return (true, projectContext);
        }

        if (!await IsTemplateTrackerAsync(project, identity.IssueProviderConfigId, ct))
        {
            // Neither the project's epic tracker nor a template's tracker, for example because the project
            // was deleted or its epic tracker changed after the WorkItem was queued: the epic's sub-issues
            // would land in a tracker nothing polls.
            _logger.Error(
                "AssignmentEnricher: epic {IssueIdentifier} is in neither the epic tracker nor a template tracker of project {ProjectId}; cannot enrich assignment",
                identity.IssueIdentifier, project.Id);
            return (false, null);
        }
        return (true, null);
    }

    /// <summary>
    /// A tracker issue's triage attempt is recorded when it is claimed, so one that ends without a result still
    /// shows (an operator triage's attempt was recorded when it was started).
    /// </summary>
    private async Task RecordTrackerTriageAttemptAsync(
        JobDistributionRequest identity, PipelineProject project, IssueDetail issue, CancellationToken ct)
    {
        if (identity.TaskType != WorkItemTaskType.Triage || _triageStore is null
            || TriageConstants.IsOperatorTriage(identity.IssueProviderConfigId) || identity.RunId is not { } runId)
            return;

        var recorded = await _triageStore.RecordAttemptStartAsync(new TriageAttemptStart
        {
            WorkItemId = runId,
            ProjectId = project.Id,
            IssueProviderConfigId = identity.IssueProviderConfigId,
            IssueIdentifier = identity.IssueIdentifier,
            IssueTitle = issue.Title,
            IssueUrl = issue.Url,
            StartedAt = DateTimeOffset.UtcNow,
        }, ct);
        if (recorded is null)
            _logger.Warning("AssignmentEnricher: the triage of {IssueIdentifier} belongs to another project; this attempt is not recorded on it",
                identity.IssueIdentifier);
    }

    /// <summary>
    /// The parts of a triage assignment the enricher adds: the project's repositories, an operator triage's
    /// report (in place of a tracker issue), and the triage context. Null when the assignment cannot be
    /// enriched (no repository list, no triage store, or the operator triage no longer exists).
    /// </summary>
    private async Task<(DecompositionProjectContext ProjectContext, IssueDetail? Subject, string ContextMarkdown)?> PrepareTriageAsync(
        JobDistributionRequest identity, PipelineProject project, CancellationToken ct)
    {
        var projectContext = await _infra.BuildProjectEpicContextAsync(project, _logger, ct);
        if (projectContext is null)
        {
            _logger.Error(
                "AssignmentEnricher: triage {IssueIdentifier} has no usable repository list in project {ProjectId}; cannot enrich assignment",
                identity.IssueIdentifier, project.Id);
            return null;
        }

        if (_triageStore is null)
        {
            _logger.Error("AssignmentEnricher: triage {IssueIdentifier} cannot be enriched without a triage store", identity.IssueIdentifier);
            return null;
        }

        var isOperatorTriage = TriageConstants.IsOperatorTriage(identity.IssueProviderConfigId);
        TriageRecord? triage;
        IssueDetail? subject = null;
        if (isOperatorTriage)
        {
            var triageId = TriageConstants.TryParseTriageId(identity.IssueIdentifier);
            triage = triageId is null ? null : await _triageStore.GetAsync(triageId.Value, ct);
            if (triage?.Request is null)
            {
                _logger.Error("AssignmentEnricher: operator triage {IssueIdentifier} no longer exists; cannot enrich assignment",
                    identity.IssueIdentifier);
                return null;
            }
            subject = TriageContextRenderer.ToIssueDetail(triage);
        }
        else
        {
            triage = await _triageStore.GetByIssueAsync(identity.IssueProviderConfigId, identity.IssueIdentifier, ct);
            if (triage is not null && triage.ProjectId != project.Id)
            {
                // A tracker two projects poll: the earlier attempts belong to the other project and stay out of this run
                _logger.Warning(
                    "AssignmentEnricher: the triage of {IssueIdentifier} belongs to project {OtherProjectId}, not {ProjectId}; its earlier attempts are left out",
                    identity.IssueIdentifier, triage.ProjectId, project.Id);
                triage = null;
            }
        }

        // Every triage the project still has: the retention sweep bounds them to the retention window
        var others = await _triageStore.ListRecentAsync(
            project.Id, DateTimeOffset.MinValue, TriageConstants.MaxHistoryContext, triage?.Id, ct);
        var markdown = TriageContextRenderer.RenderContext(triage, identity.RunId, others, reportToTracker: !isOperatorTriage);
        return (projectContext, subject, markdown);
    }

    /// <summary>
    /// The pull request a review is about, as it was dispatched. A review's subject is a pull request in the
    /// repository, so its context never comes from the tracker, where #N may be an unrelated issue.
    /// </summary>
    private static IssueDetail ReviewedPullRequest(JobDistributionRequest identity) => new()
    {
        Identifier = identity.IssueIdentifier,
        Title = identity.IssueDetail?.Title ?? "",
        Description = identity.ReviewPrDescription ?? "",
        Labels = [],
        Url = identity.LinkedPullRequest?.Url
    };

    /// <summary>Whether <paramref name="issueProviderConfigId"/> is the tracker of an enabled template of the project.</summary>
    private async Task<bool> IsTemplateTrackerAsync(PipelineProject project, string issueProviderConfigId, CancellationToken ct)
    {
        var templates = await _projectStore.LoadTemplatesForProjectAsync(project.Id, ct);
        return templates.Any(t => t.Enabled && t.IssueProviderId == issueProviderConfigId);
    }

    /// <summary>
    /// Consolidation-specific enrichment path. Delegates to
    /// <see cref="IConsolidationJobPreparationService.PrepareAsync"/> to resolve provider
    /// configs, vend short-lived tokens, and determine the per-template pipeline configuration.
    /// Issue-fetch infrastructure (<see cref="DispatchInfrastructure.PrepareDispatchCoreAsync"/>)
    /// is intentionally bypassed — consolidation agents do not process issues and have no
    /// quality gates. <paramref name="claim"/> is the route <see cref="ResolveClaimRouteAsync"/> resolved.
    /// </summary>
    private async Task<EnrichmentResult> EnrichConsolidationCoreAsync(
        JobDistributionRequest identity,
        PipelineProject project,
        ClaimRoute claim,
        CancellationToken ct)
    {
        var profile = claim.Profile;

        // ── Step 2: Resolve provider configs and vend tokens via consolidation service ──
        var templateId = string.IsNullOrEmpty(identity.ConsolidationTemplateId)
            ? (TemplateId?)null
            : (TemplateId)identity.ConsolidationTemplateId;

        var runType = identity.ConsolidationRunType ?? ConsolidationRunType.BrainConsolidation;
        var preparation = await _consolidationPreparer.PrepareAsync(
            runType,
            templateId,
            claim.AgentConfig,
            ct);

        var lastSuccessfulRunUtc = await ReadConsolidationHistoryAsync(identity, ct);

        // ── Step 3: Build enriched JobDistributionRequest ─────────────────────────
        // QualityGateConfigs and ReviewerConfigs are intentionally not set —
        // consolidation agents do not have quality gates.
        // IssueDetail, ParsedIssue, IssueComments are identity-preserved (null in minimal
        // payload) — consolidation agents do not fetch or process issues.
        var providerConfigs = preparation.ProviderConfigs ?? [];
        if (CheckConfigCollision(identity, claim, providerConfigs) is { Refusal: { } collision })
            return collision;
        return new EnrichmentResult.Enriched(identity with
        {
            ProviderConfigs = providerConfigs,
            RepoProviderConfigId = preparation.RepoProviderConfigId,
            BrainProviderConfigId = preparation.BrainProviderConfigId,
            PipelineConfiguration = preparation.PipelineConfiguration,
            ResolvedProfileId = profile.Id,
            AgentProviderConfigId = claim.AgentConfig.Id,
            Route = claim.Route,
            McpServers = McpServerMerge.Merge(profile.McpServers, project.McpServers),
            ProjectSteeringContent = project.SteeringContent,
            RepoSteeringContent = providerConfigs.TryGetProviderConfig(preparation.RepoProviderConfigId)?.SteeringContent,
            ConsolidationLastSuccessfulRunUtc = lastSuccessfulRunUtc,
            // Brain v2 (spec 054, req 10, task 3.6)
            StackLabels = claim.Template?.RequiredLabels,
        });
    }

    /// <summary>
    /// Reads the consolidation's last successful run of the same scope. Best effort: a failure is logged
    /// and the assignment goes out without it (brain consolidation then reviews the whole brain).
    /// </summary>
    private async Task<DateTimeOffset?> ReadConsolidationHistoryAsync(
        JobDistributionRequest identity, CancellationToken ct)
    {
        if (_runHistory is null)
            return null;

        try
        {
            return await ConsolidationRunHistoryContext.GetLastSuccessfulRunAsync(_runHistory, identity.IssueIdentifier, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "AssignmentEnricher: could not read the run history for consolidation {Key}; dispatching without it",
                identity.IssueIdentifier.Value);
            return null;
        }
    }

    /// <summary>
    /// Injects project secrets into the assignment message at delivery time.
    /// Secrets are not serialized in the payload for security; they are fetched fresh here.
    /// <para>
    /// Two resolution paths:
    /// <list type="bullet">
    ///   <item>
    ///     <term>Direct project lookup</term>
    ///     <description>
    ///     When <c>request.ProjectId</c> is non-null, the project is fetched directly via
    ///     <see cref="IProjectStore.GetProjectByIdAsync"/> and its secrets are injected.
    ///     </description>
    ///   </item>
    ///   <item>
    ///     <term>Template-ownership fallback (consolidation)</term>
    ///     <description>
    ///     When <c>request.ProjectId</c> is null but <c>request.ConsolidationTemplateId</c> is non-null,
    ///     the owning project is resolved via <see cref="ConsolidationTemplateResolver.ResolveTemplateWithProjectAsync"/>
    ///     rather than a reimplemented ownership-resolution loop (issue #2914).
    ///     </description>
    ///   </item>
    /// </list>
    /// </para>
    /// </summary>
    internal async Task<JobAssignmentMessage> InjectProjectSecretsAsync(
        JobAssignmentMessage message,
        JobDistributionRequest request,
        CancellationToken ct,
        IProjectStore? storeOverride = null)
    {
        // Use the injected project store, or fall back to the override passed at call time.
        // The override is needed for test subclasses constructed via the protected logger-only
        // constructor (e.g. FakeAssignmentEnricher in GetAssignmentTests.cs) that set
        // _projectStore = null! — passing the store as a parameter preserves testability.
        var store = _projectStore ?? storeOverride;

        if (request.ProjectId.HasValue)
        {
            if (store is null)
                return message;

            var project = await store.GetProjectByIdAsync(request.ProjectId.Value.ToString(), ct);
            if (project is null)
            {
                // The project was deleted after the work item was queued (issue #2639).
                _logger.Warning(
                    "InjectProjectSecretsAsync: project {ProjectId} not found — WorkItem {JobId} will run without ProjectSecrets",
                    request.ProjectId.Value, message.JobId);
                return message;
            }

            if (project.Secrets is { Count: > 0 })
                return message with { ProjectSecrets = project.Secrets };

            return message;
        }

        // Consolidation fallback: when ProjectId is null but a ConsolidationTemplateId is set,
        // resolve the owning project via ConsolidationTemplateResolver (delegates the
        // ownership-resolution loop that was previously reimplemented inline here — issue #2914).
        if (!string.IsNullOrEmpty(request.ConsolidationTemplateId))
            return await InjectConsolidationOwnerSecretsAsync(message, request.ConsolidationTemplateId, store, ct);

        return message;
    }

    private async Task<JobAssignmentMessage> InjectConsolidationOwnerSecretsAsync(
        JobAssignmentMessage message,
        string consolidationTemplateId,
        IProjectStore? store,
        CancellationToken ct)
    {
        // Use the injected resolver, or create one from the store if the enricher was
        // constructed via the protected test-only constructor (which sets both to null).
        var resolver = _consolidationTemplateResolver
            ?? (store is not null ? new ConsolidationTemplateResolver(store) : null);

        if (resolver is null || store is null)
            return message;

        var (_, _, projectId) = await resolver
            .ResolveTemplateWithProjectAsync(new TemplateId(consolidationTemplateId), ct);

        if (projectId is null)
            return message;

        var owningProject = await store.GetProjectByIdAsync(projectId, ct);
        if (owningProject?.Secrets is { Count: > 0 })
            return message with { ProjectSecrets = owningProject.Secrets };

        return message;
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
            ProviderConfig? agentConfig,
            CancellationToken ct)
            => throw new InvalidOperationException(
                "IConsolidationJobPreparationService is not available in this context. " +
                "A test subclass constructed via the protected constructor called base.EnrichAsync " +
                "for a Consolidation task type without supplying a real preparer.");
    }
}

/// <summary>
/// Where <see cref="AssignmentEnricher"/> reads a consolidation assignment's run context from.
/// </summary>
/// <param name="RunHistory">
/// The run history consolidation assignments read their last successful run from. When null,
/// consolidation assignments go out without it.
/// </param>
public sealed record ConsolidationRunContextSources(
    IPipelineRunHistoryService? RunHistory = null);
