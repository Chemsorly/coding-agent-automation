using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Orchestration.Dispatch;

public sealed partial class AgentJobDispatcher
{
    /// <summary>
    /// Parameter object holding shared context for building a <see cref="JobAssignmentMessage"/>.
    /// Used by <see cref="BuildBaseJobAssignmentMessage"/> to avoid 16+ parameter methods.
    /// </summary>
    private sealed class DispatchContext
    {
        public required string RunId { get; init; }
        public required string IssueIdentifier { get; init; }
        public required IssueDetail IssueDetail { get; init; }
        public required ParsedIssue ParsedIssue { get; init; }
        public required IReadOnlyList<IssueComment> IssueComments { get; init; }
        public required string RepoProviderId { get; init; }
        public required string AgentProviderId { get; init; }
        public string? BrainProviderId { get; init; }
        public string? PipelineProviderId { get; init; }
        public required IReadOnlyList<ProviderConfig> ProviderConfigs { get; init; }
        public required PipelineConfiguration Config { get; init; }
        public required string InitiatedBy { get; init; }
        public required string ProfileId { get; init; }
        public required IReadOnlyList<McpServerConfig> McpServers { get; init; }
        public required PipelineProject Project { get; init; }
        public string? IssueProviderId { get; init; }
    }

    /// <summary>
    /// Builds a <see cref="JobAssignmentMessage"/> with properties shared across all dispatch paths.
    /// Variant-specific properties (RunType, QualityGateConfigs, ReviewerConfigs, etc.) must be set
    /// by the caller on the returned message using <c>with</c> expressions.
    /// </summary>
    private static JobAssignmentMessage BuildBaseJobAssignmentMessage(DispatchContext ctx)
    {
        return new JobAssignmentMessage
        {
            JobId = ctx.RunId,
            IssueIdentifier = ctx.IssueIdentifier,
            IssueDetail = ctx.IssueDetail,
            ParsedIssue = ctx.ParsedIssue,
            IssueComments = ctx.IssueComments,
            RepoProviderConfigId = ctx.RepoProviderId,
            AgentProviderConfigId = ctx.AgentProviderId,
            BrainProviderConfigId = ctx.BrainProviderId,
            PipelineProviderConfigId = ctx.PipelineProviderId,
            ProviderConfigs = ctx.ProviderConfigs,
            PipelineConfiguration = ctx.Config,
            InitiatedBy = ctx.InitiatedBy,
            ResolvedProfileId = ctx.ProfileId,
            McpServers = ctx.McpServers,
            ProjectId = ctx.Project.Id,
            ProjectName = ctx.Project.Name,
            ProjectSecrets = ctx.Project.Secrets,
            TraceContext = CaptureTraceContext(),
            ProjectSteeringContent = ctx.Project.SteeringContent,
            // TODO: RepoSteeringContent lookup is effectively dead code — PrepareProviderConfigsAsync → TokenVendingService.CloneWithSettings
            // does not copy SteeringContent, so this always resolves to null. Consider passing SteeringContent separately or fixing CloneWithSettings.
            RepoSteeringContent = ctx.ProviderConfigs.FirstOrDefault(c => c.Id == ctx.RepoProviderId)?.SteeringContent,
            IssueProviderConfigId = ctx.IssueProviderId,
            // Variant-specific properties default to safe values; callers override via `with`
            QualityGateConfigs = Array.Empty<QualityGateConfiguration>(),
            ReviewerConfigs = Array.Empty<ReviewerConfiguration>()
        };
    }

