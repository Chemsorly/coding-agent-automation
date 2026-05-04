using System.Diagnostics;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using Serilog.Context;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Singleton service that coordinates the entire automated development pipeline.
/// Manages provider resolution, execution orchestration, label swaps, and PR creation.
/// Delegates run state, lifecycle transitions, events, and cancellation to <see cref="PipelineRunLifecycleService"/>.
/// Supports both local execution (single <see cref="ActiveRun"/>) and multi-agent
/// dispatch (concurrent runs tracked via <see cref="IOrchestratorRunService"/>).
/// </summary>
// TODO: Target ~400 lines post-lifecycle extraction (spec 017). Currently ~678 lines.
// If this service exceeds 500 lines after lifecycle extraction is complete, evaluate
// extracting provider lifecycle management (resolution, disposal, active provider tracking)
// into a dedicated PipelineProviderManager service. See spec 017 for the extraction pattern.
//
// TODO: Evaluate IProviderOperationsFacade — a facade grouping SwapAgentLabel,
// RemoveAllAgentLabels, and PostComment could reduce parameter passing in pipeline steps.
// However, IPipelineCallbacks already partially serves this role (it exposes SwapAgentLabel,
// RemoveAllAgentLabels, and CreatePullRequest). A separate facade may introduce unnecessary
// indirection without meaningful simplification. Revisit if pipeline steps accumulate more
// provider-operation parameters beyond what IPipelineCallbacks covers.
public class PipelineOrchestrationService : IDisposable, IAsyncDisposable
{
    private readonly PipelineRunLifecycleService _lifecycle;
    private readonly IConfigurationStore _configStore;
    private readonly IProviderFactory _providerFactory;
    private readonly IssueDescriptionParser _issueParser;
    private readonly BrainSyncOrchestrator _brainSync;
    private readonly PullRequestOrchestrator _prOrchestrator;
    private readonly IPipelineRunHistoryService _historyService;
    private readonly IAgentPhaseExecutor _agentExecution;
    private readonly IQualityGateExecutor _qualityGates;
    private readonly Serilog.ILogger _logger;

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

    // ── Delegating properties (backward compatibility) ───────────────────

    /// <summary>Fired after each state transition for UI binding. Delegates to lifecycle service.</summary>
    public event Action? OnChange
    {
        add => _lifecycle.OnChange += value;
        remove => _lifecycle.OnChange -= value;
    }

    /// <summary>Fired for each agent output line for real-time display. Delegates to lifecycle service.</summary>
    public event Action<string>? OnOutputLine
    {
        add => _lifecycle.OnOutputLine += value;
        remove => _lifecycle.OnOutputLine -= value;
    }

    /// <summary>Fired when chat response lines are received from an agent. Delegates to lifecycle service.</summary>
    public event Action<string, IReadOnlyList<string>>? OnChatResponse
    {
        add => _lifecycle.OnChatResponse += value;
        remove => _lifecycle.OnChatResponse -= value;
    }

    /// <summary>Fired when a chat session completes on an agent. Delegates to lifecycle service.</summary>
    public event Action<string, int, string?>? OnChatCompleted
    {
        add => _lifecycle.OnChatCompleted += value;
        remove => _lifecycle.OnChatCompleted -= value;
    }

    /// <summary>The currently active local pipeline run, or null if idle. Delegates to lifecycle service.</summary>
    public PipelineRun? ActiveRun
    {
        get => _lifecycle.ActiveRun;
        protected set => _lifecycle.ActiveRun = value;
    }

    /// <summary>Whether a local pipeline run is currently in progress. Delegates to lifecycle service.</summary>
    public bool IsRunning => _lifecycle.IsRunning;

    /// <summary>Whether any pipeline run is active (local or agent). Delegates to lifecycle service.</summary>
    public bool HasAnyActiveRuns => _lifecycle.HasAnyActiveRuns;

