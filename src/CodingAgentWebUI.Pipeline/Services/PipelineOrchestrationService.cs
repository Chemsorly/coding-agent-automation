using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog.Context;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Singleton service that coordinates the entire automated development pipeline.
/// Manages the active pipeline run, state transitions, chat interaction, retry logic,
/// quality gate validation, and PR creation.
/// Supports both local execution (single <see cref="ActiveRun"/>) and multi-agent
/// dispatch (concurrent runs tracked via <see cref="IOrchestratorRunService"/>).
/// </summary>
public class PipelineOrchestrationService : IDisposable, IAsyncDisposable
{
    private readonly IConfigurationStore _configStore;
    private readonly IProviderFactory _providerFactory;
    private readonly IssueDescriptionParser _issueParser;
    private readonly BrainSyncOrchestrator _brainSync;
    private readonly PullRequestOrchestrator _prOrchestrator;
    private readonly IPipelineRunHistoryService _historyService;
    private readonly AgentExecutionOrchestrator _agentExecution;
    private readonly QualityGateOrchestrator _qualityGates;
    private readonly IOrchestratorRunService? _runService;
    private readonly Serilog.ILogger _logger;

    protected CancellationTokenSource? _cancellationTokenSource;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    protected IAgentProvider? _activeAgentProvider;
    protected IRepositoryProvider? _activeRepoProvider;
    protected IRepositoryProvider? _activeBrainProvider;
    protected IIssueProvider? _activeIssueProvider;
    protected IPipelineProvider? _activePipelineProvider;
    protected PipelineConfiguration? _activeConfig;

    protected IssueDetail? _activeIssue;
    protected ParsedIssue? _activeParsedIssue;
    protected IReadOnlyList<IssueComment>? _activeIssueComments;

    /// <summary>Fired after each state transition for UI binding.</summary>
    public event Action? OnChange;

    /// <summary>Fired for each agent output line for real-time display.</summary>
    public event Action<string>? OnOutputLine;

    /// <summary>Clears all event subscribers. Used by subclasses for state reset.</summary>
    protected void ClearEventSubscribers()
    {
        OnChange = null;
        OnOutputLine = null;
    }

    /// <summary>The currently active local pipeline run, or null if idle. For agent runs, use <see cref="GetAllActiveRuns"/>.</summary>
    public PipelineRun? ActiveRun { get; protected set; }

    /// <summary>Whether a local pipeline run is currently in progress.</summary>
    public bool IsRunning => ActiveRun != null
        && ActiveRun.CurrentStep != PipelineStep.Completed
        && ActiveRun.CurrentStep != PipelineStep.Failed
        && ActiveRun.CurrentStep != PipelineStep.Cancelled;

