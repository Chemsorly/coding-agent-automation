using CodingAgent.Api.Client;
using CodingAgent.Infrastructure.Common;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration.Dispatch;

/// <summary>
/// Aggregate that bundles shared dispatch-path dependencies used by
/// <see cref="DispatchOrchestrationService"/>.
/// Reduces constructor parameter count by grouping services that always travel together:
/// provider config building, profile resolution, token vending, and label operations.
/// <para>
/// Also hosts <see cref="PrepareDispatchCoreAsync"/> — the single consolidated method
/// for the shared dispatch preparation sequence (QG/reviewer resolution, issue context,
/// provider config preparation, and pipeline config resolution).
/// <see cref="DispatchOrchestrationService"/> delegates to this method.
/// </para>
/// <para>
/// Registered as a singleton in DI. Consumers access individual services via properties.
/// </para>
/// </summary>
public class DispatchInfrastructure
{
    public ITokenVendingService TokenVending { get; }
    public IProviderFactory ProviderFactory { get; }
    public ILabelService LabelService { get; }
    public DispatchResolutionService Resolution { get; }

    /// <summary>
    /// Optional: used for agent-error staleness detection. Null in test/local contexts.
    /// </summary>
    private readonly IPipelineApiWorkItemClient? _workItemClient;

    public DispatchInfrastructure(
        ITokenVendingService tokenVending,
        IProviderFactory providerFactory,
        ILabelService labelService,
        DispatchResolutionService resolution,
        IPipelineApiWorkItemClient? workItemClient = null)
    {
        ArgumentNullException.ThrowIfNull(tokenVending);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(labelService);
        ArgumentNullException.ThrowIfNull(resolution);

        TokenVending = tokenVending;
        ProviderFactory = providerFactory;
        LabelService = labelService;
        Resolution = resolution;
        _workItemClient = workItemClient;
    }

    /// <summary>
    /// Protected constructor for test subclasses that override <see cref="PrepareDispatchCoreAsync"/>.
    /// Dependencies are not used when the method is fully overridden, so null values are accepted
    /// without validation.
    /// </summary>
    protected DispatchInfrastructure()
    {
        TokenVending = null!;
        ProviderFactory = null!;
        LabelService = null!;
        Resolution = null!;
    }

    // ── Config Resolution ──────────────────────────────────────────────────────────

    /// <summary>
    /// Prepares provider configs and resolves the pipeline configuration for a dispatch.
    /// Shared by implementation and review paths which both use the load-and-resolve overload.
    /// The decomposition path does NOT use this helper because it loads config early for
    /// <see cref="PipelineConfiguration.WorkspaceBaseDirectory"/> access before run creation.
    /// </summary>
    internal async Task<(IReadOnlyList<ProviderConfig> ProviderConfigs, PipelineConfiguration Config)> PrepareAndResolveConfigAsync(
        ProviderConfigId repoProviderId,
        string agentProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        PipelineProject project,
        ILogger logger,
        CancellationToken ct)
    {
        var providerConfigs = await PrepareProviderConfigsAsync(
            repoProviderId, agentProviderId, brainProviderId, pipelineProviderId, logger, ct);

        var config = await PipelineConfigurationResolver.ResolveAsync(
            Resolution.ConfigStore.LoadPipelineConfigAsync,
            Resolution.ConfigStore.LoadAllTemplatesAsync,
            project, repoProviderId, providerConfigs, ct);

        return (providerConfigs, config);
    }

    /// <summary>
    /// Builds a synthetic <see cref="IssueDetail"/> and <see cref="ParsedIssue"/> from metadata
    /// (e.g., PR title/description or epic title). Used by review and decomposition dispatch paths
    /// which don't have a real issue to fetch from the provider.
    /// </summary>
    internal static (IssueDetail IssueDetail, ParsedIssue ParsedIssue) BuildSyntheticIssueContext(
        string identifier, string title, string? description)
    {
        var desc = description ?? string.Empty;
        var issueDetail = new IssueDetail
        {
            Identifier = identifier,
            Title = title,
            Description = desc,
            Labels = Array.Empty<string>()
        };
        var parsedIssue = new IssueDescriptionParser().Parse(desc);
        return (issueDetail, parsedIssue);
    }

