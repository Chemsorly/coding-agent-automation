using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Aggregate that bundles shared dispatch-path dependencies used by both
/// <see cref="AgentJobDispatcher"/> and <see cref="DispatchOrchestrationService"/>.
/// Reduces constructor parameter count by grouping services that always travel together:
/// provider config building, profile resolution, token vending, and label operations.
/// <para>
/// Also hosts <see cref="PrepareDispatchCoreAsync"/> — the single consolidated method
/// for the shared dispatch preparation sequence (QG/reviewer resolution, issue context,
/// provider config preparation, pipeline config resolution, and staleness detection).
/// Both <see cref="AgentJobDispatcher"/> and <see cref="DispatchOrchestrationService"/>
/// delegate to this method, eliminating drift between the Legacy and DB paths.
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

    // TODO: Consider making StalenessDetector an init-only or constructor parameter to eliminate
    // temporal coupling — currently set as a side-effect inside the IDispatchOrchestrationService
    // singleton factory, creating an implicit DI resolution ordering dependency.
    // TODO: Restrict setter to internal to prevent accidental mutation from outside the assembly/initialization path.
    /// <summary>
    /// Optional staleness detector for evaluating analysis freshness.
    /// Set by DI in DB mode; null in legacy (no-DB) mode where
    /// <see cref="Pipeline.Interfaces.IWorkItemQueryService"/> is unavailable.
    /// </summary>
    public AnalysisStalenessDetector? StalenessDetector { get; set; }

    public DispatchInfrastructure(
        ITokenVendingService tokenVending,
        IProviderFactory providerFactory,
        ILabelService labelService,
        DispatchResolutionService resolution)
    {
        ArgumentNullException.ThrowIfNull(tokenVending);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(labelService);
        ArgumentNullException.ThrowIfNull(resolution);

        TokenVending = tokenVending;
        ProviderFactory = providerFactory;
        LabelService = labelService;
        Resolution = resolution;
    }

    // ── Provider Config Building (inlined from ProviderConfigBuilder) ──────────────

    /// <summary>
    /// Builds the provider configs list and prepares tokens via the token vending service.
    /// </summary>
    /// <remarks>
    /// The superset signature supports optional <paramref name="additionalRepoProviderIds"/> for
    /// cross-repo decomposition (used by <see cref="AgentJobDispatcher"/>). Callers that don't
    /// need cross-repo support simply omit the parameter.
    /// </remarks>
    internal async Task<IReadOnlyList<ProviderConfig>> PrepareProviderConfigsAsync(
        string repoProviderId,
        string agentProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        ILogger logger,
        CancellationToken ct,
        IEnumerable<string>? additionalRepoProviderIds = null)
    {
        var rawConfigs = await BuildAgentProviderConfigsAsync(
            repoProviderId, agentProviderId, brainProviderId, pipelineProviderId, logger, ct, additionalRepoProviderIds);
        return await TokenVending.PrepareAgentConfigsAsync(rawConfigs, repoProviderId, ct);
    }

    /// <summary>
    /// Builds the list of provider configs to send to the agent.
    /// Excludes issue provider configs (agents don't get issue access).
    /// </summary>
    internal async Task<IReadOnlyList<ProviderConfig>> BuildAgentProviderConfigsAsync(
        string repoProviderId,
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
            Resolution.ConfigStore, repoProviderId, ProviderKind.Repository, repoConfigs, required: true, logger, ct);
        configs.Add(repoConfig!);

        // Include additional repo provider configs for cross-repo decomposition.
        // These are needed so the agent can clone secondary repos for code exploration.
        if (additionalRepoProviderIds is not null)
        {
            var addedIds = new HashSet<string> { repoProviderId }; // primary already added
            foreach (var additionalId in additionalRepoProviderIds)
            {
                if (string.IsNullOrEmpty(additionalId) || !addedIds.Add(additionalId))
                    continue; // skip null/empty or duplicates

                var additionalConfig = await ProviderConfigResolver.ResolveAsync(
                    Resolution.ConfigStore, additionalId, ProviderKind.Repository, repoConfigs, required: false, logger, ct);
                if (additionalConfig is not null)
                    configs.Add(additionalConfig);
            }
        }

        var agentConfigs = await Resolution.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Agent, ct);
        var agentConfig = await ProviderConfigResolver.ResolveAsync(
            Resolution.ConfigStore, agentProviderId, ProviderKind.Agent, agentConfigs, required: true, logger, ct);
        configs.Add(agentConfig!);

        if (!string.IsNullOrEmpty(brainProviderId))
        {
            var brainConfig = await ProviderConfigResolver.ResolveAsync(
                Resolution.ConfigStore, brainProviderId, ProviderKind.Repository, repoConfigs, required: false, logger, ct);
            if (brainConfig is not null)
                configs.Add(brainConfig);
        }

        if (!string.IsNullOrEmpty(pipelineProviderId))
        {
            var pipelineConfigs = await Resolution.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Pipeline, ct);
            var pipelineConfig = await ProviderConfigResolver.ResolveAsync(
                Resolution.ConfigStore, pipelineProviderId, ProviderKind.Pipeline, pipelineConfigs, required: false, logger, ct);
            if (pipelineConfig is not null)
                configs.Add(pipelineConfig);
        }

        return configs.AsReadOnly();
    }

    // ── Issue Context Building (inlined from IssueContextBuilder) ─────────────────

    /// <summary>
    /// Pre-fetches issue details, comments, and detects existing analysis with basic staleness signals.
    /// Returns <c>null</c> if the issue provider config is not found.
    /// </summary>
    /// <remarks>
    /// This method does NOT invoke <see cref="AnalysisStalenessDetector"/> — that remains
    /// in <see cref="PrepareDispatchCoreAsync"/> because it depends on the pipeline
    /// configuration threshold which is resolved after issue context is built.
    /// </remarks>
    internal async Task<IssueContextResult?> BuildIssueContextAsync(
        string issueIdentifier,
        string issueProviderId,
        CancellationToken ct)
    {
        var issueConfig = await Resolution.ConfigStore
            .GetProviderConfigByIdAsync(issueProviderId, ProviderKind.Issue, ct);
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
        // NOTE: Only gate_rejection and gate_wont_do are detected here.
        // The three AnalysisStalenessDetector signals (body_changed, agent_error,
        // commit_threshold) are evaluated separately in PrepareDispatchCoreAsync because
        // they depend on pipeline configuration resolved after this step.
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
    /// Consolidated dispatch preparation logic shared by both <see cref="AgentJobDispatcher"/>
    /// (Legacy/SignalR path) and <see cref="DispatchOrchestrationService"/> (DB path).
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
        IReadOnlyList<string> requiredLabels,
        string issueIdentifier,
        string issueProviderId,
        string repoProviderId,
        string agentProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        PipelineProject project,
        ILogger logger,
        CancellationToken ct)
    {
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

        // ── Step 4: Evaluate staleness signals (body_changed, agent_error, commit_threshold) ──
        var forceRefresh = issueContext.ForceRefreshAnalysis;
        var stalenessSignal = issueContext.StalenessSignal;
        var refreshCount = issueContext.RefreshCount;

        if (!forceRefresh && issueContext.ExistingAnalysis is not null && StalenessDetector is not null)
        {
            var analysisComment = issueContext.IssueComments
                .Where(c => c.Body.Contains(CommentMarkers.AnalysisHeader))
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefault();

            if (analysisComment is not null)
            {
                // For signal 3 (commit_threshold): create a short-lived repo provider for commit counting
                Func<DateTimeOffset, CancellationToken, Task<int>>? getCommitCount = null;
                var repoConfig = await Resolution.ConfigStore
                    .GetProviderConfigByIdAsync(repoProviderId, ProviderKind.Repository, ct);
                if (repoConfig is not null)
                {
                    getCommitCount = async (since, token) =>
                    {
                        await using var repoProvider = ProviderFactory.CreateRepositoryProvider(repoConfig);
                        return await repoProvider.GetCommitCountSinceAsync(since, token);
                    };
                }

                var result = await StalenessDetector.EvaluateAsync(
                    analysisComment, issueContext.IssueComments,
                    issueContext.IssueDetail.Description,
                    issueIdentifier, issueProviderId,
                    config.AnalysisCommitThreshold,
                    getCommitCount, ct);

                if (result.ForceRefresh)
                {
                    forceRefresh = true;
                    stalenessSignal = result.Signal;
                }
                refreshCount = result.RefreshCount;
            }
        }

        return (resolvedQgcs, resolvedReviewerConfigs, issueContext, providerConfigs, config,
            forceRefresh, stalenessSignal, refreshCount);
    }
}