    /// <summary>
    /// Checks whether the given issue is being processed by any active run (local or agent).
    /// Use this instead of <see cref="IsRunning"/> when dispatching to agents.
    /// </summary>
    public bool IsIssueBeingProcessed(string issueIdentifier)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);

        // Check local run
        if (ActiveRun != null && ActiveRun.IssueIdentifier == issueIdentifier && IsRunning)
            return true;

        // Check agent runs via OrchestratorRunService
        return _runService?.IsIssueBeingProcessed(issueIdentifier) == true;
    }

    /// <summary>
    /// Returns all active runs — both the local run (if any) and all agent-dispatched runs.
    /// </summary>
    public IReadOnlyList<PipelineRun> GetAllActiveRuns()
    {
        var runs = new List<PipelineRun>();

        if (ActiveRun != null && IsRunning)
            runs.Add(ActiveRun);

        if (_runService != null)
            runs.AddRange(_runService.GetActiveRuns());

        return runs.AsReadOnly();
    }

    /// <summary>
    /// Whether any pipeline run is active (local or agent).
    /// </summary>
    public bool HasAnyActiveRuns => IsRunning || (_runService?.HasActiveRuns == true);

    public PipelineOrchestrationService(
        IConfigurationStore configStore,
        IProviderFactory providerFactory,
        IssueDescriptionParser issueParser,
        IQualityGateValidator qualityGateValidator,
        CiLogWriter ciLogWriter,
        Serilog.ILogger logger,
        IBrainUpdateService? brainUpdateService = null,
        IPipelineRunHistoryService? historyService = null,
        IOrchestratorRunService? runService = null)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(issueParser);
        ArgumentNullException.ThrowIfNull(qualityGateValidator);
        ArgumentNullException.ThrowIfNull(ciLogWriter);
        ArgumentNullException.ThrowIfNull(logger);

        _configStore = configStore;
        _providerFactory = providerFactory;
        _issueParser = issueParser;
        _logger = logger;
        _runService = runService;

        ArgumentNullException.ThrowIfNull(brainUpdateService);
        _brainSync = new BrainSyncOrchestrator(brainUpdateService, logger);
        _prOrchestrator = new PullRequestOrchestrator(logger);
        _historyService = historyService ?? throw new ArgumentNullException(nameof(historyService));
        _agentExecution = new AgentExecutionOrchestrator(logger);
        _qualityGates = new QualityGateOrchestrator(qualityGateValidator, ciLogWriter, _prOrchestrator, logger);
    }

    /// <summary>
    /// Starts a new pipeline run for the given issue and repository providers.
    /// Rejects if a pipeline is already running.
    /// </summary>
    public async Task<PipelineRun> StartPipelineAsync(
        string issueProviderId, string repoProviderId, string issueIdentifier,
        string agentProviderId, CancellationToken ct, string? brainProviderId = null, string? pipelineProviderId = null,
        string initiatedBy = "manual")
    {
        ArgumentNullException.ThrowIfNull(issueProviderId);
        ArgumentNullException.ThrowIfNull(repoProviderId);
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        ArgumentNullException.ThrowIfNull(agentProviderId);

        await _startLock.WaitAsync(ct);
        try
        {
            if (IsRunning)
                throw new InvalidOperationException("A pipeline run is already in progress.");

            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }
        finally
        {
            _startLock.Release();
        }

        var linkedCt = _cancellationTokenSource.Token;

        try
        {
            _activeConfig = await _configStore.LoadPipelineConfigAsync(linkedCt);
            _historyService.CleanupExpiredWorkspaces(_activeConfig, ActiveRun?.RunId);

            var issueProviderConfig = await ResolveProviderConfigAsync(issueProviderId, ProviderKind.Issue, linkedCt);
            var repoProviderConfig = await ResolveProviderConfigAsync(repoProviderId, ProviderKind.Repository, linkedCt);
            var agentProviderConfig = await ResolveProviderConfigAsync(agentProviderId, ProviderKind.Agent, linkedCt);

            await DisposePreviousProvidersAsync();
            _activeIssueProvider = _providerFactory.CreateIssueProvider(issueProviderConfig);
            var issueProvider = _activeIssueProvider;
            _activeRepoProvider = _providerFactory.CreateRepositoryProvider(repoProviderConfig);
            _activeAgentProvider = _providerFactory.CreateAgentProvider(agentProviderConfig);
            _activeBrainProvider = null;
            if (!string.IsNullOrEmpty(brainProviderId))
            {
                try
                {
                    var brainProviderConfig = await ResolveProviderConfigAsync(brainProviderId, ProviderKind.Repository, linkedCt);
                    _activeBrainProvider = _providerFactory.CreateRepositoryProvider(brainProviderConfig);
                    try { await _activeBrainProvider.ValidateAsync(linkedCt); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.Warning(ex, "Brain provider validation failed, disabling brain sync for this run");
                        if (_activeBrainProvider is IAsyncDisposable disposable) await disposable.DisposeAsync();
                        _activeBrainProvider = null;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex, "Failed to resolve brain provider {BrainProviderId}, continuing without brain", brainProviderId);
                    _activeBrainProvider = null;
                }
            }
            var configuredModel = agentProviderConfig.Settings.GetValueOrDefault("model", "auto");
            var run = new PipelineRun
            {
                RunId = Guid.NewGuid().ToString(),
                IssueIdentifier = issueIdentifier,
                IssueTitle = string.Empty,
                IssueProviderConfigId = issueProviderId,
                RepoProviderConfigId = repoProviderId,
                StartedAt = DateTime.UtcNow,
                CurrentStep = PipelineStep.Created,
                RepositoryName = _activeRepoProvider.RepositoryFullName,
                ModelName = configuredModel,
                BrainProviderConfigId = _activeBrainProvider != null ? brainProviderId : null,
                InitiatedBy = initiatedBy
            };
            ActiveRun = run;
            _logger.Information("Pipeline {RunId} using model {Model}", run.RunId, configuredModel);
            _activePipelineProvider = null;
            if (_activeConfig.ExternalCiEnabled)
            {
                ProviderConfig? pipelineProviderConfig = null;
                if (!string.IsNullOrEmpty(pipelineProviderId))
                    pipelineProviderConfig = await ResolveProviderConfigAsync(pipelineProviderId, ProviderKind.Pipeline, linkedCt);
                else
                {
                    var pipelineConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Pipeline, linkedCt);
                    if (pipelineConfigs.Count > 0) pipelineProviderConfig = pipelineConfigs[0];
                }
                if (pipelineProviderConfig is not null)
                {
                    _activePipelineProvider = _providerFactory.CreatePipelineProvider(pipelineProviderConfig);
                    run.PipelineProviderConfigId = pipelineProviderConfig.Id;
                    _logger.Information("Pipeline {RunId} external CI provider configured", run.RunId);
                }
                else
                    _logger.Warning("Pipeline {RunId} external CI enabled but no pipeline provider configured", run.RunId);
            }
            await ValidateProvidersAsync(_activeRepoProvider, repoProviderConfig,
                _activeAgentProvider, agentProviderConfig, _activePipelineProvider, linkedCt);
            _logger.Information("Pipeline {RunId} created for issue {IssueIdentifier}", run.RunId, issueIdentifier);
            NotifyChange();
            try
            {
                var labelsOk = await issueProvider.InitializeAsync(linkedCt);
                if (!labelsOk)
                    _logger.Warning("Pipeline {RunId} issue provider label creation partially failed, continuing", run.RunId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw new InvalidOperationException($"Issue provider initialization failed: {ex.Message}", ex); }
            await PersistLastUsedProviderIdsAsync(issueProviderId, repoProviderId, agentProviderId, brainProviderId, pipelineProviderId, linkedCt);
            await ExecutePipelineStepsAsync(run, issueProvider, linkedCt);
            return run;
        }
        catch (Exception ex) when (ex is not InvalidOperationException || ActiveRun != null)
        {
            if (ActiveRun != null && ActiveRun.CurrentStep != PipelineStep.Failed
                && ActiveRun.CurrentStep != PipelineStep.Cancelled)
                await HandlePipelineErrorAsync(ActiveRun, ex);
            throw;
        }
    }
    private void EmitOutputLine(string message)
    {
        try { OnOutputLine?.Invoke(message); }
        catch (Exception ex) { _logger.Warning(ex, "OnOutputLine handler threw an exception"); }
    }

    /// <summary>
    /// Creates a <see cref="PipelineRun"/> for dispatch to a remote agent.
    /// The run is tracked via <see cref="IOrchestratorRunService"/> (not the local <see cref="ActiveRun"/>).
    /// Does NOT execute the pipeline locally — the agent handles execution.
    /// </summary>
    /// <returns>The created <see cref="PipelineRun"/> ready for dispatch, or <c>null</c> if the issue is already being processed.</returns>
    public async Task<PipelineRun?> CreateDispatchedRunAsync(
        string issueProviderId, string repoProviderId, string issueIdentifier,
        string agentProviderId, string agentId, CancellationToken ct,
        string? brainProviderId = null, string? pipelineProviderId = null,
        string initiatedBy = "dispatch")
    {
        ArgumentNullException.ThrowIfNull(issueProviderId);
        ArgumentNullException.ThrowIfNull(repoProviderId);
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        ArgumentNullException.ThrowIfNull(agentProviderId);
        ArgumentNullException.ThrowIfNull(agentId);

        if (_runService == null)
            throw new InvalidOperationException("OrchestratorRunService is not configured. Cannot dispatch to agents.");

        // Check if this issue is already being processed (local or agent)
        if (IsIssueBeingProcessed(issueIdentifier))
        {
            _logger.Warning("Issue {IssueIdentifier} is already being processed, skipping dispatch", issueIdentifier);
            return null;
        }

        var config = await _configStore.LoadPipelineConfigAsync(ct);

        // Resolve repo provider config to get repository name
        var repoProviderConfig = await ResolveProviderConfigAsync(repoProviderId, ProviderKind.Repository, ct);
        await using var tempRepoProvider = _providerFactory.CreateRepositoryProvider(repoProviderConfig);
        var agentProviderConfig = await ResolveProviderConfigAsync(agentProviderId, ProviderKind.Agent, ct);
        var configuredModel = agentProviderConfig.Settings.GetValueOrDefault("model", "auto");

        var run = new PipelineRun
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = issueIdentifier,
            IssueTitle = string.Empty,
            IssueProviderConfigId = issueProviderId,
            RepoProviderConfigId = repoProviderId,
            StartedAt = DateTime.UtcNow,
            CurrentStep = PipelineStep.Created,
            RepositoryName = tempRepoProvider.RepositoryFullName,
            ModelName = configuredModel,
            BrainProviderConfigId = brainProviderId,
            PipelineProviderConfigId = pipelineProviderId,
            InitiatedBy = initiatedBy,
            AgentId = agentId
        };

        // Track in OrchestratorRunService
        _runService.AddRun(run);

        _logger.Information(
            "Dispatched run {RunId} created for issue {IssueIdentifier} → agent {AgentId}",
            run.RunId, issueIdentifier, agentId);

        NotifyChange();
        return run;
    }

    private async Task ExecutePipelineStepsAsync(
        PipelineRun run, IIssueProvider issueProvider, CancellationToken ct)
    {
        using var _ = LogContext.PushProperty("PipelineRunId", run.RunId);

        try
        {
            // Fetch and validate issue
            IssueDetail issue;
            try { issue = await issueProvider.GetIssueAsync(run.IssueIdentifier, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error(ex, "Pipeline {RunId} failed to fetch issue {IssueIdentifier}", run.RunId, run.IssueIdentifier);
                await FailRunAsync(run, $"Failed to fetch issue: {ex.Message}");
                return;
            }

            if (string.IsNullOrWhiteSpace(issue.Title) || string.IsNullOrWhiteSpace(issue.Description))
            { _logger.Warning("Pipeline {RunId} issue has insufficient information", run.RunId); await FailRunAsync(run, "insufficient issue information"); return; }

            run.IssueTitle = issue.Title;
            run.IssueLabels = issue.Labels;
            var parsed = _issueParser.Parse(issue.Description);
            _activeIssue = issue;
            _activeParsedIssue = parsed;

            EmitOutputLine($"🚀 Pipeline started for issue #{run.IssueIdentifier} — {issue.Title}");

            IReadOnlyList<IssueComment> issueComments = Array.Empty<IssueComment>();
            try
            {
                issueComments = await issueProvider.ListCommentsAsync(run.IssueIdentifier, ct);
                _logger.Information("Pipeline {RunId} fetched {CommentCount} comment(s) for issue {IssueIdentifier}",
                    run.RunId, issueComments.Count, run.IssueIdentifier);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.Warning(ex, "Pipeline {RunId} failed to fetch issue comments, proceeding without them", run.RunId); }
            _activeIssueComments = issueComments;
            // Create workspace and clone
            var workspacePath = Path.Combine(_activeConfig!.WorkspaceBaseDirectory, run.RunId);
            Directory.CreateDirectory(workspacePath);
            run.WorkspacePath = workspacePath;

            TransitionTo(run, PipelineStep.CloningRepository);
            EmitOutputLine($"📋 Cloning repository {run.RepositoryName}...");
            await SwapAgentLabelAsync(run.IssueIdentifier, AgentLabels.InProgress, ct);
            try { await _activeRepoProvider!.CloneAsync(workspacePath, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.Error(ex, "Pipeline {RunId} failed to clone repository", run.RunId); await FailRunAsync(run, $"Repository clone failed: {ex.Message}"); return; }
            // Brain sync pre-run
            if (_activeBrainProvider != null)
            {
                TransitionTo(run, PipelineStep.SyncingBrainRepoPreRun);
                try { await _brainSync.SyncPreRunAsync(run, _activeBrainProvider, workspacePath, ct, line => EmitOutputLine(line)); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex, "Pipeline {RunId} brain sync failed, continuing without brain context", run.RunId);
                    run.BrainContextLoaded = false;
                }
            }
            // Rework detection: check for existing agent PRs before branch setup
            try
            {
                var agentPrs = await _activeRepoProvider!.GetAgentPullRequestsAsync(run.IssueIdentifier, ct);
                if (agentPrs.Count > 0)
                {
                    var selectedPr = agentPrs.OrderByDescending(pr => pr.Number).First();
                    run.LinkedPullRequest = selectedPr;
                    EmitOutputLine($"🔄 Rework mode: updating existing PR #{selectedPr.Number}");
                    _logger.Information("Pipeline {RunId} detected existing agent PR #{PrNumber} for issue {IssueIdentifier}, entering rework mode",
                        run.RunId, selectedPr.Number, run.IssueIdentifier);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "Pipeline {RunId} failed to detect agent PRs for issue {IssueIdentifier}, falling back to new-issue flow",
                    run.RunId, run.IssueIdentifier);
            }

            // Create branch (or checkout existing branch in rework mode)
            TransitionTo(run, PipelineStep.CreatingBranch);
            if (run.LinkedPullRequest != null)
            {
                // Rework mode: checkout existing PR branch
                try
                {
                    await _activeRepoProvider!.CheckoutRemoteBranchAsync(
                        workspacePath, run.LinkedPullRequest.BranchName, ct);
                    run.BranchName = run.LinkedPullRequest.BranchName;
                    EmitOutputLine($"🌿 Checked out existing branch {run.BranchName}");
                    _logger.Information("Pipeline {RunId} checked out existing branch {BranchName}", run.RunId, run.BranchName);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Error(ex, "Pipeline {RunId} failed to checkout branch {BranchName}", run.RunId, run.LinkedPullRequest.BranchName);
                    await FailRunAsync(run, $"Branch checkout failed: {ex.Message}");
                    return;
                }

                // Merge from base branch
                try
                {
                    var mergeResult = await _activeRepoProvider.MergeFromBaseAsync(workspacePath, ct);
                    run.MergeConflictFiles = mergeResult.ConflictFiles;
                    if (mergeResult.HasConflicts)
                    {
                        EmitOutputLine($"⚠️ Merged from {_activeRepoProvider.BaseBranch} with {mergeResult.ConflictFiles.Count} conflict(s)");
                        _logger.Information("Pipeline {RunId} merged from base with {ConflictCount} conflict(s)", run.RunId, mergeResult.ConflictFiles.Count);
                    }
                    else
                    {
                        EmitOutputLine($"🔀 Merged from {_activeRepoProvider.BaseBranch} (no conflicts)");
                        _logger.Information("Pipeline {RunId} merged from base (no conflicts)", run.RunId);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Error(ex, "Pipeline {RunId} failed to merge from base branch", run.RunId);
                    await FailRunAsync(run, $"Base branch merge failed: {ex.Message}");
                    return;
                }
            }
            else
            {
                // New-issue flow: create new branch
                EmitOutputLine("🌿 Creating branch...");
                try
                {
                    var branchName = PipelineFormatting.GenerateBranchName(run.IssueIdentifier, issue.Title, run.RunId);
                    run.BranchName = await _activeRepoProvider.CreateBranchAsync(workspacePath, branchName, ct);
                    _logger.Information("Pipeline {RunId} branch {BranchName} created", run.RunId, run.BranchName);
                    EmitOutputLine($"🌿 Created branch {run.BranchName}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Error(ex, "Pipeline {RunId} failed to create branch", run.RunId);
                    await FailRunAsync(run, $"Branch creation failed: {ex.Message}");
                    return;
                }
            }
            // Analysis phase
            EmitOutputLine("🔍 Starting analysis...");
            IAgentIssueOperations issueOps = new IssueProviderIssueOperations(_activeIssueProvider!, _logger);
            var analysisShouldContinue = await _agentExecution.ExecuteAnalysisPhaseAsync(
                run, _activeConfig, _activeAgentProvider!, issueOps,
                issue, parsed, issueComments,
                step => TransitionTo(run, step),
                r => AddRunToHistory(r),
                line => EmitOutputLine(line), () => NotifyChange(), ct);
            if (!analysisShouldContinue) return;
            // Code generation phase
            string? reworkPromptOverride = null;
            if (run.LinkedPullRequest != null)
            {
                reworkPromptOverride = PromptBuilder.BuildReworkPrompt(
                    run.MergeConflictFiles,
                    run.LinkedPullRequest.ReviewComments,
                    isDraft: run.LinkedPullRequest.IsDraft);

                if (reworkPromptOverride == null)
                {
                    // Nothing to rework — no conflicts, no comments, not draft. Skip code generation.
                    EmitOutputLine("⏭️ No conflicts, review comments, or draft status — skipping code generation");
                    _logger.Information("Pipeline {RunId} rework prompt is null (no conflicts, no comments, not draft), skipping code generation", run.RunId);
                }
                else
                {
                    EmitOutputLine("⚙️ Starting rework code generation...");
                }
            }
            else
            {
                EmitOutputLine("⚙️ Starting code generation...");
            }

            if (reworkPromptOverride != null || run.LinkedPullRequest == null)
            {
                var shouldContinue = await _agentExecution.ExecuteCodeGenerationAsync(
                    run, _activeConfig, _activeAgentProvider!,
                    issue, parsed,
                    _cancellationTokenSource,
                    step => TransitionTo(run, step),
                    line => OnOutputLine?.Invoke(line), () => NotifyChange(),
                    r => UpdateFileChangeStatsAsync(r),
                    issueOps,
                    r => AddRunToHistory(r), ct,
                    promptOverride: reworkPromptOverride);
                if (!shouldContinue) return;
            }
            // Brain pull before write
            if (_activeBrainProvider != null && !_activeConfig!.BrainReadOnly && run.BrainContextLoaded)
            {
                try { await _brainSync.PullBeforeWriteAsync(run, _activeBrainProvider, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { _logger.Warning(ex, "Pipeline {RunId} brain repo pull-before-write failed, continuing", run.RunId); }
            }
            // Code review phase
            var allReviewerConfigs = await _configStore.LoadReviewerConfigsAsync(ct);
            var reviewerResolver = new ReviewerResolver();
            var repoConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
            var repoConfigForLabels = repoConfigs.FirstOrDefault(c => c.Id == run.RepoProviderConfigId);
            var requiredLabelsForReview = LabelResolver.ResolveRequiredLabels(repoConfigForLabels, _activeConfig!);
            var resolvedReviewers = reviewerResolver.Resolve(allReviewerConfigs, requiredLabelsForReview);

            await _agentExecution.ExecuteCodeReviewAsync(
                run, _activeConfig!, _activeAgentProvider!,
                _activeIssue!, _activeParsedIssue!,
                _cancellationTokenSource,
                step => TransitionTo(run, step),
                line => OnOutputLine?.Invoke(line), () => NotifyChange(), ct,
                resolvedReviewers);
            // Quality gates
            var allQgcs = await _configStore.LoadQualityGateConfigsAsync(ct);
            var qualityGateContext = new QualityGateContext
            {
                Run = run,
                Config = _activeConfig!,
                AgentProvider = _activeAgentProvider!,
                RepoProvider = _activeRepoProvider!,
                PipelineProvider = _activePipelineProvider,
                OrchestratorCts = _cancellationTokenSource,
                TransitionTo = step => TransitionTo(run, step),
                IssueOps = issueOps,
                RemoveAllAgentLabels = (id, token) => RemoveAllAgentLabelsAsync(id, token),
                AddRunToHistory = r => AddRunToHistory(r),
                OnOutputLine = line => OnOutputLine?.Invoke(line),
                OnChange = () => NotifyChange(),
                CreatePullRequest = (r, report, isDraft, token) => CreatePullRequestAsync(r, report, isDraft, token),
                QualityGateConfigs = allQgcs,
                QgcsConfiguredAtDispatch = allQgcs.Count > 0
            };
            await _qualityGates.ProceedToQualityGatesAsync(qualityGateContext, ct);
        }
        catch (OperationCanceledException)
        {
            if (run.CurrentStep is not (PipelineStep.Cancelled or PipelineStep.Failed))
            {
                _logger.Information("Pipeline {RunId} was cancelled", run.RunId);
                run.CompletedAt = DateTime.UtcNow;
                await SwapAgentLabelAsync(run.IssueIdentifier, AgentLabels.Cancelled, CancellationToken.None);
                EmitOutputLine("🚫 Pipeline cancelled");
                TransitionTo(run, PipelineStep.Cancelled);
                AddRunToHistory(run);
            }
        }
    }
    /// <summary>Cancels the active pipeline run.</summary>
    public async Task CancelPipelineAsync()
    {
        if (ActiveRun == null || !IsRunning) return;
        var run = ActiveRun;
        using var _ = LogContext.PushProperty("PipelineRunId", run.RunId);
        _logger.Information("Pipeline {RunId} cancellation requested", run.RunId);
        _cancellationTokenSource?.Cancel();
        run.CompletedAt = DateTime.UtcNow;
        if (_activeIssueProvider != null)
            await SwapAgentLabelAsync(run.IssueIdentifier, AgentLabels.Cancelled, CancellationToken.None);
        EmitOutputLine("🚫 Pipeline cancelled");
        TransitionTo(run, PipelineStep.Cancelled);
        AddRunToHistory(run);
    }
    /// <summary>Returns the in-memory run history.</summary>
    public IReadOnlyList<PipelineRunSummary> GetRunHistory() => _historyService.GetRunHistory();
    // --- Private helpers ---
    private async Task CreatePullRequestAsync(
        PipelineRun run, QualityGateReport report, bool isDraft, CancellationToken ct)
    {
        TransitionTo(run, PipelineStep.CreatingPullRequest);
        try
        {
            // Set PR info from linked PR before calling the orchestrator (rework mode)
            if (run.LinkedPullRequest != null)
            {
                run.PullRequestUrl = run.LinkedPullRequest.Url;
                run.PullRequestNumber = run.LinkedPullRequest.Number.ToString();
            }

            var prUrl = await _prOrchestrator.CreatePullRequestAsync(
                run, report, isDraft, _activeRepoProvider!, _activeIssue,
                _activeIssueComments, _activeConfig!, ct, line => EmitOutputLine(line),
                isRework: run.LinkedPullRequest != null);

            if (prUrl == null && _activeConfig?.BlacklistMode == BlacklistMode.Fail
                && run.BlacklistedFilesDetected.Count > 0)
            { await FailRunAsync(run, $"Blacklisted files detected: {string.Join(", ", run.BlacklistedFilesDetected)}. The agent modified protected paths."); return; }

            if (prUrl == null)
            { await FailRunAsync(run, "Agent did not produce any changes. No commits ahead of base branch.", ct); return; }

            var finalStep = isDraft ? PipelineStep.Failed : PipelineStep.Completed;
            if (isDraft)
            {
                run.FailureReason = "Quality gates failed after max retries; draft PR created.";
                await SwapAgentLabelAsync(run.IssueIdentifier, AgentLabels.Error, ct);
            }
            else
                await SwapAgentLabelAsync(run.IssueIdentifier, AgentLabels.Done, ct);

            if (!isDraft && _activeBrainProvider != null && !_activeConfig!.BrainReadOnly)
            {
                // Reflection step: ask the agent to review the entire run and enrich .brain/ knowledge
                TransitionTo(run, PipelineStep.ReflectingOnRun);
                EmitOutputLine("🧠 Reflecting on run and updating brain knowledge...");
                try
                {
                    var reflectionPrompt = PromptBuilder.BuildReflectionPrompt(
                        run, _activeIssue?.Title, run.RepositoryName?.Split('/').LastOrDefault());
                    _logger.Debug("Pipeline {RunId} reflection prompt:\n{Prompt}", run.RunId, reflectionPrompt);

                    await _activeAgentProvider!.ExecuteAsync(
                        new AgentRequest
                        {
                            Prompt = reflectionPrompt,
                            WorkspacePath = run.WorkspacePath!,
                            Timeout = _activeConfig.AgentTimeout,
                            UseResume = true
                        },
                        ct,
                        line =>
                        {
                            run.OutputLines.Enqueue(line);
                            OnOutputLine?.Invoke(line);
                        });

                    _logger.Information("Pipeline {RunId} reflection step completed", run.RunId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex, "Pipeline {RunId} reflection step failed, continuing with brain sync", run.RunId);
                }

                TransitionTo(run, PipelineStep.SyncingBrainRepoPostRun);
                try { await _brainSync.SyncPostRunAsync(run, _activeBrainProvider, ct, line => EmitOutputLine(line), _activeConfig!.BrainPushMaxRetries); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { _logger.Warning(ex, "Pipeline {RunId} brain post-run sync failed", run.RunId); run.BrainUpdatesPushed = false; }
            }

            TransitionTo(run, finalStep);
            AddRunToHistory(run);

            // TODO: [UX-16] duration.TotalMinutes:F0 rounds instead of truncating — use (int)duration.TotalMinutes
            var duration = run.CompletedAt!.Value - run.StartedAt;
            if (finalStep == PipelineStep.Completed)
                EmitOutputLine($"✅ Pipeline completed in {duration.TotalMinutes:F0}m {duration.Seconds}s");
            else
                EmitOutputLine($"❌ Pipeline failed: {run.FailureReason}");

            if (finalStep == PipelineStep.Completed && _activeConfig!.CleanupSuccessfulWorkspaces)
                _historyService.TryDeleteWorkspace(run.WorkspacePath, run.RunId, _activeConfig.WorkspaceBaseDirectory);
            _logger.Information("Pipeline {RunId} {Outcome} in {Duration}. Retries: {RetryCount}. PR: {PullRequestUrl}",
                run.RunId, finalStep, run.CompletedAt!.Value - run.StartedAt, run.RetryCount, prUrl);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { _logger.Error(ex, "Pipeline {RunId} failed to create pull request", run.RunId); await FailRunAsync(run, $"PR creation failed: {ex.Message}"); }
    }
    private async Task DisposePreviousProvidersAsync()
    {
        await DisposeProviderAsync(_activeAgentProvider, "Agent");
        await DisposeProviderAsync(_activeIssueProvider, "Issue");
        await DisposeProviderAsync(_activeRepoProvider, "Repository");
        await DisposeProviderAsync(_activeBrainProvider, "Brain");
        await DisposeProviderAsync(_activePipelineProvider, "Pipeline");
    }
    private async Task DisposeProviderAsync(IAsyncDisposable? provider, string providerKind)
    {
        if (provider is null) return;
        try { await provider.DisposeAsync(); }
        catch (Exception ex) { _logger.Warning(ex, "Failed to dispose previous {ProviderKind} provider", providerKind); }
    }
    private async Task ValidateProvidersAsync(
        IRepositoryProvider repoProvider, ProviderConfig repoConfig,
        IAgentProvider agentProvider, ProviderConfig agentConfig,
        IPipelineProvider? pipelineProvider, CancellationToken ct)
    {
        try { await repoProvider.ValidateAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new InvalidOperationException($"Repository provider ({repoConfig.ProviderType}) validation failed: {ex.Message}", ex); }
        try { await agentProvider.ValidateAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { throw new InvalidOperationException($"Agent provider ({agentConfig.ProviderType}) validation failed: {ex.Message}", ex); }
        if (pipelineProvider != null)
        {
            try { await pipelineProvider.ValidateAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { throw new InvalidOperationException($"Pipeline provider validation failed: {ex.Message}", ex); }
        }
    }
    private async Task PersistLastUsedProviderIdsAsync(
        string issueId, string repoId, string agentId,
        string? brainId, string? pipelineId, CancellationToken ct)
    {
        try
        {
            var lastUsed = _activeConfig!.LastUsedProviderIds.ToDictionary(kv => kv.Key, kv => kv.Value);
            lastUsed["issue"] = issueId;
            lastUsed["repository"] = repoId;
            lastUsed["agent"] = agentId;
            if (!string.IsNullOrEmpty(brainId)) lastUsed["brain"] = brainId;
            if (!string.IsNullOrEmpty(pipelineId)) lastUsed["pipeline"] = pipelineId;
            await _configStore.UpdatePipelineConfigAsync(
                current => current with { LastUsedProviderIds = lastUsed }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { _logger.Warning(ex, "Pipeline {RunId} failed to persist last-used provider IDs", ActiveRun?.RunId); }
    }
    private Task UpdateFileChangeStatsAsync(PipelineRun run)
        => _prOrchestrator.UpdateFileChangeStatsAsync(run, _activeRepoProvider!);
    private void TransitionTo(PipelineRun run, PipelineStep step)
    {
        var previousStep = run.CurrentStep;
        run.CurrentStep = step;
        if (step is not (PipelineStep.Failed or PipelineStep.Cancelled)
            && (int)step > (int)run.HighWaterMark)
            run.HighWaterMark = step;
        _logger.Information("Pipeline {RunId} transitioned from {PreviousStep} to {Step}", run.RunId, previousStep, step);
        NotifyChange();
    }
    private async Task SwapAgentLabelAsync(string issueId, string newLabel, CancellationToken ct)
    {
        try
        {
            foreach (var label in AgentLabels.All)
                await _activeIssueProvider!.RemoveLabelAsync(issueId, label, ct);
            await _activeIssueProvider!.AddLabelAsync(issueId, newLabel, ct);
        }
        catch (Exception ex) { _logger.Warning(ex, "Failed to swap agent label to {Label} on issue {Issue}", newLabel, issueId); }
    }
    private async Task RemoveAllAgentLabelsAsync(string issueId, CancellationToken ct)
    {
        try
        {
            foreach (var label in AgentLabels.All)
                await _activeIssueProvider!.RemoveLabelAsync(issueId, label, ct);
        }
        catch (Exception ex) { _logger.Warning(ex, "Failed to remove agent labels from issue {Issue}", issueId); }
    }
    internal void NotifyChange()
    {
        try { OnChange?.Invoke(); }
        catch (Exception ex) { _logger.Warning(ex, "OnChange handler threw an exception"); }
    }

    /// <summary>Fired when chat response lines are received from an agent.</summary>
    public event Action<string, IReadOnlyList<string>>? OnChatResponse;

    /// <summary>Fired when a chat session completes on an agent.</summary>
    public event Action<string, int, string?>? OnChatCompleted;

    /// <summary>
    /// Notifies subscribers that chat response lines were received for a session.
    /// </summary>
    internal void NotifyChatResponse(string sessionId, IReadOnlyList<string> lines)
    {
        try { OnChatResponse?.Invoke(sessionId, lines); }
        catch (Exception ex) { _logger.Warning(ex, "OnChatResponse handler threw an exception"); }
    }

    /// <summary>
    /// Notifies subscribers that a chat session has completed.
    /// </summary>
    internal void NotifyChatCompleted(string sessionId, int exitCode, string? error)
    {
        try { OnChatCompleted?.Invoke(sessionId, exitCode, error); }
        catch (Exception ex) { _logger.Warning(ex, "OnChatCompleted handler threw an exception"); }
    }
    private async Task<ProviderConfig> ResolveProviderConfigAsync(string providerId, ProviderKind kind, CancellationToken ct)
    {
        var configs = await _configStore.LoadProviderConfigsAsync(kind, ct);
        return configs.FirstOrDefault(c => c.Id == providerId)
            ?? throw new InvalidOperationException($"Provider config '{providerId}' of kind '{kind}' not found.");
    }
    private async Task HandlePipelineErrorAsync(PipelineRun run, Exception ex)
    {
        using var _ = LogContext.PushProperty("PipelineRunId", run.RunId);
        _logger.Error(ex, "Pipeline {RunId} encountered an unhandled error at step {Step}", run.RunId, run.CurrentStep);
        await FailRunAsync(run, ex.Message);
    }
    private void AddRunToHistory(PipelineRun run) => _historyService.AddRunToHistory(run);
    private async Task FailRunAsync(PipelineRun run, string reason, CancellationToken ct = default)
    {
        run.FailureReason = reason;
        run.CompletedAt = DateTime.UtcNow;
        await SwapAgentLabelAsync(run.IssueIdentifier, AgentLabels.Error, ct);
        EmitOutputLine($"❌ Pipeline failed: {reason}");
        TransitionTo(run, PipelineStep.Failed);
        AddRunToHistory(run);
    }
    public async ValueTask DisposeAsync()
    {
        _cancellationTokenSource?.Dispose();
        _startLock.Dispose();
        await DisposePreviousProvidersAsync();
        GC.SuppressFinalize(this);
    }
    public void Dispose()
    {
        _cancellationTokenSource?.Dispose();
        _startLock.Dispose();
        // Do not call DisposePreviousProvidersAsync synchronously — .GetAwaiter().GetResult()
        // deadlocks in Blazor Server's SynchronizationContext (review finding #13).
        // DisposeAsync() is the correct disposal path; sync Dispose handles only sync resources.
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Adapts <see cref="IIssueProvider"/> to <see cref="IAgentIssueOperations"/> for use
    /// by <see cref="AgentExecutionOrchestrator"/> and <see cref="QualityGateOrchestrator"/>.
    /// Implements the label swap logic (remove all agent labels, add new label) inline.
    /// </summary>
    private sealed class IssueProviderIssueOperations : IAgentIssueOperations
    {
        private readonly IIssueProvider _issueProvider;
        private readonly Serilog.ILogger _logger;

        public IssueProviderIssueOperations(IIssueProvider issueProvider, Serilog.ILogger logger)
        {
            _issueProvider = issueProvider;
            _logger = logger;
        }

        public Task PostCommentAsync(string issueIdentifier, string body, CancellationToken ct)
            => _issueProvider.PostCommentAsync(issueIdentifier, body, ct);

        public async Task SwapLabelAsync(string issueIdentifier, string newLabel, CancellationToken ct)
        {
            try
            {
                foreach (var label in AgentLabels.All)
                    await _issueProvider.RemoveLabelAsync(issueIdentifier, label, ct);
                await _issueProvider.AddLabelAsync(issueIdentifier, newLabel, ct);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to swap agent label to {Label} on issue {Issue}", newLabel, issueIdentifier);
            }
        }
    }
}