    // ── Provider Config Building (inlined from ProviderConfigBuilder) ──────────────

    /// <summary>
    /// Builds the provider configs list and prepares tokens via the token vending service.
    /// </summary>
    /// <remarks>
    /// <paramref name="additionalRepoProviderIds"/> lists the other project repositories a project epic's
    /// decomposition clones. The agent only reads them, so they get read-only tokens and none of their
    /// secrets or setup steps; see <see cref="ITokenVendingService.PrepareReadOnlyCloneConfigsAsync"/>.
    /// </remarks>
    internal async Task<IReadOnlyList<ProviderConfig>> PrepareProviderConfigsAsync(
        ProviderConfigId repoProviderId,
        string agentProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        ILogger logger,
        CancellationToken ct,
        IEnumerable<string>? additionalRepoProviderIds = null)
    {
        var rawConfigs = await BuildAgentProviderConfigsAsync(
            repoProviderId, agentProviderId, brainProviderId, pipelineProviderId, logger, ct);
        var jobConfigs = await TokenVending.PrepareAgentConfigsAsync(rawConfigs, repoProviderId.Value, ct);

        if (additionalRepoProviderIds is null)
            return jobConfigs;

        var additionalConfigs = await ResolveAdditionalRepoConfigsAsync(
            additionalRepoProviderIds, excludedIds: [repoProviderId.Value, brainProviderId], logger, ct);
        var cloneConfigs = await TokenVending.PrepareReadOnlyCloneConfigsAsync(additionalConfigs, ct);
        return [.. jobConfigs, .. cloneConfigs];
    }

    /// <summary>
    /// Builds the list of provider configs to send to the agent.
    /// Excludes issue provider configs (agents don't get issue access).
    /// </summary>
    internal async Task<IReadOnlyList<ProviderConfig>> BuildAgentProviderConfigsAsync(
        ProviderConfigId repoProviderId,
        string agentProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        ILogger logger,
        CancellationToken ct)
    {
        var configs = new List<ProviderConfig>();

        var repoConfigs = await Resolution.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
        var repoConfig = await ProviderConfigResolver.ResolveAsync(
            Resolution.ConfigStore, repoProviderId.Value, ProviderKind.Repository, repoConfigs, required: true, logger, ct);
        configs.Add(repoConfig!);

        var agentConfigs = await Resolution.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Agent, ct);
        var agentConfig = await ProviderConfigResolver.ResolveAsync(
            Resolution.ConfigStore, agentProviderId, ProviderKind.Agent, agentConfigs, required: true, logger, ct);
        configs.Add(agentConfig!);

        var brainConfig = await ResolveOptionalProviderConfigAsync(brainProviderId, ProviderKind.Repository, repoConfigs, logger, ct);
        if (brainConfig is not null)
            configs.Add(brainConfig);

