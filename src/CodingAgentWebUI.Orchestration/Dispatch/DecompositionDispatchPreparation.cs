using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Strategy for preparing a decomposition dispatch.
/// Reserves a run, loads config early for workspace path, builds cross-repo project context,
/// and prepares provider configs with additional repo providers for project-level decomposition.
/// </summary>
internal sealed class DecompositionDispatchPreparation : IDispatchPreparationHandler
{
    private readonly DispatchInfrastructure _infra;
    private readonly IDispatchRunCreator _orchestration;
    private readonly ILogger _logger;
    private readonly AgentEntry _agent;
    private readonly string _epicIdentifier;
    private readonly string _epicTitle;
    private readonly PipelineRunType _phaseType;
    private readonly string _issueProviderId;
    private readonly string _repoProviderId;
    private readonly string? _brainProviderId;
    private readonly string _initiatedBy;
    private readonly string? _decompositionSource;

    public DecompositionDispatchPreparation(
        DispatchInfrastructure infra,
        IDispatchRunCreator orchestration,
        ILogger logger,
        AgentEntry agent,
        string epicIdentifier,
        string epicTitle,
        PipelineRunType phaseType,
        string issueProviderId,
        string repoProviderId,
        string? brainProviderId,
        string initiatedBy,
        string? decompositionSource)
    {
        _infra = infra;
        _orchestration = orchestration;
        _logger = logger;
        _agent = agent;
        _epicIdentifier = epicIdentifier;
        _epicTitle = epicTitle;
        _phaseType = phaseType;
        _issueProviderId = issueProviderId;
        _repoProviderId = repoProviderId;
        _brainProviderId = brainProviderId;
        _initiatedBy = initiatedBy;
        _decompositionSource = decompositionSource;
    }

    public async Task<AgentJobDispatcher.DispatchPipelineResult?> PrepareAsync(
        PipelineProject project,
        AgentProfile profile,
        string agentProviderId,
        CancellationToken ct)
    {
        // Reserve a run ID and dedup guard via PipelineOrchestrationService
        var reservation = await _orchestration.ReserveRunIdAsync(
            _issueProviderId, _repoProviderId, _epicIdentifier,
            agentProviderId, _agent.AgentId, ct,
            _brainProviderId, pipelineProviderId: null, _initiatedBy);

        if (reservation == null)
        {
            _logger.Warning("Failed to reserve run for decomposition of epic {EpicIdentifier}", _epicIdentifier);
            return null;
        }

        // Load config early — needed for WorkspaceBaseDirectory before settings override
        var config = await _infra.Resolution.ConfigStore.LoadPipelineConfigAsync(ct);
        var runId = reservation.RunId;
        var workspacePath = Path.Combine(config.WorkspaceBaseDirectory, "decomposition", runId);

        // Construct the fully-populated decomposition run using reserved metadata
        var run = PipelineRun.CreateDecomposition(
            runId: runId,
            issueIdentifier: _epicIdentifier,
            issueTitle: _epicTitle,
            issueProviderConfigId: _issueProviderId,
            repoProviderConfigId: _repoProviderId,
            phaseType: _phaseType,
            startedAt: reservation.StartedAt,
            initiatedBy: _initiatedBy,
            agentId: _agent.AgentId,
            agentProviderConfigId: agentProviderId,
            brainProviderConfigId: _brainProviderId,
            decompositionSource: _decompositionSource);
        run.RepositoryName = reservation.RepositoryName;
        run.ModelName = reservation.ModelName;
        run.WorkspacePath = workspacePath;

        // Atomically replace the sentinel with the fully-populated run
        // Note: ApplyRunMetadata is called by the template after this delegate returns
        _orchestration.RegisterDispatchedRun(run);

        // Build a synthetic IssueDetail from epic metadata for the job assignment
        var (syntheticIssueDetail, syntheticParsedIssue) = DispatchInfrastructure.BuildSyntheticIssueContext(
            _epicIdentifier, _epicTitle, null);

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
        var providerConfigs = await _infra.PrepareProviderConfigsAsync(
            _repoProviderId, agentProviderId, _brainProviderId, pipelineProviderId: null, _logger, ct, additionalRepoProviderIds);

        // Settings resolution: apply Project → Template overrides to the pre-loaded config
        config = await PipelineConfigurationResolver.ResolveAsync(
            config,
            _infra.Resolution.ConfigStore.LoadAllTemplatesAsync,
            project, _repoProviderId, _brainProviderId, providerConfigs, ct);

        var pipelineCtx = new AgentJobDispatcher.DispatchPipelineContext
        {
            Agent = _agent,
            Run = run,
            Profile = profile,
            IssueIdentifier = _epicIdentifier,
            IssueDetail = syntheticIssueDetail,
            ParsedIssue = syntheticParsedIssue,
            IssueComments = Array.Empty<IssueComment>(),
            RepoProviderId = _repoProviderId,
            AgentProviderId = agentProviderId,
            BrainProviderId = _brainProviderId,
            PipelineProviderId = null,
            IssueProviderId = _issueProviderId,
            ProviderConfigs = providerConfigs,
            Config = config,
            InitiatedBy = _initiatedBy,
            Project = project
        };

        Func<JobAssignmentMessage, JobAssignmentMessage> customize = msg => msg with
        {
            RunType = _phaseType,
            ProjectContext = projectContext
        };

        Action onSuccess = () =>
        {
            _logger.Information(
                "Decomposition {Phase} job {JobId} dispatched to agent {AgentId} for epic {EpicIdentifier} (profile={ProfileId}, project={ProjectName})",
                _phaseType, run.RunId, _agent.AgentId, _epicIdentifier, profile.Id, project.Name);
        };

        return new AgentJobDispatcher.DispatchPipelineResult(pipelineCtx, customize, onSuccess);
    }
}