    /// <summary>
    /// Dispatches a job to a specific agent. Resolves the agent profile and quality gate
    /// configurations, creates the PipelineRun, prepares configs, and sends the
    /// <see cref="JobAssignmentMessage"/> via SignalR.
    /// </summary>
    internal async Task<bool> DispatchToAgentAsync(
        AgentEntry agent,
        string issueIdentifier,
        string issueProviderId,
        string repoProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        string initiatedBy,
        IReadOnlyList<string> requiredLabels,
        CancellationToken ct,
        PipelineProject? project = null)
    {
        try
        {
            project = EnsureProject(project, issueIdentifier, "issue");

            // Resolve profile for this agent
            var profile = await _infra.Resolution.ResolveProfileAsync(agent, ct);
            if (profile is null)
                return false;

            var agentProviderId = profile.AgentProviderConfigId;

            // Resolve quality gate configurations for this job
            var resolvedQgcs = await _infra.Resolution.ResolveQualityGatesAsync(requiredLabels, ct);

            // Resolve reviewer configurations for this job
            var resolvedReviewerConfigs = await _infra.Resolution.ResolveReviewersAsync(requiredLabels, ct);

            // Create the dispatched run via PipelineOrchestrationService
            var run = await _orchestration.CreateDispatchedRunAsync(
                issueProviderId, repoProviderId, issueIdentifier,
                agentProviderId, agent.AgentId, ct,
                brainProviderId, pipelineProviderId, initiatedBy);

            if (run == null)
            {
                _logger.Warning("Failed to create dispatched run for issue {IssueIdentifier}", issueIdentifier);
                return false;
            }

            // Set project context on the run (captured at dispatch time)
            run.ProjectId = project.Id;
            run.ProjectName = project.Name;

            // Populate resolved profile and QGC IDs on the run
            run.ResolvedProfileId = profile.Id;
            run.ResolvedQualityGateConfigIds = resolvedQgcs.Select(q => q.Id).ToList().AsReadOnly();
            run.ResolvedReviewerConfigIds = resolvedReviewerConfigs.Select(r => r.Id).ToList().AsReadOnly();

            // Pre-fetch issue details and comments
            var issueContext = await PrepareIssueContextAsync(issueIdentifier, issueProviderId, ct);
            if (issueContext is null)
            {
                _logger.Error("Issue provider config '{ConfigId}' not found", issueProviderId);
                _runService.RemoveRun(run.RunId);
                return false;
            }

            // Update run with fetched issue title
            run.IssueTitle = issueContext.IssueDetail.Title;

            // Build and prepare provider configs for the agent
            var providerConfigs = await _infra.ProviderConfigPreparation.PrepareProviderConfigsAsync(
                repoProviderId, agentProviderId, brainProviderId, pipelineProviderId, ct);

            // Settings resolution: Global → Project overrides → Template overrides (blacklist from ProviderConfig)
            var config = await PipelineConfiguration.ResolveAsync(
                _infra.Resolution.ConfigStore.LoadPipelineConfigAsync,
                _infra.Resolution.ConfigStore.LoadAllTemplatesAsync,
                project, repoProviderId, brainProviderId, providerConfigs, ct);

            var ctx = new DispatchContext
            {
                RunId = run.RunId,
                IssueIdentifier = issueIdentifier,
                IssueDetail = issueContext.IssueDetail,
                ParsedIssue = issueContext.ParsedIssue,
                IssueComments = issueContext.IssueComments,
                RepoProviderId = repoProviderId,
                AgentProviderId = agentProviderId,
                BrainProviderId = brainProviderId,
                PipelineProviderId = pipelineProviderId,
                ProviderConfigs = providerConfigs,
                Config = config,
                InitiatedBy = initiatedBy,
                ProfileId = profile.Id,
                McpServers = profile.McpServers,
                Project = project,
                IssueProviderId = issueProviderId
            };

            var message = BuildBaseJobAssignmentMessage(ctx) with
            {
                ExistingAnalysis = issueContext.ExistingAnalysis,
                ForceRefreshAnalysis = issueContext.ForceRefreshAnalysis,
                StalenessSignal = issueContext.StalenessSignal,
                AnalysisRefreshCount = issueContext.RefreshCount,
                QualityGateConfigs = resolvedQgcs,
                ReviewerConfigs = resolvedReviewerConfigs
            };

            await AssignAndSendAsync(agent, run.RunId, message, ct);

            _logger.Information(
                "Job {JobId} dispatched to agent {AgentId} for issue {IssueIdentifier} (profile={ProfileId}, qgcs={QgcCount}, reviewerConfigs={ReviewerConfigCount}, project={ProjectName})",
                run.RunId, agent.AgentId, issueIdentifier, profile.Id, resolvedQgcs.Count, resolvedReviewerConfigs.Count, project.Name);

            if (resolvedReviewerConfigs.Count > 0)
            {
                var reviewerSummary = string.Join(", ", resolvedReviewerConfigs.Select(r =>
                    $"{r.DisplayName} (labels: [{string.Join(", ", r.MatchLabels)}])"));
                _logger.Debug("Job {JobId} resolved reviewer configs: {ReviewerSummary}", run.RunId, reviewerSummary);
            }

            return true;
        }
        catch (Exception ex)
        {
            await RevertDispatchFailureAsync(agent, ex,
                "Failed to dispatch job to agent {AgentId} for issue {IssueIdentifier}",
                issueProviderId, issueIdentifier, AgentLabels.Next);
            return false;
        }
    }

