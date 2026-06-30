using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Extracts shared orchestration logic from <see cref="AgentJobDispatcher"/>
/// for consumption by DB-backed <see cref="IWorkDistributor"/> implementations.
/// Performs: issue fetching, label swapping, profile/QG resolution,
/// PipelineRun creation, and provider config preparation.
/// </summary>
/// <remarks>
/// NOT registered in Legacy mode (no-DB). <c>PipelineLoopService</c> checks
/// for null before calling <see cref="PrepareAsync"/>.
/// </remarks>
public sealed class DispatchOrchestrationService : IDispatchOrchestrationService
{
    private readonly DispatchResolutionService _resolution;
    private readonly PipelineOrchestrationService _orchestration;
    private readonly ITokenVendingService _tokenVending;
    private readonly IProviderFactory _providerFactory;
    private readonly ILabelSwapper _labelSwapper;
    private readonly OrchestratorRunService _runService;
    private readonly ILogger _logger;

    public DispatchOrchestrationService(
        DispatchResolutionService resolution,
        PipelineOrchestrationService orchestration,
        ITokenVendingService tokenVending,
        IProviderFactory providerFactory,
        ILabelSwapper labelSwapper,
        OrchestratorRunService runService,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(orchestration);
        ArgumentNullException.ThrowIfNull(tokenVending);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(labelSwapper);
        ArgumentNullException.ThrowIfNull(runService);
        ArgumentNullException.ThrowIfNull(logger);

        _resolution = resolution;
        _orchestration = orchestration;
        _tokenVending = tokenVending;
        _providerFactory = providerFactory;
        _labelSwapper = labelSwapper;
        _runService = runService;
        _logger = logger;
    }

