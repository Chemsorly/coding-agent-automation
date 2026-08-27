using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

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
public sealed class DispatchInfrastructure
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
            project, repoProviderId, brainProviderId, providerConfigs, ct);

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
    /// The superset signature supports optional <paramref name="additionalRepoProviderIds"/> for
    /// cross-repo decomposition. Callers that don't
    /// need cross-repo support simply omit the parameter.
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
            repoProviderId, agentProviderId, brainProviderId, pipelineProviderId, logger, ct, additionalRepoProviderIds);
        return await TokenVending.PrepareAgentConfigsAsync(rawConfigs, repoProviderId.Value, ct);
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
        CancellationToken ct,
        IEnumerable<string>? additionalRepoProviderIds = null)
    {
        var configs = new List<ProviderConfig>();

        var repoConfigs = await Resolution.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
        var repoConfig = await ProviderConfigResolver.ResolveAsync(
            Resolution.ConfigStore, repoProviderId.Value, ProviderKind.Repository, repoConfigs, required: true, logger, ct);
        configs.Add(repoConfig!);

        // Include additional repo provider configs for cross-repo decomposition.
        // These are needed so the agent can clone secondary repos for code exploration.
        if (additionalRepoProviderIds is not null)
        {
            var additionalConfigs = await ResolveAdditionalRepoConfigsAsync(
                repoProviderId.Value, additionalRepoProviderIds, repoConfigs, logger, ct);
            configs.AddRange(additionalConfigs);
        }

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

    private async Task<IReadOnlyList<ProviderConfig>> ResolveAdditionalRepoConfigsAsync(
        string primaryId,
        IEnumerable<string> additionalRepoProviderIds,
        IReadOnlyList<ProviderConfig> repoConfigs,
        ILogger logger,
        CancellationToken ct)
    {
        var configs = new List<ProviderConfig>();
        var addedIds = new HashSet<string> { primaryId }; // primary already added

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

    // ── Issue Context Building (inlined from IssueContextBuilder) ─────────────────

    /// <summary>
    /// Pre-fetches issue details, comments, and detects existing analysis with basic staleness signals
    /// (gate_rejection, gate_wont_do). Returns <c>null</c> if the issue provider config is not found.
    /// </summary>
    internal async Task<IssueContextResult?> BuildIssueContextAsync(
        IssueIdentifier issueIdentifier,
        ProviderConfigId issueProviderId,
        CancellationToken ct)
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
            // Cap at 50 comments per REQ-4.4
            issueComments = allComments.Count > 50
                ? allComments.Take(50).ToList().AsReadOnly()
                : allComments;
        }

        // Extract images from body + comments (mirrors FetchIssueStep pattern)
        var imageExtractor = new IssueImageExtractor();
        var images = imageExtractor.Extract(issueDetail.Description, issueComments, issueIdentifier, ImageSourceKind.Issue);
        issueDetail = new IssueDetail
        {
            Description = issueDetail.Description,
            Identifier = issueDetail.Identifier,
            Labels = issueDetail.Labels,
            Title = issueDetail.Title,
            Images = images
        };

        // Detect existing analysis and rework state from comments.
        // Detects gate_rejection and gate_wont_do signals.
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

        return new IssueContextResult(
            issueDetail, parsedIssue, issueComments,
            existingAnalysis, forceRefreshAnalysis, stalenessSignal, 0);
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
    internal async Task<(
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
        var issueContext = await BuildIssueContextAsync(issueIdentifier, issueProviderId, ct);
        if (issueContext is null)
        {
            logger.Error("Issue provider config '{ConfigId}' not found", issueProviderId);
            return null;
        }

        // ── Step 3: Prepare provider configs and resolve pipeline configuration ──
        var providerConfigs = await PrepareProviderConfigsAsync(
            repoProviderId, agentProviderId, brainProviderId, pipelineProviderId, logger, ct);

        var config = await PipelineConfigurationResolver.ResolveAsync(
            Resolution.ConfigStore.LoadPipelineConfigAsync,
            Resolution.ConfigStore.LoadAllTemplatesAsync,
            project, repoProviderId, brainProviderId, providerConfigs, ct);

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
