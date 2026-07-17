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
    private readonly DispatchInfrastructure _infra;
    private readonly IDispatchRunCreator _orchestration;
    private readonly IOrchestratorRunService _runService;
    private readonly IWorkDistributor _workDistributor;
    private readonly IAgentProfileStore _agentProfileStore;
    private readonly IProviderConfigStore _providerConfigStore;
    private readonly IPipelineConfigStore _pipelineConfigStore;
    private readonly IProjectStore _projectStore;
    private readonly AnalysisStalenessDetector? _stalenessDetector;
    private readonly IssueContextBuilder _issueContextBuilder;
    private readonly ILogger _logger;

    public DispatchOrchestrationService(
        DispatchInfrastructure infra,
        IDispatchRunCreator orchestration,
        IOrchestratorRunService runService,
        IWorkDistributor workDistributor,
        IAgentProfileStore agentProfileStore,
        IProviderConfigStore providerConfigStore,
        IPipelineConfigStore pipelineConfigStore,
        IProjectStore projectStore,
        ILogger logger,
        IWorkItemQueryService? workItemQuery = null)
    {
        ArgumentNullException.ThrowIfNull(infra);
        ArgumentNullException.ThrowIfNull(orchestration);
        ArgumentNullException.ThrowIfNull(runService);
        ArgumentNullException.ThrowIfNull(workDistributor);
        ArgumentNullException.ThrowIfNull(agentProfileStore);
        ArgumentNullException.ThrowIfNull(providerConfigStore);
        ArgumentNullException.ThrowIfNull(pipelineConfigStore);
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(logger);

        _infra = infra;
        _orchestration = orchestration;
        _runService = runService;
        _workDistributor = workDistributor;
        _agentProfileStore = agentProfileStore;
        _providerConfigStore = providerConfigStore;
        _pipelineConfigStore = pipelineConfigStore;
        _projectStore = projectStore;
        _logger = logger;
        _stalenessDetector = workItemQuery is not null
            ? new AnalysisStalenessDetector(workItemQuery, logger)
            : null;
        _issueContextBuilder = new IssueContextBuilder(infra.ProviderFactory, providerConfigStore);
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
        CancellationToken ct,
        PipelineRunType runType = PipelineRunType.Implementation)
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
                requiredLabels, project, ct, runType);
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
        CancellationToken ct,
        PipelineRunType runType)
    {
        // Resolve profile using required labels (no agent entry — DB mode has no connected agents)
        var profile = await ResolveProfileByLabelsAsync(requiredLabels, ct);
        if (profile is null)
            return null;

        var agentProviderId = profile.AgentProviderConfigId;

        // Resolve quality gate configurations
        var resolvedQgcs = await _infra.Resolution.ResolveQualityGatesAsync(requiredLabels, ct);

        // Resolve reviewer configurations
        var resolvedReviewerConfigs = await _infra.Resolution.ResolveReviewersAsync(requiredLabels, ct);

        // Create the dispatched run via PipelineOrchestrationService
        var run = await _orchestration.CreateDispatchedRunAsync(
            issueProviderId, repoProviderId, issueIdentifier,
            agentProviderId, null, ct,
            brainProviderId, pipelineProviderId, initiatedBy, runType);

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
        var issueContext = await _issueContextBuilder.BuildAsync(
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

        // --- Staleness detection (after config is resolved for threshold) ---
        var stalenessSignal = issueContext.StalenessSignal;
        var refreshCount = issueContext.RefreshCount;
        var forceRefresh = issueContext.ForceRefreshAnalysis;

        if (!forceRefresh && issueContext.ExistingAnalysis is not null && _stalenessDetector is not null)
        {
            var analysisComment = issueContext.IssueComments
                .Where(c => c.Body.Contains(CommentMarkers.AnalysisHeader))
                .OrderByDescending(c => c.CreatedAt)
                .FirstOrDefault();

            if (analysisComment is not null)
            {
                // For signal 3: create a short-lived repo provider for commit counting
                Func<DateTimeOffset, CancellationToken, Task<int>>? getCommitCount = null;
                var repoConfig = await _providerConfigStore
                    .GetProviderConfigByIdAsync(repoProviderId, ProviderKind.Repository, ct);
                if (repoConfig is not null)
                {
                    getCommitCount = async (since, token) =>
                    {
                        await using var repoProvider = _infra.ProviderFactory.CreateRepositoryProvider(repoConfig);
                        return await repoProvider.GetCommitCountSinceAsync(since, token);
                    };
                }

                var result = await _stalenessDetector.EvaluateAsync(
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
            ForceRefreshAnalysis = forceRefresh,
            StalenessSignal = stalenessSignal,
            AnalysisRefreshCount = refreshCount,
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
        var profiles = await _agentProfileStore.LoadAgentProfilesAsync(ct);

        var resolver = new ProfileResolver();
        var profile = resolver.ResolveByRequiredLabels(profiles, requiredLabels);

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
        return await _infra.TokenVending.PrepareAgentConfigsAsync(
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

        var repoConfigs = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
        var repoConfig = await ProviderConfigResolver.ResolveAsync(
            _providerConfigStore, repoProviderId, ProviderKind.Repository, repoConfigs, required: true, _logger, ct);
        configs.Add(repoConfig!);

        var agentConfigs = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Agent, ct);
        var agentConfig = await ProviderConfigResolver.ResolveAsync(
            _providerConfigStore, agentProviderId, ProviderKind.Agent, agentConfigs, required: true, _logger, ct);
        configs.Add(agentConfig!);

        if (!string.IsNullOrEmpty(brainProviderId))
        {
            var brainConfig = await ProviderConfigResolver.ResolveAsync(
                _providerConfigStore, brainProviderId, ProviderKind.Repository, repoConfigs, required: false, _logger, ct);
            if (brainConfig is not null)
                configs.Add(brainConfig);
        }

        if (!string.IsNullOrEmpty(pipelineProviderId))
        {
            var pipelineConfigs = await _providerConfigStore.LoadProviderConfigsAsync(ProviderKind.Pipeline, ct);
            var pipelineConfig = await ProviderConfigResolver.ResolveAsync(
                _providerConfigStore, pipelineProviderId, ProviderKind.Pipeline, pipelineConfigs, required: false, _logger, ct);
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
        return await PipelineConfiguration.ResolveAsync(
            _pipelineConfigStore.LoadPipelineConfigAsync,
            _projectStore.LoadAllTemplatesAsync,
            project, repoProviderId, brainProviderId, providerConfigs, ct);
    }

    // ── IDispatchOrchestrationService implementation ─────────────────────

    /// <inheritdoc />
    public async Task<JobDistributionRequest?> PrepareDistributionRequestAsync(
        string issueIdentifier,
        ProviderConfigId issueProviderId,
        ProviderConfigId repoProviderId,
        ProviderConfigId? brainProviderId,
        ProviderConfigId? pipelineProviderId,
        string initiatedBy,
        PipelineProject project,
        WorkItemTaskType taskType = WorkItemTaskType.Implementation,
        PipelineRunType runType = PipelineRunType.Implementation,
        CancellationToken ct = default)
    {
        var requiredLabels = await ResolveRequiredLabelsInternalAsync(repoProviderId, ct);

        var result = await PrepareAsync(
            issueIdentifier, issueProviderId.Value, repoProviderId.Value,
            brainProviderId?.Value, pipelineProviderId?.Value, initiatedBy,
            requiredLabels, project, ct, runType);

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
            reviewRequest.IssueProviderId.Value,
            reviewRequest.RepoProviderId.Value,
            reviewRequest.BrainProviderId?.Value,
            null, // pipelineProviderId
            reviewRequest.InitiatedBy,
            requiredLabels, project, ct,
            PipelineRunType.Review);

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
        ProviderConfigId issueProviderId,
        ProviderConfigId repoProviderId,
        ProviderConfigId? brainProviderId,
        string initiatedBy,
        PipelineProject project,
        string? decompositionSource = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        var requiredLabels = await ResolveRequiredLabelsInternalAsync(repoProviderId, ct);

        var result = await PrepareAsync(
            epicIdentifier, issueProviderId.Value, repoProviderId.Value,
            brainProviderId?.Value, null, initiatedBy,
            requiredLabels, project, ct,
            phaseType);

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
        ProviderConfigId repoProviderId, CancellationToken ct)
    {
        var repoConfig = await _providerConfigStore
            .GetProviderConfigByIdAsync(repoProviderId.Value, ProviderKind.Repository, ct);
        var pipelineConfig = await _pipelineConfigStore.LoadPipelineConfigAsync(ct);
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
            BrainProviderConfigId = result.CreatedRun.BrainProviderConfigId is { } brain ? new ProviderConfigId(brain) : (ProviderConfigId?)null,
            PipelineProviderConfigId = result.CreatedRun.PipelineProviderConfigId is { } pipeline ? new ProviderConfigId(pipeline) : (ProviderConfigId?)null,
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
            StalenessSignal = result.StalenessSignal,
            AnalysisRefreshCount = result.AnalysisRefreshCount,
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
    public async Task<DispatchOutcome> DistributeAndFinalizeAsync(JobDistributionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await _workDistributor.DistributeAsync(request, ct);
        if (!result.Success)
        {
            await RevertFailedDistributionAsync(request, ct);
            return new DispatchOutcome(false, false, result.ErrorMessage);
        }

        if (!result.Queued)
            await ConfirmDistributionLabelAsync(request, ct);

        return new DispatchOutcome(true, result.Queued, null);
    }

    /// <inheritdoc />
    public async Task ConfirmDistributionLabelAsync(JobDistributionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Best-effort: the agent already has the job at this point. If the label swap fails
        // (GitHub API error), we log a warning but do NOT propagate the exception — otherwise
        // PipelineLoopService treats it as a failed dispatch (FailedCount++) even though the
        // agent is actively working. Note: IRunLifecycleManager.AgentAcceptedRunAsync also
        // performs this swap (best-effort) in the SignalR direct-dispatch path, so this call
        // is a safety net / idempotent confirmation.
        try
        {
            _logger.Information(
                "Orchestration: confirming distribution — swapping label to agent:in-progress for issue {IssueIdentifier}",
                request.IssueIdentifier);
            await _infra.LabelSwapper.SwapLabelAsync(
                request.IssueProviderConfigId, request.IssueIdentifier, AgentLabels.InProgress, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex,
                "Orchestration: failed to swap label to agent:in-progress for issue {IssueIdentifier} (non-fatal — agent already has the job)",
                request.IssueIdentifier);
        }
    }

    public async Task RevertFailedDistributionAsync(JobDistributionRequest request, CancellationToken ct)
    {
        try
        {
            // Revert label from agent:in-progress back to agent:next
            _logger.Warning("Reverting failed distribution for issue {IssueIdentifier}: swapping label back to agent:next",
                request.IssueIdentifier);
            await _infra.LabelSwapper.SwapLabelAsync(
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