    /// <summary>
    /// Dispatches a PR review job to a specific agent. Creates the PipelineRun with review metadata,
    /// pre-fetches linked issues, and sends the <see cref="JobAssignmentMessage"/> via SignalR.
    /// </summary>
    internal async Task<bool> DispatchReviewToAgentAsync(
        AgentEntry agent,
        ReviewDispatchRequest request,
        IReadOnlyList<string> requiredLabels,
        CancellationToken ct,
        PipelineProject? project = null)
    {
        try
        {
            project = EnsureProject(project, request.PrIdentifier, "PR");

            // Resolve profile for this agent
            var profile = await _infra.Resolution.ResolveProfileAsync(agent, ct);
            if (profile is null)
                return false;

            var agentProviderId = profile.AgentProviderConfigId;

            // Resolve reviewer configurations for this job (quality gates not needed for reviews)
            var resolvedReviewerConfigs = await _infra.Resolution.ResolveReviewersAsync(requiredLabels, ct);

            // Create the dispatched run via PipelineOrchestrationService
            var run = await _orchestration.CreateDispatchedRunAsync(
                request.IssueProviderId, request.RepoProviderId, request.PrIdentifier,
                agentProviderId, agent.AgentId, ct,
                request.BrainProviderId, pipelineProviderId: null, request.InitiatedBy,
                PipelineRunType.Review);

            if (run == null)
            {
                _logger.Warning("Failed to create dispatched run for PR review {PrIdentifier}", request.PrIdentifier);
                return false;
            }

            // Pre-fetch linked issues before constructing the final run (non-fatal on failure)
            var linkedIssueContexts = await PreFetchLinkedIssuesAsync(
                request.PrIdentifier, request.IssueProviderId, request.RepoProviderId, ct);

            // Replace the initial run with a fully-populated review run atomically.
            // Using ReplaceRun instead of RemoveRun+AddRun eliminates the race window where
            // IsIssueBeingProcessed(prIdentifier) would return false during the gap.
            var previousRepositoryName = run.RepositoryName;
            var previousModelName = run.ModelName;
            run = PipelineRun.Create(
                runId: run.RunId,
                issueIdentifier: request.PrIdentifier,
                issueTitle: request.PrTitle,
                issueProviderConfigId: request.IssueProviderId,
                repoProviderConfigId: request.RepoProviderId,
                runType: PipelineRunType.Review,
                startedAt: run.StartedAtOffset,
                initiatedBy: request.InitiatedBy,
                agentId: agent.AgentId,
                agentProviderConfigId: agentProviderId,
                brainProviderConfigId: request.BrainProviderId,
                reviewPrBranchName: request.PrBranchName,
                reviewPrTargetBranch: request.PrTargetBranch,
                reviewPrUrl: request.PrUrl,
                reviewPrDescription: request.PrDescription,
                reviewPrAuthor: request.PrAuthor,
                linkedIssueContexts: linkedIssueContexts.Count > 0 ? linkedIssueContexts : null);
            run.RepositoryName = previousRepositoryName;
            run.ModelName = previousModelName;
            run.ProjectId = project.Id;
            run.ProjectName = project.Name;
            run.LinkedPullRequest = new LinkedPullRequest
            {
                Number = int.TryParse(request.PrIdentifier, out var prNum) ? prNum : 0,
                BranchName = request.PrBranchName,
                Url = request.PrUrl,
                IsDraft = false
            };

            _runService.ReplaceRun(run);

            // Populate resolved profile and reviewer config IDs on the run
            run.ResolvedProfileId = profile.Id;
            run.ResolvedReviewerConfigIds = resolvedReviewerConfigs.Select(r => r.Id).ToList().AsReadOnly();

            // Build and prepare provider configs for the agent
            var providerConfigs = await _infra.ProviderConfigPreparation.PrepareProviderConfigsAsync(
                request.RepoProviderId, agentProviderId, request.BrainProviderId, pipelineProviderId: null, ct);

            // Settings resolution: Global → Project overrides → Template overrides (blacklist from ProviderConfig)
            var config = await PipelineConfiguration.ResolveAsync(
                _infra.Resolution.ConfigStore.LoadPipelineConfigAsync,
                _infra.Resolution.ConfigStore.LoadAllTemplatesAsync,
                project, request.RepoProviderId, request.BrainProviderId, providerConfigs, ct);

            // NOTE: Label swap to agent:in-progress is handled by AssignAndSendAsync → AgentAcceptedRunAsync.
            // This ensures the label only changes when an agent actually accepts the job.

            // Build a synthetic IssueDetail and ParsedIssue from PR metadata for the job assignment
            var syntheticIssueDetail = new IssueDetail
            {
                Identifier = request.PrIdentifier,
                Title = request.PrTitle,
                Description = request.PrDescription ?? string.Empty,
                Labels = Array.Empty<string>()
            };
            var syntheticParsedIssue = new IssueDescriptionParser().Parse(request.PrDescription ?? string.Empty);

            var ctx = new DispatchContext
            {
                RunId = run.RunId,
                IssueIdentifier = request.PrIdentifier,
                IssueDetail = syntheticIssueDetail,
                ParsedIssue = syntheticParsedIssue,
                IssueComments = Array.Empty<IssueComment>(),
                RepoProviderId = request.RepoProviderId,
                AgentProviderId = agentProviderId,
                BrainProviderId = request.BrainProviderId,
                PipelineProviderId = null,
                ProviderConfigs = providerConfigs,
                Config = config,
                InitiatedBy = request.InitiatedBy,
                ProfileId = profile.Id,
                McpServers = profile.McpServers,
                Project = project,
                IssueProviderId = request.IssueProviderId
            };

            var message = BuildBaseJobAssignmentMessage(ctx) with
            {
                LinkedPullRequest = run.LinkedPullRequest,
                LinkedIssueContexts = linkedIssueContexts.Count > 0 ? linkedIssueContexts : null,
                RunType = PipelineRunType.Review,
                ReviewPrTargetBranch = request.PrTargetBranch,
                ReviewPrDescription = request.PrDescription,
                ReviewPrAuthor = request.PrAuthor,
                ReviewerConfigs = resolvedReviewerConfigs
            };

            await AssignAndSendAsync(agent, run.RunId, message, ct);

            _logger.Information(
                "Review job {JobId} dispatched to agent {AgentId} for PR {PrIdentifier} (profile={ProfileId}, reviewerConfigs={ReviewerConfigCount}, linkedIssues={LinkedIssueCount})",
                run.RunId, agent.AgentId, request.PrIdentifier, profile.Id, resolvedReviewerConfigs.Count, linkedIssueContexts.Count);

            return true;
        }
        catch (Exception ex)
        {
            await RevertDispatchFailureAsync(agent, ex,
                "Failed to dispatch review job to agent {AgentId} for PR {PrIdentifier}",
                request.RepoProviderId, request.PrIdentifier, AgentLabels.Next, LabelTargetKind.PullRequest);
            return false;
        }
    }