    /// <summary>
    /// Performs full orchestration for an implementation issue dispatch:
    /// fetches issue, swaps labels, resolves profile/QGs, creates run, prepares provider configs.
    /// </summary>
    /// <param name="issueIdentifier">The issue to dispatch.</param>
    /// <param name="issueProviderId">Issue provider config ID.</param>
    /// <param name="repoProviderId">Repository provider config ID.</param>
    /// <param name="brainProviderId">Optional brain provider config ID.</param>
    /// <param name="pipelineProviderId">Optional pipeline provider config ID.</param>
    /// <param name="initiatedBy">Who initiated the dispatch.</param>
    /// <param name="requiredLabels">Resolved required labels for agent matching.</param>
    /// <param name="project">The project context for this dispatch.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Preparation result or null if orchestration failed.</returns>
    public async Task<DispatchPreparationResult?> PrepareAsync(
        string issueIdentifier,
        string issueProviderId,
        string repoProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        string initiatedBy,
        IReadOnlyList<string> requiredLabels,
        PipelineProject project,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        ArgumentNullException.ThrowIfNull(issueProviderId);
        ArgumentNullException.ThrowIfNull(repoProviderId);
        ArgumentNullException.ThrowIfNull(initiatedBy);
        ArgumentNullException.ThrowIfNull(requiredLabels);
        ArgumentNullException.ThrowIfNull(project);

        try
        {
            return await PrepareCoreAsync(
                issueIdentifier, issueProviderId, repoProviderId,
                brainProviderId, pipelineProviderId, initiatedBy,
                requiredLabels, project, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error(ex,
                "Orchestration failed for issue {IssueIdentifier}", issueIdentifier);
            return null;
        }
    }

    private async Task<DispatchPreparationResult?> PrepareCoreAsync(
        string issueIdentifier,
        string issueProviderId,
        string repoProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        string initiatedBy,
        IReadOnlyList<string> requiredLabels,
        PipelineProject project,
        CancellationToken ct)
    {
        // Resolve profile using required labels (no agent entry — DB mode has no connected agents)
        var profile = await ResolveProfileByLabelsAsync(requiredLabels, ct);
        if (profile is null)
            return null;

        var agentProviderId = profile.AgentProviderConfigId;

        // Resolve quality gate configurations
        var resolvedQgcs = await _resolution.ResolveQualityGatesAsync(requiredLabels, ct);

        // Resolve reviewer configurations
        var resolvedReviewerConfigs = await _resolution.ResolveReviewersAsync(requiredLabels, ct);

        // Create the dispatched run via PipelineOrchestrationService
        var run = await _orchestration.CreateDispatchedRunAsync(
            issueProviderId, repoProviderId, issueIdentifier,
            agentProviderId, "pending", ct,
            brainProviderId, pipelineProviderId, initiatedBy);

        if (run is null)
        {
            _logger.Warning(
                "Failed to create dispatched run for issue {IssueIdentifier}",
                issueIdentifier);
            return null;
        }

        // Set project context on the run
        run.ProjectId = project.Id;
        run.ProjectName = project.Name;
        run.ResolvedProfileId = profile.Id;
        run.ResolvedQualityGateConfigIds = resolvedQgcs
            .Select(q => q.Id).ToList().AsReadOnly();
        run.ResolvedReviewerConfigIds = resolvedReviewerConfigs
            .Select(r => r.Id).ToList().AsReadOnly();

        // Pre-fetch issue details, comments, and swap labels
        var issueContext = await PrepareIssueContextAsync(
            issueIdentifier, issueProviderId, ct);
        if (issueContext is null)
        {
            _logger.Error("Issue provider config '{ConfigId}' not found", issueProviderId);
            _runService.RemoveRun(run.RunId);
            return null;
        }

        // Update run with fetched issue title
        run.IssueTitle = issueContext.IssueDetail.Title;

        // Build and prepare provider configs for the agent
        var providerConfigs = await PrepareProviderConfigsAsync(
            repoProviderId, agentProviderId, brainProviderId,
            pipelineProviderId, ct);

        // Settings resolution: Global → Project → Template overrides
        var config = await LoadAndApplySettingsAsync(
            project, repoProviderId, brainProviderId, providerConfigs, ct);

        return new DispatchPreparationResult
        {
            ResolvedProfile = profile,
            QualityGateConfigs = resolvedQgcs,
            ReviewerConfigs = resolvedReviewerConfigs,
            ProviderConfigs = providerConfigs,
            PipelineConfiguration = config,
            IssueDetail = issueContext.IssueDetail,
            ParsedIssue = issueContext.ParsedIssue,
            IssueComments = issueContext.IssueComments,
            ExistingAnalysis = issueContext.ExistingAnalysis,
            ForceRefreshAnalysis = issueContext.ForceRefreshAnalysis,
            CreatedRun = run,
            Project = project,
            McpServers = profile.McpServers,
            TraceContext = PipelineTelemetry.CaptureTraceContext("DispatchOrchestration")
        };
    }

    /// <summary>
    /// Resolves agent profile by matching required labels against all profiles.
    /// Used in DB mode where there is no specific connected agent at orchestration time.
    /// </summary>
    private async Task<AgentProfile?> ResolveProfileByLabelsAsync(
        IReadOnlyList<string> requiredLabels, CancellationToken ct)
    {
        var profiles = await _resolution.ConfigStore.LoadAgentProfilesAsync(ct);

        // Find the best profile whose match labels COVER all required labels.
        // Profile.MatchLabels = "agent must have these labels for this profile to apply."
        // Any agent matched by such a profile has at least those labels,
        // so if requiredLabels ⊆ profile.MatchLabels, the agent can satisfy the job.
        var profile = profiles
            .Where(p => p.Enabled)
            .Where(p => requiredLabels.All(rl =>
                p.MatchLabels.Contains(rl, StringComparer.OrdinalIgnoreCase)))
            .OrderByDescending(p => p.MatchLabels.Count)
            .ThenByDescending(p => p.Priority)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        if (profile is null)
        {
            var labelsStr = string.Join(", ", requiredLabels);
            _logger.Warning(
                "No profile matches required labels [{Labels}] for DB-mode dispatch",
                labelsStr);
        }

        return profile;
    }

    /// <summary>
    /// Pre-fetches issue details, comments, swaps labels to in-progress,
    /// and detects existing analysis. Returns null if issue provider config not found.
    /// </summary>
    private async Task<IssueContext?> PrepareIssueContextAsync(
        string issueIdentifier, string issueProviderId, CancellationToken ct)
    {
        var issueConfig = await _resolution.ConfigStore
            .GetProviderConfigByIdAsync(issueProviderId, ProviderKind.Issue, ct);
        if (issueConfig is null)
            return null;

        IssueDetail issueDetail;
        ParsedIssue parsedIssue;
        IReadOnlyList<IssueComment> issueComments;
        await using (var issueProvider = _providerFactory.CreateIssueProvider(issueConfig))
        {
            issueDetail = await issueProvider.GetIssueAsync(issueIdentifier, ct);
            parsedIssue = new IssueDescriptionParser().Parse(issueDetail.Description);
            var allComments = await issueProvider.ListCommentsAsync(issueIdentifier, ct);
            issueComments = allComments.Count > 50
                ? allComments.Take(50).ToList().AsReadOnly()
                : allComments;
        }

        // Swap label to agent:in-progress
        _logger.Information(
            "Orchestration: swapping label to agent:in-progress for issue {IssueIdentifier} (provider={ProviderId})",
            issueIdentifier, issueProviderId);
        await _labelSwapper.SwapLabelAsync(
            issueProviderId, issueIdentifier, AgentLabels.InProgress, ct);

        // Detect existing analysis and rework state from comments
        string? existingAnalysis = null;
        bool forceRefreshAnalysis = false;
        var analysisComment = issueComments
            .FirstOrDefault(c => c.Body.Contains(CommentMarkers.AnalysisHeader));
        if (analysisComment is not null)
        {
            existingAnalysis = analysisComment.Body;
            var gateRejection = issueComments
                .FirstOrDefault(c => c.Body.Contains(CommentMarkers.GateRejection));
            var gateWontDo = issueComments
                .FirstOrDefault(c => c.Body.Contains(CommentMarkers.GateWontDo));
            if ((gateRejection?.CreatedAt > analysisComment.CreatedAt) ||
                (gateWontDo?.CreatedAt > analysisComment.CreatedAt))
                forceRefreshAnalysis = true;
        }

        return new IssueContext(
            issueDetail, parsedIssue, issueComments,
            existingAnalysis, forceRefreshAnalysis);
    }

    /// <summary>
    /// Builds provider configs list and vends tokens via the token vending service.
    /// </summary>
    private async Task<IReadOnlyList<ProviderConfig>> PrepareProviderConfigsAsync(
        string repoProviderId,
        string agentProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        CancellationToken ct)
    {
        var rawConfigs = await BuildAgentProviderConfigsAsync(
            repoProviderId, agentProviderId, brainProviderId,
            pipelineProviderId, ct);
        return await _tokenVending.PrepareAgentConfigsAsync(
            rawConfigs, repoProviderId, ct);
    }

    /// <summary>
    /// Builds the list of provider configs to send to the agent.
    /// Excludes issue provider configs (agents don't get issue access).
    /// </summary>
    private async Task<IReadOnlyList<ProviderConfig>> BuildAgentProviderConfigsAsync(
        string repoProviderId,
        string agentProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        CancellationToken ct)
    {
        var configs = new List<ProviderConfig>();
        var store = _resolution.ConfigStore;

        var repoConfigs = await store.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
        var repoConfig = await ProviderConfigResolver.ResolveAsync(
            store, repoProviderId, ProviderKind.Repository, repoConfigs, required: true, _logger, ct);
        configs.Add(repoConfig!);

        var agentConfigs = await store.LoadProviderConfigsAsync(ProviderKind.Agent, ct);
        var agentConfig = await ProviderConfigResolver.ResolveAsync(
            store, agentProviderId, ProviderKind.Agent, agentConfigs, required: true, _logger, ct);
        configs.Add(agentConfig!);

        if (!string.IsNullOrEmpty(brainProviderId))
        {
            var brainConfig = await ProviderConfigResolver.ResolveAsync(
                store, brainProviderId, ProviderKind.Repository, repoConfigs, required: false, _logger, ct);
            if (brainConfig is not null)
                configs.Add(brainConfig);
        }

        if (!string.IsNullOrEmpty(pipelineProviderId))
        {
            var pipelineConfigs = await store.LoadProviderConfigsAsync(ProviderKind.Pipeline, ct);
            var pipelineConfig = await ProviderConfigResolver.ResolveAsync(
                store, pipelineProviderId, ProviderKind.Pipeline, pipelineConfigs, required: false, _logger, ct);
            if (pipelineConfig is not null)
                configs.Add(pipelineConfig);
        }

        return configs.AsReadOnly();
    }

    /// <summary>
    /// Loads the pipeline configuration and applies project/template overrides.
    /// </summary>
    private async Task<PipelineConfiguration> LoadAndApplySettingsAsync(
        PipelineProject project,
        string repoProviderId,
        string? brainProviderId,
        IReadOnlyList<ProviderConfig> providerConfigs,
        CancellationToken ct)
    {
        var config = await _resolution.ConfigStore.LoadPipelineConfigAsync(ct);
        config = PipelineConfiguration.ApplyProjectOverrides(config, project);
        var templates = await _resolution.ConfigStore.LoadAllTemplatesAsync(ct);
        return ApplyTemplateOverrides(
            config, repoProviderId, brainProviderId, providerConfigs, templates);
    }

    private static PipelineConfiguration ApplyTemplateOverrides(
        PipelineConfiguration config,
        string repoProviderId,
        string? brainProviderId,
        IReadOnlyList<ProviderConfig> providerConfigs,
        IReadOnlyList<PipelineJobTemplate> templates)
    {
        var matchingTemplate = templates.FirstOrDefault(t =>
            t.RepoProviderId == repoProviderId
            && t.BrainProviderId == brainProviderId);
        if (matchingTemplate is { BrainReadOnly: true })
            config = config with { BrainReadOnly = true };

        return PipelineConfiguration.ApplyBlacklistOverride(
            config,
            providerConfigs.FirstOrDefault(c => c.Id == repoProviderId));
    }

    /// <summary>
    /// Internal DTO for pre-fetched issue context.
    /// </summary>
    private sealed record IssueContext(
        IssueDetail IssueDetail,
        ParsedIssue ParsedIssue,
        IReadOnlyList<IssueComment> IssueComments,
        string? ExistingAnalysis,
        bool ForceRefreshAnalysis);

    // ── IDispatchOrchestrationService implementation ─────────────────────

    /// <inheritdoc />
    public async Task<JobDistributionRequest?> PrepareDistributionRequestAsync(
        string issueIdentifier,
        string issueProviderId,
        string repoProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        string initiatedBy,
        PipelineProject project,
        WorkItemTaskType taskType = WorkItemTaskType.Implementation,
        PipelineRunType runType = PipelineRunType.Implementation,
        CancellationToken ct = default)
    {
        var requiredLabels = await ResolveRequiredLabelsInternalAsync(repoProviderId, ct);

        var result = await PrepareAsync(
            issueIdentifier, issueProviderId, repoProviderId,
            brainProviderId, pipelineProviderId, initiatedBy,
            requiredLabels, project, ct);

        return result is null ? null : MapToRequest(result, taskType, runType);
    }

    /// <inheritdoc />
    public async Task<JobDistributionRequest?> PrepareReviewDistributionRequestAsync(
        ReviewDispatchRequest reviewRequest,
        PipelineProject project,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reviewRequest);
        ArgumentNullException.ThrowIfNull(project);

        var requiredLabels = await ResolveRequiredLabelsInternalAsync(reviewRequest.RepoProviderId, ct);

        var result = await PrepareAsync(
            reviewRequest.PrIdentifier,
            reviewRequest.IssueProviderId,
            reviewRequest.RepoProviderId,
            reviewRequest.BrainProviderId,
            null, // pipelineProviderId
            reviewRequest.InitiatedBy,
            requiredLabels, project, ct);

        if (result is null) return null;

        var request = MapToRequest(result, WorkItemTaskType.Review, PipelineRunType.Review);
        return request with
        {
            LinkedPullRequest = new LinkedPullRequest
            {
                Url = reviewRequest.PrUrl,
                BranchName = reviewRequest.PrBranchName,
                IsDraft = false,
                Number = 0
            },
            ReviewPrTargetBranch = reviewRequest.PrTargetBranch,
            ReviewPrDescription = reviewRequest.PrDescription,
            ReviewPrAuthor = reviewRequest.PrAuthor
        };
    }