    /// <summary>
    /// Returns all active runs — both the local run (if any) and all agent-dispatched runs.
    /// Delegates to lifecycle service.
    /// </summary>
    public IReadOnlyList<PipelineRun> GetAllActiveRuns() => _lifecycle.GetAllActiveRuns();

    /// <summary>
    /// Checks whether the given issue is being processed by any active run (local or agent).
    /// Delegates to lifecycle service.
    /// </summary>
    public bool IsIssueBeingProcessed(string issueIdentifier) => _lifecycle.IsIssueBeingProcessed(issueIdentifier);

    public PipelineOrchestrationService(
        IConfigurationStore configStore,
        IProviderFactory providerFactory,
        IssueDescriptionParser issueParser,
        IAgentPhaseExecutor agentExecution,
        IQualityGateExecutor qualityGates,
        Serilog.ILogger logger,
        IBrainUpdateService? brainUpdateService = null,
        IPipelineRunHistoryService? historyService = null,
        IOrchestratorRunService? runService = null,
        PipelineRunLifecycleService? lifecycle = null)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(issueParser);
        ArgumentNullException.ThrowIfNull(agentExecution);
        ArgumentNullException.ThrowIfNull(qualityGates);
        ArgumentNullException.ThrowIfNull(logger);

        _configStore = configStore;
        _providerFactory = providerFactory;
        _issueParser = issueParser;
        _logger = logger;
        _agentExecution = agentExecution;
        _qualityGates = qualityGates;

        ArgumentNullException.ThrowIfNull(brainUpdateService);
        _brainSync = new BrainSyncOrchestrator(brainUpdateService, logger);
        _prOrchestrator = new PullRequestOrchestrator(logger);
        _historyService = historyService ?? throw new ArgumentNullException(nameof(historyService));

        // Use provided lifecycle service or create one internally for backward compatibility
        _lifecycle = lifecycle ?? new PipelineRunLifecycleService(
            _historyService, runService, logger);
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