        if (!string.IsNullOrEmpty(pipelineProviderId))
        {
            var pipelineConfigs = await Resolution.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Pipeline, ct);
            var pipelineConfig = await ResolveOptionalProviderConfigAsync(pipelineProviderId, ProviderKind.Pipeline, pipelineConfigs, logger, ct);
            if (pipelineConfig is not null)
                configs.Add(pipelineConfig);
        }

        return configs.AsReadOnly();
    }

    /// <summary>
    /// Resolves the repository configs for <paramref name="additionalRepoProviderIds"/>, skipping
    /// empty ids, duplicates, and <paramref name="excludedIds"/> (the job's own repository and brain,
    /// which are already in the job's configs).
    /// </summary>
    private async Task<IReadOnlyList<ProviderConfig>> ResolveAdditionalRepoConfigsAsync(
        IEnumerable<string> additionalRepoProviderIds,
        IEnumerable<string?> excludedIds,
        ILogger logger,
        CancellationToken ct)
    {
        var configs = new List<ProviderConfig>();
        var repoConfigs = await Resolution.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
        var addedIds = excludedIds.OfType<string>().ToHashSet();

        foreach (var additionalId in additionalRepoProviderIds)
        {
            if (string.IsNullOrEmpty(additionalId) || !addedIds.Add(additionalId))
                continue; // skip null/empty or duplicates

            var additionalConfig = await ProviderConfigResolver.ResolveAsync(
                Resolution.ConfigStore, additionalId, ProviderKind.Repository, repoConfigs, required: false, logger, ct);
            if (additionalConfig is not null)
                configs.Add(additionalConfig);
        }

        return configs;
    }

    private async Task<ProviderConfig?> ResolveOptionalProviderConfigAsync(
        string? providerId, ProviderKind kind,
        IReadOnlyList<ProviderConfig> existingConfigs,
        ILogger logger, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(providerId))
            return null;

        return await ProviderConfigResolver.ResolveAsync(
            Resolution.ConfigStore, providerId, kind, existingConfigs, required: false, logger, ct);
    }

    // ── Project epic context ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds the <see cref="DecompositionProjectContext"/> of a project epic (1E-006): the project's
    /// repositories the agent may route sub-issues to, and clone. Returns null when the project has
    /// no usable template or the templates cannot be loaded. See <see cref="BuildRepositoryTargets"/>.
    /// </summary>
    internal virtual async Task<DecompositionProjectContext?> BuildProjectEpicContextAsync(
        PipelineProject project, ILogger logger, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(project.Id) || project.TemplateIds is not { Count: > 0 })
            return null;

        try
        {
            var allTemplates = await Resolution.ConfigStore.LoadAllTemplatesAsync(ct);
            var repositories = BuildRepositoryTargets(project, allTemplates, logger);

            if (repositories.Count == 0)
                return null;

            return new DecompositionProjectContext
            {
                ProjectName = project.Name,
                Repositories = repositories
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning(ex,
                "DispatchInfrastructure: failed to build DecompositionProjectContext for project {ProjectId}",
                project.Id);
            return null;
        }
    }

    /// <summary>
    /// The project's other repositories for the project review: the project's enabled templates (see
    /// <see cref="BuildRepositoryTargets"/>) without the run's own repository, each repository once. Empty when the
    /// project has no other repository or its templates cannot be loaded; the project reviewers then review without
    /// clones.
    /// </summary>
    internal virtual async Task<IReadOnlyList<RepositoryTarget>> BuildProjectReviewRepositoriesAsync(
        PipelineProject project, string ownRepoProviderId, ILogger logger, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(project.Id) || project.TemplateIds is not { Count: > 0 })
            return [];

        try
        {
            var allTemplates = await Resolution.ConfigStore.LoadAllTemplatesAsync(ct);
            return OtherRepositories(project, allTemplates, ownRepoProviderId, logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning(ex,
                "DispatchInfrastructure: failed to list project {ProjectId}'s repositories for the project review; it runs without clones",
                project.Id);
            return [];
        }
    }

    /// <summary>The repositories of the project's enabled templates other than <paramref name="ownRepoProviderId"/>, each once.</summary>
    internal static List<RepositoryTarget> OtherRepositories(
        PipelineProject project, IReadOnlyList<PipelineJobTemplate> allTemplates, string ownRepoProviderId, ILogger logger) =>
        BuildRepositoryTargets(project, allTemplates, logger)
            .Where(r => !string.IsNullOrEmpty(r.RepoProviderId)
                && !string.Equals(r.RepoProviderId, ownRepoProviderId, StringComparison.Ordinal))
            .DistinctBy(r => r.RepoProviderId)
            .ToList();

    /// <summary>
    /// Lists the project's enabled templates as routing targets, in project order (by name). A template's name is
    /// the routing key the agent writes (<c>targetRepository</c>), so a template is left out when its name
    /// is empty or an earlier template in the project already uses it (possible only for templates saved before
    /// <see cref="TemplateBindingRules"/> were enforced).
    /// </summary>
    internal static List<RepositoryTarget> BuildRepositoryTargets(
        PipelineProject project, IReadOnlyList<PipelineJobTemplate> allTemplates, ILogger logger)
    {
        var templatesById = allTemplates.ToLookup(t => t.Id);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var repositories = new List<RepositoryTarget>();

        foreach (var template in project.TemplateIds.Distinct().SelectMany(id => templatesById[id]))
        {
            if (!template.Enabled)
                continue;

            if (string.IsNullOrWhiteSpace(template.Name) || !names.Add(template.Name))
            {
                logger.Warning(
                    "DispatchInfrastructure: template {TemplateId} is left out of project {ProjectId}'s repository list: its name '{TemplateName}' is empty or already used by an earlier template",
                    template.Id, project.Id, LogSanitizer.SanitizeForLog(template.Name));
                continue;
            }

            repositories.Add(new RepositoryTarget
            {
                TemplateName = template.Name,
                IssueProviderId = template.IssueProviderId,
                RepoProviderId = template.RepoProviderId,
                Description = string.Empty,
                DecompositionEnabled = template.DecompositionEnabled,
                Labels = []
            });
        }

        return repositories;
    }

    // ── Issue Context Building (inlined from IssueContextBuilder) ─────────────────

    /// <summary>
    /// Builds the context of a review from the pull request it is about. A review's subject is a pull
    /// request in the repository, not an issue in the tracker: in GitLab, and whenever the tracker is a
    /// different system than the repository, issue #N and pull request !N are different things. So the
    /// pull request's own title and description stand in for the issue, and there are no issue comments,
    /// no existing analysis and no staleness signals.
    /// </summary>
    internal IssueContextResult BuildPullRequestContext(IssueDetail pullRequest)
    {
        IReadOnlyList<ImageReference> images = [];
        try
        {
            images = ExtractImages(pullRequest.Description, [], pullRequest.Identifier);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex,
                "BuildPullRequestContext: image extraction failed for pull request {Identifier} — continuing with empty image list",
                pullRequest.Identifier);
        }

        var detail = new IssueDetail
        {
            Identifier = pullRequest.Identifier,
            Title = pullRequest.Title,
            Description = pullRequest.Description,
            Labels = pullRequest.Labels,
            Images = images,
            Url = pullRequest.Url
        };
        return new IssueContextResult(
            detail, new IssueDescriptionParser().Parse(detail.Description), [],
            ExistingAnalysis: null, ForceRefreshAnalysis: false, StalenessSignal: null, RefreshCount: 0);
    }

    /// <summary>
    /// Pre-fetches issue details, comments, and detects existing analysis with staleness signals
    /// (gate_rejection, gate_wont_do, agent_error_since). Returns <c>null</c> if the issue
    /// provider config is not found.
    /// </summary>
    internal async Task<IssueContextResult?> BuildIssueContextAsync(
        IssueIdentifier issueIdentifier,
        ProviderConfigId issueProviderId,
        CancellationToken ct,
        bool newestComments = false)
    {
        var issueConfig = await Resolution.ConfigStore
            .GetProviderConfigByIdAsync(issueProviderId.Value, ProviderKind.Issue, ct);
        if (issueConfig is null)
            return null;

        IssueDetail issueDetail;
        ParsedIssue parsedIssue;
        IReadOnlyList<IssueComment> issueComments;
        await using (var issueProvider = ProviderFactory.CreateIssueProvider(issueConfig))
        {
            issueDetail = await issueProvider.GetIssueAsync(issueIdentifier, ct);
            parsedIssue = new IssueDescriptionParser().Parse(issueDetail.Description);
            var allComments = await issueProvider.ListCommentsAsync(issueIdentifier, ct);
            // Cap at 50 comments per REQ-4.4 (the newest ones when asked: a triage needs the latest feedback)
            issueComments = allComments.Count <= 50
                ? allComments
                : newestComments
                    ? allComments.Skip(allComments.Count - 50).ToList().AsReadOnly()
                    : allComments.Take(50).ToList().AsReadOnly();
        }

        // Extract images from body + comments (mirrors FetchIssueStep pattern).
        // Non-fatal: a malformed attachment must not permanently block dispatch.
        IReadOnlyList<ImageReference> images = [];
        try
        {
            images = ExtractImages(issueDetail.Description, issueComments, issueIdentifier);
        }
        catch (Exception ex)
        {
            // TODO: use injected ILogger instead of the global static Serilog.Log.Warning — the
            // static call makes this untestable without swapping Log.Logger globally, and silently
            // drops the warning in contexts where Log.Logger has not been configured. All other
            // callers in this class accept ILogger as a parameter; align this catch block once
            // BuildIssueContextAsync receives an ILogger parameter.
            Serilog.Log.Warning(ex,
                "BuildIssueContextAsync: image extraction failed for {IssueIdentifier} — continuing with empty image list",
                issueIdentifier);
        }
        issueDetail = new IssueDetail
        {
            Description = issueDetail.Description,
            Identifier = issueDetail.Identifier,
            Labels = issueDetail.Labels,
            Title = issueDetail.Title,
            Images = images,
            Url = issueDetail.Url
        };

        var (existingAnalysis, forceRefreshAnalysis, stalenessSignal) =
            await DetectAnalysisStalenessAsync(issueComments, issueIdentifier, issueProviderId, ct);

        // Body-hash staleness check (body_changed signal).
        // AnalysisBodyHash.Extract returns null for legacy comments without an embedded hash,
        // so this is silently skipped for those — no false positives.
        // The !forceRefreshAnalysis guard ensures body_changed does not override a higher-priority
        // signal (gate_rejection, gate_wont_do, agent_error_since).
        if (!forceRefreshAnalysis && existingAnalysis is not null)
        {
            var embeddedHash = AnalysisBodyHash.Extract(existingAnalysis);
            if (embeddedHash is not null)
            {
                var currentHash = AnalysisBodyHash.Compute(issueDetail.Description);
                if (embeddedHash != currentHash)
                {
                    forceRefreshAnalysis = true;
                    stalenessSignal = "body_changed";
                }
            }
        }

        return new IssueContextResult(
            issueDetail, parsedIssue, issueComments,
            existingAnalysis, forceRefreshAnalysis, stalenessSignal, 0);
    }

    /// <summary>
    /// Detects whether an existing analysis is stale by inspecting gate-rejection / gate-wont-do
    /// signals from comments and (if a work-item client is available) agent-error history.
    /// Mirrors <see cref="CheckCommitCountStalenessAsync"/> which was also extracted to reduce
    /// cognitive complexity (S3776).
    /// </summary>
    internal async Task<(string? ExistingAnalysis, bool ForceRefresh, string? StalenessSignal)>
        DetectAnalysisStalenessAsync(
            IReadOnlyList<IssueComment> issueComments,
            IssueIdentifier issueIdentifier,
            ProviderConfigId issueProviderId,
            CancellationToken ct)
    {
        string? existingAnalysis = null;
        bool forceRefreshAnalysis = false;
        string? stalenessSignal = null;

        var analysisComment = issueComments
            .Where(c => c.Body.Contains(CommentMarkers.AnalysisHeader))
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefault();

        if (analysisComment is not null)
        {
            existingAnalysis = analysisComment.Body;
            var gateRejection = issueComments
                .FirstOrDefault(c => c.Body.Contains(CommentMarkers.GateRejection));
            var gateWontDo = issueComments
                .FirstOrDefault(c => c.Body.Contains(CommentMarkers.GateWontDo));
            if (gateRejection?.CreatedAt > analysisComment.CreatedAt)
            {
                forceRefreshAnalysis = true;
                stalenessSignal = "gate_rejection";
            }
            else if (gateWontDo?.CreatedAt > analysisComment.CreatedAt)
            {
                forceRefreshAnalysis = true;
                stalenessSignal = "gate_wont_do";
            }
            // Agent-error-since check (1F-001): if the agent errored since the last analysis,
            // force a fresh analysis run. Uses the work item client to query the DB via the API.
            // Note: checked after the if/else-if chain so forceRefreshAnalysis is guaranteed false here.
        }

        // analysisComment is not null (not existingAnalysis is not null) — preserves the intent
        // that GetStalenessAsync is only called when a source analysis comment actually exists.
        if (!forceRefreshAnalysis && _workItemClient is not null && analysisComment is not null)
        {
            try
            {
                var staleness = await _workItemClient.GetStalenessAsync(
                    issueIdentifier.Value, issueProviderId.Value, analysisComment.CreatedAt, ct);
                if (staleness?.HasAgentErrorSince == true)
                {
                    forceRefreshAnalysis = true;
                    stalenessSignal = "agent_error_since";
                }
            }
            catch (Exception ex)
            {
                // Non-fatal: if the staleness check fails, proceed without forcing refresh.
                Serilog.Log.Warning(ex,
                    "DispatchInfrastructure: agent-error staleness check failed for {IssueIdentifier}; proceeding without refresh",
                    issueIdentifier);
            }
        }

        return (existingAnalysis, forceRefreshAnalysis, stalenessSignal);
    }

    /// <summary>
    /// Extracts image references from an issue body and its comments.
    /// Virtual so that test subclasses can override it to simulate extraction failure
    /// without requiring <see cref="IssueImageExtractor"/> to be mockable.
    /// </summary>
    protected virtual IReadOnlyList<ImageReference> ExtractImages(
        string description,
        IReadOnlyList<IssueComment> comments,
        IssueIdentifier issueIdentifier)
    {
        return new IssueImageExtractor().Extract(description, comments, issueIdentifier, ImageSourceKind.Issue);
    }

    /// <summary>
    /// Holds the pre-fetched issue context needed to build a <see cref="JobAssignmentMessage"/>
    /// or a <see cref="DispatchPreparationResult"/>. Produced by <see cref="BuildIssueContextAsync"/>.
    /// </summary>
    internal sealed record IssueContextResult(
        IssueDetail IssueDetail,
        ParsedIssue ParsedIssue,
        IReadOnlyList<IssueComment> IssueComments,
        string? ExistingAnalysis,
        bool ForceRefreshAnalysis,
        string? StalenessSignal,
        int RefreshCount);

    // ── Consolidated Dispatch Preparation ─────────────────────────────────────────

    /// <summary>
    /// Consolidated dispatch preparation logic used by <see cref="DispatchOrchestrationService"/>.
    /// <para>
    /// Performs the full shared sequence: resolve quality gates → resolve reviewers →
    /// build issue context → prepare provider configs → resolve pipeline configuration →
    /// evaluate staleness signals.
    /// </para>
    /// </summary>
    /// <returns>
    /// A tuple containing all resolved dispatch artifacts, or <c>null</c> if issue context
    /// building failed (provider config not found).
    /// </returns>
    internal virtual async Task<(
        IReadOnlyList<QualityGateConfiguration> QualityGates,
        IReadOnlyList<ReviewerConfiguration> Reviewers,
        IssueContextResult IssueContext,
        IReadOnlyList<ProviderConfig> ProviderConfigs,
        PipelineConfiguration Config,
        bool ForceRefresh,
        string? StalenessSignal,
        int RefreshCount)?> PrepareDispatchCoreAsync(
        DispatchCoreRequest request,
        CancellationToken ct)
    {
        var requiredLabels = request.RequiredLabels;
        var issueIdentifier = request.IssueIdentifier;
        var issueProviderId = request.IssueProviderId;
        var repoProviderId = request.RepoProviderId;
        var agentProviderId = request.AgentProviderId;
        var brainProviderId = request.BrainProviderId;
        var pipelineProviderId = request.PipelineProviderId;
        var project = request.Project;
        var logger = request.Logger;
        // ── Step 1: Resolve quality gate and reviewer configurations ──
        var resolvedQgcs = await Resolution.ResolveQualityGatesAsync(requiredLabels, ct);
        var resolvedReviewerConfigs = await Resolution.ResolveReviewersAsync(requiredLabels, ct);

        // ── Step 2: Build issue context (pre-fetch details, comments, basic staleness) ──
        // A review is about a pull request, so its context comes from the pull request, never from the tracker.
        var issueContext = request.PullRequest is { } pullRequest
            ? BuildPullRequestContext(pullRequest)
            : await BuildIssueContextAsync(issueIdentifier, issueProviderId, ct, request.NewestComments);
        if (issueContext is null)
        {
            logger.Error("Issue provider config '{ConfigId}' not found", issueProviderId);
            return null;
        }

        // ── Step 3: Prepare provider configs and resolve pipeline configuration ──
        var providerConfigs = await PrepareProviderConfigsAsync(
            repoProviderId, agentProviderId, brainProviderId, pipelineProviderId, logger, ct,
            request.AdditionalRepoProviderIds);

        var config = await PipelineConfigurationResolver.ResolveAsync(
            Resolution.ConfigStore.LoadPipelineConfigAsync,
            Resolution.ConfigStore.LoadAllTemplatesAsync,
            project, repoProviderId, providerConfigs, ct);

        // ── Step 4: Carry forward staleness signals from issue context ──
        var forceRefresh = issueContext.ForceRefreshAnalysis;
        var stalenessSignal = issueContext.StalenessSignal;
        var refreshCount = issueContext.RefreshCount;

        // ── Step 4.5: Commit-count staleness (1F-001) ──
        if (!forceRefresh && issueContext.ExistingAnalysis is not null && config.AnalysisCommitThreshold > 0)
        {
            (forceRefresh, stalenessSignal) = await CheckCommitCountStalenessAsync(
                issueContext, repoProviderId, providerConfigs, config.AnalysisCommitThreshold, request.Logger, ct);
        }

        return (resolvedQgcs, resolvedReviewerConfigs, issueContext, providerConfigs, config,
            forceRefresh, stalenessSignal, refreshCount);
    }

    /// <summary>
    /// Checks whether enough commits have landed since the last analysis to force a refresh.
    /// Extracted from <see cref="PrepareDispatchCoreAsync"/> to reduce cognitive complexity (S3776).
    /// Returns the updated (forceRefresh, stalenessSignal) pair.
    /// </summary>
    internal async Task<(bool ForceRefresh, string? StalenessSignal)> CheckCommitCountStalenessAsync(
        IssueContextResult issueContext,
        ProviderConfigId repoProviderId,
        IReadOnlyList<ProviderConfig> providerConfigs,
        int analysisCommitThreshold,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            var repoConfig = providerConfigs.FirstOrDefault(c => c.Id == repoProviderId.Value);
            if (repoConfig is null) return (false, null);

            await using var repoProvider = ProviderFactory.CreateRepositoryProvider(repoConfig);
            if (repoProvider is not Pipeline.Interfaces.IRepositoryAnalyticsProvider analyticsProvider)
                return (false, null);

            var latestAnalysisComment = issueContext.IssueComments
                .Where(c => c.Body.Contains(CommentMarkers.AnalysisHeader))
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefault();

            if (latestAnalysisComment is null) return (false, null);

            var commitCount = await analyticsProvider.GetCommitCountSinceAsync(latestAnalysisComment.CreatedAt, ct);
            if (commitCount >= analysisCommitThreshold)
                return (true, "commit_threshold");

            return (false, null);
        }
        catch (Exception ex)
        {
            logger.Warning(ex,
                "DispatchInfrastructure: commit-count staleness check failed; proceeding without refresh");
            return (false, null);
        }
    }
}