    /// <inheritdoc />
    public async Task<JobDistributionRequest?> PrepareDecompositionDistributionRequestAsync(
        string epicIdentifier,
        string epicTitle,
        PipelineRunType phaseType,
        string issueProviderId,
        string repoProviderId,
        string? brainProviderId,
        string initiatedBy,
        PipelineProject project,
        string? decompositionSource = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        var requiredLabels = await ResolveRequiredLabelsInternalAsync(repoProviderId, ct);

        var result = await PrepareAsync(
            epicIdentifier, issueProviderId, repoProviderId,
            brainProviderId, null, initiatedBy,
            requiredLabels, project, ct);

        if (result is null) return null;

        var request = MapToRequest(result, WorkItemTaskType.Decomposition, phaseType);
        return request with
        {
            DecompositionSource = decompositionSource
        };
    }

    /// <summary>
    /// Resolves required labels from the repo provider config, falling back to global config defaults.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveRequiredLabelsInternalAsync(
        string repoProviderId, CancellationToken ct)
    {
        var repoConfig = await _resolution.ConfigStore
            .GetProviderConfigByIdAsync(repoProviderId, ProviderKind.Repository, ct);
        var pipelineConfig = await _resolution.ConfigStore.LoadPipelineConfigAsync(ct);
        return LabelResolver.ResolveRequiredLabels(repoConfig, pipelineConfig);
    }

    /// <summary>
    /// Maps a <see cref="DispatchPreparationResult"/> to a <see cref="JobDistributionRequest"/>.
    /// </summary>
    private static JobDistributionRequest MapToRequest(
        DispatchPreparationResult result,
        WorkItemTaskType taskType,
        PipelineRunType runType)
    {
        var agentSelector = string.Join(",",
            (result.ResolvedProfile.MatchLabels ?? []).OrderBy(l => l, StringComparer.Ordinal));

        return new JobDistributionRequest
        {
            IssueIdentifier = result.IssueDetail.Identifier,
            IssueProviderConfigId = result.CreatedRun.IssueProviderConfigId,
            RepoProviderConfigId = result.CreatedRun.RepoProviderConfigId,
            BrainProviderConfigId = result.CreatedRun.BrainProviderConfigId,
            PipelineProviderConfigId = result.CreatedRun.PipelineProviderConfigId,
            InitiatedBy = result.CreatedRun.InitiatedBy ?? "loop",
            TaskType = taskType,
            AgentSelector = agentSelector,
            TimeoutSeconds = (int)result.PipelineConfiguration.AgentTimeout.TotalSeconds,
            ProjectId = result.Project.Id,
            ProjectName = result.Project.Name,
            RunType = runType,
            IssueDetail = result.IssueDetail,
            ParsedIssue = result.ParsedIssue,
            IssueComments = result.IssueComments,
            ExistingAnalysis = result.ExistingAnalysis,
            ForceRefreshAnalysis = result.ForceRefreshAnalysis,
            ProviderConfigs = result.ProviderConfigs,
            PipelineConfiguration = result.PipelineConfiguration,
            ResolvedProfileId = result.ResolvedProfile.Id,
            AgentProviderConfigId = result.ResolvedProfile.AgentProviderConfigId,
            QualityGateConfigs = result.QualityGateConfigs,
            ReviewerConfigs = result.ReviewerConfigs,
            McpServers = result.McpServers,
            TraceContext = result.TraceContext,
            RunId = result.CreatedRun.RunId
        };
    }

    /// <inheritdoc />
    public async Task RevertFailedDistributionAsync(JobDistributionRequest request, CancellationToken ct)
    {
        try
        {
            // Revert label from agent:in-progress back to agent:next
            _logger.Warning("Reverting failed distribution for issue {IssueIdentifier}: swapping label back to agent:next",
                request.IssueIdentifier);
            await _labelSwapper.SwapLabelAsync(
                request.IssueProviderConfigId, request.IssueIdentifier, AgentLabels.Next, ct);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to revert label for issue {IssueIdentifier} after distribution failure",
                request.IssueIdentifier);
        }

        try
        {
            // Remove the dangling run that was created during PrepareAsync
            var activeRuns = _runService.GetActiveRuns();
            var danglingRun = activeRuns.FirstOrDefault(r =>
                r.IssueIdentifier == request.IssueIdentifier &&
                r.IssueProviderConfigId == request.IssueProviderConfigId);
            if (danglingRun is not null)
            {
                _runService.RemoveRun(danglingRun.RunId);
                _logger.Information("Removed dangling run {RunId} for issue {IssueIdentifier} after distribution failure",
                    danglingRun.RunId, request.IssueIdentifier);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to remove dangling run for issue {IssueIdentifier} after distribution failure",
                request.IssueIdentifier);
        }
    }
}
