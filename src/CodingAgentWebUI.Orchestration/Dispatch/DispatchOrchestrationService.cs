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
/// Performs: issue fetching, label swapping, profile/QG resolution, and provider config preparation.
/// </summary>
/// <remarks>
/// Run materialisation (creating the in-memory <see cref="PipelineRun"/> in
/// <see cref="IOrchestratorRunService"/>) was moved to the Pipeline API's
/// <c>POST /api/work-items</c> handler (Req 1a.1 Option A). This service no longer
/// registers runs in a local run registry.
/// </remarks>
public sealed class DispatchOrchestrationService : IDispatchOrchestrationService
{
    private readonly DispatchInfrastructure _infra;
    private readonly IWorkDistributor _workDistributor;
    private readonly IAgentProfileStore _agentProfileStore;
    private readonly IConfigurationStore _providerConfigStore;
    private readonly IPipelineConfigStore _pipelineConfigStore;
    private readonly ILogger _logger;

    public DispatchOrchestrationService(
        DispatchOrchestrationServiceDependencies deps,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentNullException.ThrowIfNull(logger);

        _infra = deps.Infra;
        _workDistributor = deps.WorkDistributor;
        _agentProfileStore = deps.AgentProfileStore;
        _providerConfigStore = deps.ProviderConfigStore;
        _pipelineConfigStore = deps.PipelineConfigStore;
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
        OrchestratorPreparationRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(request.IssueIdentifier.Value);
        ArgumentNullException.ThrowIfNull(request.InitiatedBy);
        ArgumentNullException.ThrowIfNull(request.RequiredLabels);
        ArgumentNullException.ThrowIfNull(request.Project);

        try
        {
            return await PrepareCoreAsync(request, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error(ex,
                "Orchestration failed for issue {IssueIdentifier}", request.IssueIdentifier);
            return null;
        }
    }

    private async Task<DispatchPreparationResult?> PrepareCoreAsync(
        OrchestratorPreparationRequest request,
        CancellationToken ct)
    {
        var issueIdentifier = request.IssueIdentifier;
        var issueProviderId = request.IssueProviderId;
        var repoProviderId = request.RepoProviderId;
        var brainProviderId = request.BrainProviderId;
        var pipelineProviderId = request.PipelineProviderId;
        var initiatedBy = request.InitiatedBy;
        var requiredLabels = request.RequiredLabels;
        var project = request.Project;
        var runType = request.RunType;
        // Resolve profile using required labels (no agent entry — DB mode has no connected agents)
        var profile = await ResolveProfileByLabelsAsync(requiredLabels, ct);
        if (profile is null)
            return null;

        var agentProviderId = profile.AgentProviderConfigId;

        // Shared dispatch preparation: QG/reviewer resolution, issue context, config, staleness
        var preparation = await _infra.PrepareDispatchCoreAsync(
            new DispatchCoreRequest(
                requiredLabels, issueIdentifier, issueProviderId,
                repoProviderId, agentProviderId, brainProviderId, pipelineProviderId,
                project, _logger),
            ct);
        if (preparation is null)
            return null;

        var (resolvedQgcs, resolvedReviewerConfigs, issueContext, providerConfigs, config,
            forceRefresh, stalenessSignal, refreshCount) = preparation.Value;

        // Build the run locally for metadata propagation — NOT registered in any in-memory
        // registry. The API's POST /api/work-items handler calls PipelineRunFactory.CreateFromWorkItem
        // and registers it in the API's IOrchestratorRunService (Req 1a.1 Option A).
        var run = BuildLocalRun(request);

        if (run is null)
        {
            _logger.Warning(
                "Failed to create dispatched run for issue {IssueIdentifier}",
                issueIdentifier);
            return null;
        }

        // Set project context and resolved metadata on the run
        run.ProjectId = project.Id;
        run.ProjectName = project.Name;
        run.ResolvedProfileId = profile.Id;
        run.ResolvedQualityGateConfigIds = resolvedQgcs
            .Select(q => q.Id).ToList().AsReadOnly();
        run.ResolvedReviewerConfigIds = resolvedReviewerConfigs
            .Select(r => r.Id).ToList().AsReadOnly();
        run.IssueTitle = issueContext.IssueDetail.Title;

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
            McpServers = MergeMcpServers(profile.McpServers, project.McpServers),
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

        var profile = ProfileResolver.ResolveByRequiredLabels(profiles, requiredLabels);

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
    /// Constructs a <see cref="PipelineRun"/> for metadata propagation only — it is NOT registered
    /// in any in-memory run registry. The API registers the run when the WorkItem is persisted.
    /// </summary>
    private static PipelineRun? BuildLocalRun(OrchestratorPreparationRequest request)
    {
        var runId = Guid.NewGuid().ToString();
        try
        {
            return request.RunType switch
            {
                PipelineRunType.Review => PipelineRun.CreateReview(new PipelineRunCreationParams
                {
                    RunId = runId,
                    IssueIdentifier = request.IssueIdentifier,
                    IssueTitle = string.Empty,
                    IssueProviderConfigId = request.IssueProviderId.Value,
                    RepoProviderConfigId = request.RepoProviderId.Value,
                    RunType = PipelineRunType.Review,
                    InitiatedBy = request.InitiatedBy
                }),
                PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition =>
                    PipelineRun.CreateDecomposition(new PipelineRunCreationParams
                    {
                        RunId = runId,
                        IssueIdentifier = request.IssueIdentifier,
                        IssueTitle = string.Empty,
                        IssueProviderConfigId = request.IssueProviderId.Value,
                        RepoProviderConfigId = request.RepoProviderId.Value,
                        RunType = request.RunType,
                        InitiatedBy = request.InitiatedBy
                    }),
                _ => PipelineRun.CreateImplementation(new PipelineRunCreationParams
                {
                    RunId = runId,
                    IssueIdentifier = request.IssueIdentifier,
                    IssueTitle = string.Empty,
                    IssueProviderConfigId = request.IssueProviderId.Value,
                    RepoProviderConfigId = request.RepoProviderId.Value,
                    InitiatedBy = request.InitiatedBy
                })
            };
        }
        catch
        {
            return null;
        }
    }

    // ── IDispatchOrchestrationService implementation ─────────────────────

    /// <inheritdoc />
    public async Task<JobDistributionRequest?> PrepareDistributionRequestAsync(
        ImplementationDispatchOrchestrationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var requiredLabels = await ResolveRequiredLabelsInternalAsync(request.RepoProviderId, ct);

        var result = await PrepareAsync(
            new OrchestratorPreparationRequest(
                request.IssueIdentifier, request.IssueProviderId, request.RepoProviderId,
                request.BrainProviderId, request.PipelineProviderId, request.InitiatedBy,
                requiredLabels, request.Project, request.RunType),
            ct);

        return result is null ? null : MapToRequest(result, request.TaskType, request.RunType);
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
            new OrchestratorPreparationRequest(
                reviewRequest.PrIdentifier,
                reviewRequest.IssueProviderId,
                reviewRequest.RepoProviderId,
                reviewRequest.BrainProviderId,
                null, // pipelineProviderId
                reviewRequest.InitiatedBy,
                requiredLabels, project, PipelineRunType.Review),
            ct);

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
        DecompositionDispatchOrchestrationRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Project);

        var requiredLabels = await ResolveRequiredLabelsInternalAsync(request.RepoProviderId, ct);

        var result = await PrepareAsync(
            new OrchestratorPreparationRequest(
                request.EpicIdentifier, request.IssueProviderId, request.RepoProviderId,
                request.BrainProviderId, null, request.InitiatedBy,
                requiredLabels, request.Project, request.PhaseType),
            ct);

        if (result is null) return null;

        var jobRequest = MapToRequest(result, WorkItemTaskType.Decomposition, request.PhaseType);
        return jobRequest with
        {
            DecompositionSource = request.DecompositionSource
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
            RunId = result.CreatedRun.RunId,
            ProjectSteeringContent = result.Project.SteeringContent,
            RepoSteeringContent = result.ProviderConfigs
                .FirstOrDefault(c => c.Id == result.CreatedRun.RepoProviderConfigId)?.SteeringContent,
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
            await _infra.LabelService.SwapLabelAsync(
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
            await _infra.LabelService.SwapLabelAsync(
                request.IssueProviderConfigId, request.IssueIdentifier, AgentLabels.Next, ct);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to revert label for issue {IssueIdentifier} after distribution failure",
                request.IssueIdentifier);
        }
        // Note: in-memory run cleanup is no longer done here. The run is owned by the API's
        // IOrchestratorRunService; the API will remove it when the WorkItem transitions to a
        // terminal state via POST /api/work-items/{id}/status (Req 1a.1 Option A).
    }

    /// <summary>
    /// Merges profile-level and project-level MCP server configurations.
    /// Project servers override profile servers with the same Name (case-insensitive);
    /// new project server names are appended. Null or empty project servers = passthrough.
    /// </summary>
    internal static IReadOnlyList<McpServerConfig> MergeMcpServers(
        IReadOnlyList<McpServerConfig> profileServers,
        IReadOnlyList<McpServerConfig>? projectServers)
    {
        if (projectServers is null or { Count: 0 })
            return profileServers;

        var merged = profileServers.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var ps in projectServers)
            merged[ps.Name] = ps;

        return merged.Values.ToList();
    }
}