    /// <summary>
    /// Dispatches a decomposition job to a specific agent. Creates the PipelineRun with the
    /// correct RunType (DecompositionAnalysis or Decomposition), sets workspace path to
    /// <c>{base}/decomposition/{runId}/</c>, and sends the <see cref="JobAssignmentMessage"/> via SignalR.
    /// </summary>
    internal async Task<bool> DispatchDecompositionToAgentAsync(
        AgentEntry agent,
        string epicIdentifier,
        string epicTitle,
        PipelineRunType phaseType,
        string issueProviderId,
        string repoProviderId,
        string? brainProviderId,
        string initiatedBy,
        IReadOnlyList<string> requiredLabels,
        CancellationToken ct,
        string? decompositionSource = null,
        PipelineProject? project = null)
    {
        try
        {
            project = EnsureProject(project, epicIdentifier, "epic");

            // Resolve profile for this agent
            var profile = await _infra.Resolution.ResolveProfileAsync(agent, ct);
            if (profile is null)
                return false;

            var agentProviderId = profile.AgentProviderConfigId;

            // Create the dispatched run via PipelineOrchestrationService
            var run = await _orchestration.CreateDispatchedRunAsync(
                issueProviderId, repoProviderId, epicIdentifier,
                agentProviderId, agent.AgentId, ct,
                brainProviderId, pipelineProviderId: null, initiatedBy, phaseType);

            if (run == null)
            {
                _logger.Warning("Failed to create dispatched run for decomposition of epic {EpicIdentifier}", epicIdentifier);
                return false;
            }

            // Load config early — needed for WorkspaceBaseDirectory before settings override
            var config = await _infra.Resolution.ConfigStore.LoadPipelineConfigAsync(ct);
            var runId = run.RunId;
            var workspacePath = Path.Combine(config.WorkspaceBaseDirectory, "decomposition", runId);

            // Replace the initial run with a fully-populated decomposition run atomically.
            var previousRepositoryName = run.RepositoryName;
            var previousModelName = run.ModelName;
            run = PipelineRun.Create(
                runId: runId,
                issueIdentifier: epicIdentifier,
                issueTitle: epicTitle,
                issueProviderConfigId: issueProviderId,
                repoProviderConfigId: repoProviderId,
                runType: phaseType,
                startedAt: run.StartedAtOffset,
                initiatedBy: initiatedBy,
                agentId: agent.AgentId,
                agentProviderConfigId: agentProviderId,
                brainProviderConfigId: brainProviderId,
                decompositionSource: decompositionSource);
            run.RepositoryName = previousRepositoryName;
            run.ModelName = previousModelName;
            run.WorkspacePath = workspacePath;
            run.ProjectId = project.Id;
            run.ProjectName = project.Name;

            _runService.ReplaceRun(run);

            // Populate resolved profile ID on the run
            run.ResolvedProfileId = profile.Id;

            // Build a synthetic IssueDetail from epic metadata for the job assignment
            var syntheticIssueDetail = new IssueDetail
            {
                Identifier = epicIdentifier,
                Title = epicTitle,
                Description = string.Empty,
                Labels = Array.Empty<string>()
            };
            var syntheticParsedIssue = new IssueDescriptionParser().Parse(string.Empty);

            // Build DecompositionProjectContext for cross-repo decomposition (project-level epics only).
            // Per-template decomposition (EpicIssueProviderId is null) should NOT get project context.
            DecompositionProjectContext? projectContext = null;
            if (!string.IsNullOrEmpty(project.EpicIssueProviderId))
            {
                var repoProviderConfigs = await _infra.Resolution.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
                var repoConfigLookup = repoProviderConfigs.ToDictionary(c => c.Id);
                var templateLookup = (await _infra.Resolution.ConfigStore.LoadAllTemplatesAsync(ct)).ToDictionary(t => t.Id);

                var repositories = new List<RepositoryTarget>();
                foreach (var templateId in project.TemplateIds)
                {
                    if (!templateLookup.TryGetValue(templateId, out var tmpl))
                        continue;

                    var description = repoConfigLookup.TryGetValue(tmpl.RepoProviderId, out var repoCfg)
                        ? repoCfg.DisplayName
                        : tmpl.Name;

                    repositories.Add(new RepositoryTarget
                    {
                        TemplateName = tmpl.Name,
                        Description = description,
                        DecompositionEnabled = tmpl.DecompositionEnabled,
                        Available = tmpl.Enabled,
                        IssueProviderId = tmpl.IssueProviderId,
                        RepoProviderId = tmpl.RepoProviderId,
                        Labels = repoConfigLookup.TryGetValue(tmpl.RepoProviderId, out var rc)
                            ? (rc.RequiredLabels ?? [])
                            : []
                    });
                }

                projectContext = new DecompositionProjectContext
                {
                    ProjectName = project.Name,
                    Repositories = repositories.AsReadOnly()
                };
            }

            // Build and prepare provider configs for the agent.
            // For project-level decomposition, include all project repos' provider configs
            // so the agent can clone secondary repos for cross-repo code exploration.
            var additionalRepoProviderIds = projectContext?.Repositories
                .Select(r => r.RepoProviderId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Cast<string>();
            var providerConfigs = await _infra.ProviderConfigPreparation.PrepareProviderConfigsAsync(
                repoProviderId, agentProviderId, brainProviderId, pipelineProviderId: null, ct, additionalRepoProviderIds);

            // Settings resolution: apply Project → Template overrides to the pre-loaded config
            config = await PipelineConfiguration.ResolveAsync(
                config,
                _infra.Resolution.ConfigStore.LoadAllTemplatesAsync,
                project, repoProviderId, brainProviderId, providerConfigs, ct);

            var ctx = new DispatchContext
            {
                RunId = run.RunId,
                IssueIdentifier = epicIdentifier,
                IssueDetail = syntheticIssueDetail,
                ParsedIssue = syntheticParsedIssue,
                IssueComments = Array.Empty<IssueComment>(),
                RepoProviderId = repoProviderId,
                AgentProviderId = agentProviderId,
                BrainProviderId = brainProviderId,
                PipelineProviderId = null,
                ProviderConfigs = providerConfigs,
                Config = config,
                InitiatedBy = initiatedBy,
                ProfileId = profile.Id,
                McpServers = profile.McpServers,
                Project = project,
                IssueProviderId = issueProviderId
            };

            var message = BuildBaseJobAssignmentMessage(ctx) with
            {
                RunType = phaseType,
                ProjectContext = projectContext
            };

            // Swap label to agent:in-progress before dispatch so the epic is immediately marked
            // NOTE: Label swap to agent:in-progress is handled by AssignAndSendAsync → AgentAcceptedRunAsync.
            await AssignAndSendAsync(agent, run.RunId, message, ct);

            _logger.Information(
                "Decomposition {Phase} job {JobId} dispatched to agent {AgentId} for epic {EpicIdentifier} (profile={ProfileId}, project={ProjectName})",
                phaseType, run.RunId, agent.AgentId, epicIdentifier, profile.Id, project.Name);

            return true;
        }
        catch (Exception ex)
        {
            // Revert label on dispatch failure — Phase 1 reverts to agent:epic, Phase 2 reverts to agent:epic-approved
            var revertLabel = phaseType == PipelineRunType.DecompositionAnalysis
                ? AgentLabels.Epic
                : AgentLabels.EpicApproved;
            await RevertDispatchFailureAsync(agent, ex,
                "Failed to dispatch decomposition job to agent {AgentId} for epic {EpicIdentifier}",
                issueProviderId, epicIdentifier, revertLabel);
            return false;
        }
    }

    /// <summary>
    /// Ensures a non-null project, falling back to the Default project with a warning log
    /// if the template has no parent project (data corruption case).
    /// </summary>
    private PipelineProject EnsureProject(PipelineProject? project, string identifier, string identifierType)
    {
        if (project is not null)
            return project;

        _logger.Warning(
            "Template for {IdentifierType} {Identifier} has no parent project (data corruption). Assigning to Default project for settings resolution",
            identifierType, identifier);
        return new PipelineProject
        {
            Id = WellKnownIds.DefaultProjectId,
            Name = "Default"
        };
    }

    /// <summary>
    /// Assigns the job to the agent and sends the assignment via IAgentCommunication.
    /// Routes through IRunLifecycleManager.AgentAcceptedRunAsync to ensure label swap
    /// and agent state are handled uniformly across all modes.
    /// </summary>
    private async Task AssignAndSendAsync(AgentEntry agent, string runId, JobAssignmentMessage message, CancellationToken ct)
    {
        await _agentComm.AssignJobAsync(agent.ConnectionId, message, ct);

        if (_lifecycleManager is not null)
        {
            await _lifecycleManager.AgentAcceptedRunAsync(
                runId, agent.AgentId,
                message.IssueIdentifier, message.IssueProviderConfigId ?? "",
                message.RepoProviderConfigId ?? "", message.RunType, ct);
        }
        else
        {
            // Fallback for tests without lifecycle manager
            agent.ActiveJobId = runId;
            _registry.TransitionStatus(agent.AgentId, AgentStatus.Busy);
        }
    }

    /// <summary>
    /// Handles dispatch failure by resetting agent status, logging the error, and reverting the label.
    /// </summary>
    private async Task RevertDispatchFailureAsync(
        AgentEntry agent,
        Exception ex,
        string messageTemplate,
        string providerConfigId,
        string identifier,
        string revertLabel,
        LabelTargetKind? targetKind = null)
    {
        _logger.Error(ex, messageTemplate, agent.AgentId, identifier);

        // Remove the orphaned run from OrchestratorRunService to unblock future dispatch.
        // Without this, IsIssueBeingProcessed returns true forever in legacy mode.
        if (agent.ActiveJobId is not null)
            _runService.RemoveRun(agent.ActiveJobId);

        agent.ActiveJobId = null;
        _registry.TransitionStatus(agent.AgentId, AgentStatus.Idle);

        if (targetKind.HasValue)
            await _infra.LabelSwapper.SwapLabelAsync(providerConfigId, identifier, revertLabel, targetKind.Value, CancellationToken.None);
        else
            await _infra.LabelSwapper.SwapLabelAsync(providerConfigId, identifier, revertLabel, CancellationToken.None);
    }

    /// <summary>
    /// Pre-fetches linked issue details for a PR review dispatch.
    /// Calls <see cref="IRepositoryProvider.ExtractLinkedIssuesAsync"/> to get issue IDs,
    /// then fetches each issue's details via <see cref="IIssueProvider.GetIssueAsync"/>.
    /// Non-fatal: returns empty list on failure.
    /// </summary>
    private async Task<IReadOnlyList<LinkedIssueContext>> PreFetchLinkedIssuesAsync(
        string prIdentifier,
        string issueProviderId,
        string repoProviderId,
        CancellationToken ct)
    {
        var linkedIssueContexts = new List<LinkedIssueContext>();

        try
        {
            // Resolve repository provider to extract linked issues
            var repoConfig = await _infra.Resolution.ConfigStore.GetProviderConfigByIdAsync(repoProviderId, ProviderKind.Repository, ct);
            if (repoConfig == null)
            {
                _logger.Warning("Repo provider config '{ConfigId}' not found for linked issue extraction", repoProviderId);
                return linkedIssueContexts.AsReadOnly();
            }

            IReadOnlyList<string> linkedIssueIds;
            await using (var repoProvider = _infra.ProviderFactory.CreateRepositoryProvider(repoConfig))
            {
                if (!int.TryParse(prIdentifier, out var prNum))
                {
                    _logger.Warning("PR identifier '{PrIdentifier}' is not a valid integer, skipping linked issue extraction", prIdentifier);
                    return linkedIssueContexts.AsReadOnly();
                }

                linkedIssueIds = await repoProvider.ExtractLinkedIssuesAsync(prNum, ct);
            }

            if (linkedIssueIds.Count == 0)
            {
                _logger.Debug("No linked issues found for PR {PrIdentifier}", prIdentifier);
                return linkedIssueContexts.AsReadOnly();
            }

            // Resolve issue provider to fetch issue details
            var issueConfig = await _infra.Resolution.ConfigStore.GetProviderConfigByIdAsync(issueProviderId, ProviderKind.Issue, ct);
            if (issueConfig == null)
            {
                _logger.Warning("Issue provider config '{ConfigId}' not found for linked issue pre-fetch", issueProviderId);
                return linkedIssueContexts.AsReadOnly();
            }

            await using (var issueProvider = _infra.ProviderFactory.CreateIssueProvider(issueConfig))
            {
                foreach (var issueId in linkedIssueIds)
                {
                    try
                    {
                        var issueDetail = await issueProvider.GetIssueAsync(issueId, ct);
                        linkedIssueContexts.Add(new LinkedIssueContext
                        {
                            Identifier = issueId,
                            Title = issueDetail.Title,
                            Description = issueDetail.Description
                        });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.Warning(ex, "Failed to fetch linked issue {IssueId} for PR {PrIdentifier}", issueId, prIdentifier);
                    }
                }
            }

            _logger.Information("Pre-fetched {Count} linked issue(s) for PR {PrIdentifier}", linkedIssueContexts.Count, prIdentifier);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Failed to pre-fetch linked issues for PR {PrIdentifier}, continuing with empty context", prIdentifier);
        }

        return linkedIssueContexts.AsReadOnly();
    }

    /// <summary>
    /// Pre-fetches issue details, comments, swaps labels, and detects existing analysis.
    /// Returns null if the issue provider config is not found.
    /// </summary>
    private async Task<IssueContext?> PrepareIssueContextAsync(
        string issueIdentifier,
        string issueProviderId,
        CancellationToken ct)
    {
        var issueConfig = await _infra.Resolution.ConfigStore.GetProviderConfigByIdAsync(issueProviderId, ProviderKind.Issue, ct);
        if (issueConfig == null)
            return null;

        IssueDetail issueDetail;
        ParsedIssue parsedIssue;
        IReadOnlyList<IssueComment> issueComments;
        await using (var issueProvider = _infra.ProviderFactory.CreateIssueProvider(issueConfig))
        {
            issueDetail = await issueProvider.GetIssueAsync(issueIdentifier, ct);
            parsedIssue = new IssueDescriptionParser().Parse(issueDetail.Description);
            var allComments = await issueProvider.ListCommentsAsync(issueIdentifier, ct);
            // Cap at 50 comments per REQ-4.4
            issueComments = allComments.Count > 50
                ? allComments.Take(50).ToList().AsReadOnly()
                : allComments;
        }

        // NOTE: Label swap to agent:in-progress is NOT done here.
        // It's handled by IRunLifecycleManager.AgentAcceptedRunAsync after the agent accepts the job,
        // ensuring label state is consistent across all three modes (Legacy, SignalR, K8s).

        // Detect existing analysis and rework state from comments
        // TODO: Legacy mode (AgentJobDispatcher) does not invoke AnalysisStalenessDetector for the
        // three new signals (body_changed, agent_error, commit_threshold). Only gate_rejection and
        // gate_wont_do are detected here. If legacy mode still processes issues, staleness detection
        // silently does not apply. Consider wiring AnalysisStalenessDetector here as well.
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
            var gateRejection = issueComments.FirstOrDefault(c => c.Body.Contains(CommentMarkers.GateRejection));
            var gateWontDo = issueComments.FirstOrDefault(c => c.Body.Contains(CommentMarkers.GateWontDo));
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

        return new IssueContext(issueDetail, parsedIssue, issueComments, existingAnalysis, forceRefreshAnalysis, stalenessSignal);
    }

    internal static Dictionary<string, string>? CaptureTraceContext() =>
        PipelineTelemetry.CaptureTraceContext("DispatchJob");
}