            _lifecycle.CreateLinkedCancellationToken(ct);
        }
        finally
        {
            _startLock.Release();
        }

        var linkedCt = _lifecycle.CancellationTokenSource!.Token;

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
            _lifecycle.ActiveRun = run;
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
            _lifecycle.NotifyChange();
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

        if (_lifecycle.IsIssueBeingProcessed(issueIdentifier))
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

        // Delegate registration to lifecycle service
        if (!_lifecycle.RegisterDispatchedRun(run))
            return null;

        _logger.Information(
            "Dispatched run {RunId} created for issue {IssueIdentifier} → agent {AgentId}",
            run.RunId, issueIdentifier, agentId);

        return run;
    }

    private async Task ExecutePipelineStepsAsync(
        PipelineRun run, IIssueProvider issueProvider, CancellationToken ct)
    {
        using var _ = LogContext.PushProperty("PipelineRunId", run.RunId);
        using var executePipelineActivity = PipelineTelemetry.ActivitySource.StartActivity("ExecutePipeline");
        executePipelineActivity?.SetTag("pipeline.run_id", run.RunId);
        executePipelineActivity?.SetTag("pipeline.issue", run.IssueIdentifier);
        var pipelineStopwatch = Stopwatch.StartNew();

        IAgentIssueOperations issueOps = new IssueProviderIssueOperations(issueProvider, _logger);
        Steps.PipelineStepContext? ctx = null;

        var callbacks = new OrchestratorCallbacks(this, run, () => ctx);
        ctx = new Steps.PipelineStepContext
        {
            Run = run, Config = _activeConfig!, RepoProvider = _activeRepoProvider!,
            AgentProvider = _activeAgentProvider!, BrainProvider = _activeBrainProvider,
            PipelineProvider = _activePipelineProvider, Cts = _lifecycle.CancellationTokenSource,
            ConfigStore = _configStore, IssueProvider = issueProvider,
            Callbacks = callbacks,
            IssueOps = issueOps, AgentExecution = _agentExecution,
            QualityGates = _qualityGates, BrainSync = _brainSync,
            PrOrchestrator = _prOrchestrator, Logger = _logger
        };

        try
        {
            await Steps.PipelineStepRunner.ExecuteAsync(BuildStepPipeline(), ctx, ct);

            pipelineStopwatch.Stop();
            PipelineTelemetry.JobDuration.Record(pipelineStopwatch.Elapsed.TotalSeconds);
            if (run.CurrentStep == PipelineStep.Completed)
                PipelineTelemetry.JobsCompleted.Add(1);
            else if (run.CurrentStep == PipelineStep.Failed)
                PipelineTelemetry.JobsFailed.Add(1);
        }
        catch (OperationCanceledException)
        {
            if (run.CurrentStep is not (PipelineStep.Cancelled or PipelineStep.Failed))
            {
                _logger.Information("Pipeline {RunId} was cancelled", run.RunId);
                run.CompletedAt = DateTime.UtcNow;
                await SwapAgentLabelAsync(run.IssueIdentifier, AgentLabels.Cancelled, CancellationToken.None);
                _lifecycle.EmitOutputLine("🚫 Pipeline cancelled");
                _lifecycle.TransitionTo(run, PipelineStep.Cancelled);
                _lifecycle.AddRunToHistory(run);
            }
        }
    }

    /// <summary>
    /// Builds the ordered list of pipeline steps. Step ordering is explicit and configurable.
    /// </summary>
    private IReadOnlyList<Steps.IPipelineStep> BuildStepPipeline()
    {
        return new Steps.IPipelineStep[]
        {
            new Steps.FetchIssueStep(_issueParser),
            new Steps.CloneRepositoryStep(),
            new Steps.SyncBrainPreRunStep(),
            new Steps.DetectReworkStep(),
            new Steps.CreateBranchStep(),
            new Steps.AnalyzeCodeStep(),
            new Steps.GenerateCodeStep(),
            new Steps.BrainPullBeforeWriteStep(),
            new Steps.ReviewCodeStep(),
            new Steps.RunQualityGatesStep()
        };
    }

    /// <summary>Cancels the active pipeline run. Delegates state transitions to lifecycle service.</summary>
    public async Task CancelPipelineAsync()
    {
        if (ActiveRun == null || !IsRunning) return;
        var run = ActiveRun;
        using var _ = LogContext.PushProperty("PipelineRunId", run.RunId);

        // Label swap requires the active issue provider (orchestration concern)
        if (_activeIssueProvider != null)
            await SwapAgentLabelAsync(run.IssueIdentifier, AgentLabels.Cancelled, CancellationToken.None);

        // Delegate state transitions to lifecycle
        await _lifecycle.CancelPipelineAsync();
    }

    /// <summary>
    /// Best-effort label swap to <see cref="AgentLabels.Cancelled"/> for all agent-dispatched active runs.
    /// Called during graceful shutdown to mark interrupted runs.
    /// Delegates state changes to lifecycle service, retains label swap logic.
    /// </summary>
    public async Task CancelActiveAgentRunsAsync()
    {
        // Label swap logic (requires provider resolution)
        var allRuns = _lifecycle.GetAllActiveRuns()
            .Where(r => r.AgentId != null)
            .ToList();

        if (allRuns.Count == 0) return;

        foreach (var run in allRuns)
        {
            try
            {
                var issueConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
                var issueConfig = issueConfigs.FirstOrDefault(c => c.Id == run.IssueProviderConfigId);
                if (issueConfig is null) continue;

                await using var issueProvider = _providerFactory.CreateIssueProvider(issueConfig);
                foreach (var label in AgentLabels.All)
                    await issueProvider.RemoveLabelAsync(run.IssueIdentifier, label, CancellationToken.None);
                await issueProvider.AddLabelAsync(run.IssueIdentifier, AgentLabels.Cancelled, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Best-effort label swap to agent:cancelled failed for run {RunId} on issue {Issue}",
                    run.RunId, run.IssueIdentifier);
            }
        }

        // Delegate state changes to lifecycle
        await _lifecycle.MarkAgentRunsCancelled();
    }

    /// <summary>Returns the in-memory run history.</summary>
    public IReadOnlyList<PipelineRunSummary> GetRunHistory() => _historyService.GetRunHistory();

    // --- Private helpers ---
    private async Task CreatePullRequestAsync(
        PipelineRun run, QualityGateReport report, bool isDraft, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("CreatePullRequest");
        activity?.SetTag("pipeline.run_id", run.RunId);
        activity?.SetTag("pipeline.pr.is_draft", isDraft);

        _lifecycle.TransitionTo(run, PipelineStep.CreatingPullRequest);
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
                _activeIssueComments, _activeConfig!, ct, line => _lifecycle.EmitOutputLine(line),
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
                _lifecycle.TransitionTo(run, PipelineStep.ReflectingOnRun);
                _lifecycle.EmitOutputLine("🧠 Reflecting on run and updating brain knowledge...");
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
                            _lifecycle.EmitOutputLine(line);
                        });

                    _logger.Information("Pipeline {RunId} reflection step completed", run.RunId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex, "Pipeline {RunId} reflection step failed, continuing with brain sync", run.RunId);
                }

                _lifecycle.TransitionTo(run, PipelineStep.SyncingBrainRepoPostRun);
                try { await _brainSync.SyncPostRunAsync(run, _activeBrainProvider, ct, line => _lifecycle.EmitOutputLine(line), _activeConfig!.BrainPushMaxRetries); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { _logger.Warning(ex, "Pipeline {RunId} brain post-run sync failed", run.RunId); run.BrainUpdatesPushed = false; }
            }

            _lifecycle.TransitionTo(run, finalStep);
            _lifecycle.AddRunToHistory(run);

            var duration = run.CompletedAt!.Value - run.StartedAt;
            if (finalStep == PipelineStep.Completed)
                _lifecycle.EmitOutputLine($"✅ Pipeline completed in {(int)duration.TotalMinutes}m {duration.Seconds}s");
            else
                _lifecycle.EmitOutputLine($"❌ Pipeline failed: {run.FailureReason}");

            if (finalStep == PipelineStep.Completed)
                _historyService.TryDeleteWorkspace(run.WorkspacePath, run.RunId, _activeConfig!.WorkspaceBaseDirectory);
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
    private async Task HandlePipelineErrorAsync(PipelineRun run, Exception ex)
    {
        using var _ = LogContext.PushProperty("PipelineRunId", run.RunId);
        _logger.Error(ex, "Pipeline {RunId} encountered an unhandled error at step {Step}", run.RunId, run.CurrentStep);
        await FailRunAsync(run, ex.Message);
    }
    private async Task FailRunAsync(PipelineRun run, string reason, CancellationToken ct = default)
    {
        run.FailureReason = reason;
        run.CompletedAt = DateTime.UtcNow;
        await SwapAgentLabelAsync(run.IssueIdentifier, AgentLabels.Error, ct);
        _lifecycle.EmitOutputLine($"❌ Pipeline failed: {reason}");
        _lifecycle.TransitionTo(run, PipelineStep.Failed);
        _lifecycle.AddRunToHistory(run);
    }
    public async ValueTask DisposeAsync()
    {
        _startLock.Dispose();
        await DisposePreviousProvidersAsync();
        GC.SuppressFinalize(this);
    }
    public void Dispose()
    {
        _startLock.Dispose();
        // Do not call DisposePreviousProvidersAsync synchronously — .GetAwaiter().GetResult()
        // deadlocks in Blazor Server's SynchronizationContext (review finding #13).
        // DisposeAsync() is the correct disposal path; sync Dispose handles only sync resources.
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Adapts the orchestration service's private helper methods to <see cref="IPipelineCallbacks"/>.
    /// Delegates lifecycle operations (TransitionTo, EmitOutputLine, NotifyChange, AddRunToHistory) to the lifecycle service.
    /// Retains provider-dependent operations (SwapAgentLabel, RemoveAllAgentLabels, UpdateFileChangeStats, CreatePullRequest).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Closure Pattern:</b> This class captures a <c>Func&lt;PipelineStepContext?&gt;</c> (<paramref name="ctxAccessor"/>)
    /// rather than a direct reference to the <see cref="Steps.PipelineStepContext"/>. This is necessary because the
    /// step context is constructed <em>after</em> the callbacks instance (the callbacks are a constructor argument
    /// to the context itself). The <c>Func</c> closure resolves the chicken-and-egg problem: callbacks are created
    /// first, then the context is created referencing the callbacks, and the <c>Func</c> captures the local variable
    /// that is assigned after construction.
    /// </para>
    /// <para>
    /// Additionally, mutable properties on the step context (e.g., <c>Issue</c>, <c>ParsedIssue</c>,
    /// <c>IssueComments</c>) change during pipeline execution as steps populate them. A direct reference
    /// captured at construction time would be stale for operations like <see cref="CreatePullRequest"/>
    /// that need the latest state. The <c>Func</c> ensures the current context snapshot is always accessed.
    /// </para>
    /// </remarks>
    private sealed class OrchestratorCallbacks(
        PipelineOrchestrationService svc,
        PipelineRun run,
        Func<Steps.PipelineStepContext?> ctxAccessor) : IPipelineCallbacks
    {
        public void TransitionTo(PipelineStep step) => svc._lifecycle.TransitionTo(run, step);
        public void EmitOutputLine(string line) => svc._lifecycle.EmitOutputLine(line);
        public void NotifyChange() => svc._lifecycle.NotifyChange();
        public void AddRunToHistory(PipelineRun r) => svc._lifecycle.AddRunToHistory(r);
        public Task UpdateFileChangeStats(PipelineRun r) => svc.UpdateFileChangeStatsAsync(r);
        public Task SwapAgentLabel(string issueIdentifier, string label, CancellationToken ct)
            => svc.SwapAgentLabelAsync(issueIdentifier, label, ct);
        public Task RemoveAllAgentLabels(string issueIdentifier, CancellationToken ct)
            => svc.RemoveAllAgentLabelsAsync(issueIdentifier, ct);
        public Task CreatePullRequest(PipelineRun r, QualityGateReport report, bool isDraft, CancellationToken ct)
        {
            var ctx = ctxAccessor();
            svc._activeIssue = ctx?.Issue;
            svc._activeParsedIssue = ctx?.ParsedIssue;
            svc._activeIssueComments = ctx?.IssueComments;
            return svc.CreatePullRequestAsync(r, report, isDraft, ct);
        }
    }

    private async Task<ProviderConfig> ResolveProviderConfigAsync(string providerId, ProviderKind kind, CancellationToken ct)
    {
        var configs = await _configStore.LoadProviderConfigsAsync(kind, ct);
        return configs.FirstOrDefault(c => c.Id == providerId)
            ?? throw new InvalidOperationException($"Provider config '{providerId}' of kind '{kind}' not found.");
    }

    /// <summary>
    /// Notifies subscribers that chat response lines were received for a session.
    /// Delegates to lifecycle service.
    /// </summary>
    internal void NotifyChatResponse(string sessionId, IReadOnlyList<string> lines)
        => _lifecycle.NotifyChatResponse(sessionId, lines);

    /// <summary>
    /// Notifies subscribers that a chat session has completed.
    /// Delegates to lifecycle service.
    /// </summary>
    internal void NotifyChatCompleted(string sessionId, int exitCode, string? error)
        => _lifecycle.NotifyChatCompleted(sessionId, exitCode, error);

    /// <summary>
    /// Notifies subscribers of a state change. Delegates to lifecycle service.
    /// Called by AgentHub for agent-dispatched run state updates.
    /// </summary>
    internal void NotifyChange() => _lifecycle.NotifyChange();
}
