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
// Provider lifecycle management (resolution, disposal, active provider tracking) is delegated
// to PipelineProviderManager, extracted per spec 017 / MAINT-09.
//
// IProviderOperationsFacade evaluation: IPipelineCallbacks already covers SwapAgentLabel,
// RemoveAllAgentLabels, and CreatePullRequest. A separate facade would add indirection
// without meaningful simplification. Revisit if pipeline steps accumulate more
// provider-operation parameters beyond what IPipelineCallbacks covers.
public class PipelineOrchestrationService : IDisposable, IAsyncDisposable
{
    private readonly PipelineRunLifecycleService _lifecycle;
    private readonly IConfigurationStore _configStore;
    private readonly IProviderFactory _providerFactory;
    private readonly IIssueProviderLabelSwapper _labelSwapper;
    private readonly IssueDescriptionParser _issueParser;
    private readonly BrainSyncOrchestrator _brainSync;
    private readonly PullRequestOrchestrator _prOrchestrator;
    private readonly IPipelineRunHistoryService _historyService;
    private readonly IAgentPhaseExecutor _agentExecution;
    private readonly IQualityGateExecutor _qualityGates;
    private readonly Interfaces.IQualityGateValidator? _qualityGateValidator;
    private readonly FeedbackService _feedbackService;
    private readonly Serilog.ILogger _logger;

    private readonly SemaphoreSlim _startLock = new(1, 1);
    protected readonly PipelineProviderManager _providerManager;
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
        PipelineRunLifecycleService? lifecycle = null,
        Interfaces.IQualityGateValidator? qualityGateValidator = null,
        IIssueProviderLabelSwapper? labelSwapper = null)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(issueParser);
        ArgumentNullException.ThrowIfNull(agentExecution);
        ArgumentNullException.ThrowIfNull(qualityGates);
        ArgumentNullException.ThrowIfNull(logger);

        _configStore = configStore;
        _providerFactory = providerFactory;
        _labelSwapper = labelSwapper ?? NoOpLabelSwapper.Instance;
        _issueParser = issueParser;
        _logger = logger;
        _agentExecution = agentExecution;
        _qualityGates = qualityGates;
        _qualityGateValidator = qualityGateValidator;
        _feedbackService = new FeedbackService(logger);
        _providerManager = new PipelineProviderManager(configStore, providerFactory, logger);

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

            var issueProviderConfig = await _providerManager.ResolveProviderConfigAsync(issueProviderId, ProviderKind.Issue, linkedCt);
            var repoProviderConfig = await _providerManager.ResolveProviderConfigAsync(repoProviderId, ProviderKind.Repository, linkedCt);
            var agentProviderConfig = await _providerManager.ResolveProviderConfigAsync(agentProviderId, ProviderKind.Agent, linkedCt);

            // TODO: Override BrainReadOnly from the matching PipelineJobTemplate here (same as AgentJobDispatcher does).
            // Currently the per-template BrainReadOnly setting is only applied in the dispatched-job path,
            // not in this local execution path. See review finding #2.

            // Override blacklist settings from repo provider config (per-repo takes precedence)
            _activeConfig = PipelineConfiguration.ApplyBlacklistOverride(_activeConfig, repoProviderConfig);

            await _providerManager.CreateCoreProvidersAsync(issueProviderConfig, repoProviderConfig, agentProviderConfig, linkedCt);
            var issueProvider = _providerManager.ActiveIssueProvider!;
            if (!string.IsNullOrEmpty(brainProviderId))
                await _providerManager.CreateBrainProviderAsync(brainProviderId, linkedCt);
            var configuredModel = agentProviderConfig.Settings.GetValueOrDefault(ProviderSettingKeys.Model, "auto");
            var run = new PipelineRun
            {
                RunId = Guid.NewGuid().ToString(),
                IssueIdentifier = issueIdentifier,
                IssueTitle = string.Empty,
                IssueProviderConfigId = issueProviderId,
                RepoProviderConfigId = repoProviderId,
                StartedAt = DateTime.UtcNow,
                CurrentStep = PipelineStep.Created,
                RepositoryName = _providerManager.ActiveRepoProvider!.RepositoryFullName,
                ModelName = configuredModel,
                BrainProviderConfigId = _providerManager.ActiveBrainProvider != null ? brainProviderId : null,
                InitiatedBy = initiatedBy,
                AgentProviderConfigId = agentProviderId
            };
            _lifecycle.ActiveRun = run;
            _logger.Information("Pipeline {RunId} using model {Model}", run.RunId, configuredModel);
            var pipelineConfigId = await _providerManager.CreatePipelineProviderAsync(pipelineProviderId, linkedCt);
            if (pipelineConfigId is not null)
            {
                run.PipelineProviderConfigId = pipelineConfigId;
                _logger.Information("Pipeline {RunId} external CI provider configured", run.RunId);
            }
            await _providerManager.ValidateProvidersAsync(repoProviderConfig, agentProviderConfig, linkedCt);
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
        var repoProviderConfig = await _providerManager.ResolveProviderConfigAsync(repoProviderId, ProviderKind.Repository, ct);
        await using var tempRepoProvider = _providerFactory.CreateRepositoryProvider(repoProviderConfig);
        var agentProviderConfig = await _providerManager.ResolveProviderConfigAsync(agentProviderId, ProviderKind.Agent, ct);
        var configuredModel = agentProviderConfig.Settings.GetValueOrDefault(ProviderSettingKeys.Model, "auto");

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
            AgentId = agentId,
            AgentProviderConfigId = agentProviderId
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
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("ExecutePipeline");
        activity?.SetTag("pipeline.run_id", run.RunId);
        activity?.SetTag("pipeline.issue", run.IssueIdentifier);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        PipelineTelemetry.JobsDispatched.Add(1);

        IAgentIssueOperations issueOps = new IssueProviderIssueOperations(issueProvider, _logger);
        Steps.PipelineStepContext? ctx = null;

        var callbacks = new OrchestratorCallbacks(this, run, () => ctx);
        ctx = new Steps.PipelineStepContext
        {
            Run = run, Config = _activeConfig!, RepoProvider = _providerManager.ActiveRepoProvider!,
            AgentProvider = _providerManager.ActiveAgentProvider!, BrainProvider = _providerManager.ActiveBrainProvider,
            PipelineProvider = _providerManager.ActivePipelineProvider, Cts = _lifecycle.CancellationTokenSource,
            ConfigStore = _configStore, IssueProvider = issueProvider,
            Callbacks = callbacks,
            IssueOps = issueOps, AgentExecution = _agentExecution,
            QualityGates = _qualityGates, BrainSync = _brainSync,
            PrOrchestrator = _prOrchestrator, Logger = _logger,
            QualityGateValidator = _qualityGateValidator
        };

        try
        {
            await Steps.PipelineStepRunner.ExecuteAsync(BuildStepPipeline(), ctx, ct);

            sw.Stop();
            PipelineTelemetry.JobDuration.Record(sw.Elapsed.TotalSeconds);
            if (run.CurrentStep == PipelineStep.Completed)
                PipelineTelemetry.JobsCompleted.Add(1);
            else
                PipelineTelemetry.JobsFailed.Add(1);

            activity?.SetTag("pipeline.final_step", run.CurrentStep.ToString());
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            PipelineTelemetry.JobDuration.Record(sw.Elapsed.TotalSeconds);
            PipelineTelemetry.JobsFailed.Add(1);
            activity?.SetTag("pipeline.final_step", "Cancelled");

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
            new Steps.VerifyBaselineStep(),
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
        if (_providerManager.ActiveIssueProvider != null)
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
            await _labelSwapper.SwapLabelAsync(
                run.IssueProviderConfigId, run.IssueIdentifier, AgentLabels.Cancelled, CancellationToken.None);
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
        activity?.SetTag("pipeline.issue", run.IssueIdentifier);
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
                run, report, isDraft, _providerManager.ActiveRepoProvider!, _activeIssue,
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

            if (!isDraft && _providerManager.ActiveBrainProvider != null && !_activeConfig!.BrainReadOnly)
            {
                // Reflection step: ask the agent to review the entire run and enrich .brain/ knowledge
                _lifecycle.TransitionTo(run, PipelineStep.ReflectingOnRun);
                _lifecycle.EmitOutputLine("🧠 Reflecting on run and updating brain knowledge...");
                try
                {
                    var reflectionPrompt = PromptBuilder.BuildReflectionPrompt(
                        run, _activeIssue?.Title, run.RepositoryName?.Split('/').LastOrDefault());
                    _logger.Debug("Pipeline {RunId} reflection prompt:\n{Prompt}", run.RunId, reflectionPrompt);

                    var reflectionResult = await _providerManager.ActiveAgentProvider!.ExecuteAsync(
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

                    run.AccumulateTokenUsage(reflectionResult);
                    _logger.Information("Pipeline {RunId} reflection step completed", run.RunId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex, "Pipeline {RunId} reflection step failed, continuing with brain sync", run.RunId);
                }

                _lifecycle.TransitionTo(run, PipelineStep.SyncingBrainRepoPostRun);
                try { await _brainSync.SyncPostRunAsync(run, _providerManager.ActiveBrainProvider, ct, line => _lifecycle.EmitOutputLine(line), _activeConfig!.BrainPushMaxRetries); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { _logger.Warning(ex, "Pipeline {RunId} brain post-run sync failed", run.RunId); run.BrainUpdatesPushed = false; }
            }

            // Feedback collection: separate agent call, runs regardless of brain provider
            if (!isDraft)
            {
                _lifecycle.EmitOutputLine("📋 Collecting run feedback...");
                try
                {
                    var elapsed = DateTime.UtcNow - run.StartedAt;
                    var recentSummaries = _historyService.GetRunHistory()
                        .OrderByDescending(s => s.StartedAt)
                        .Take(FeedbackConstraints.MaxRecentRunsForCategories)
                        .ToList();

                    var harnessCategories = recentSummaries
                        .Where(s => s.Feedback?.Harness.Category is not null)
                        .Select(s => s.Feedback!.Harness.Category!)
                        .Distinct()
                        .ToList();

                    var issueCategories = recentSummaries
                        .Where(s => s.Feedback?.Issue?.Category is not null)
                        .Select(s => s.Feedback!.Issue!.Category!)
                        .Distinct()
                        .ToList();

                    var feedbackPrompt = FeedbackPromptBuilder.BuildStandaloneFeedbackPrompt(
                        run, elapsed, harnessCategories, issueCategories);

                    var feedbackResult = await _providerManager.ActiveAgentProvider!.ExecuteAsync(
                        new AgentRequest
                        {
                            Prompt = feedbackPrompt,
                            WorkspacePath = run.WorkspacePath!,
                            Timeout = TimeSpan.FromSeconds(FeedbackConstraints.FailureFeedbackTimeoutSeconds),
                            UseResume = true
                        },
                        ct,
                        line =>
                        {
                            run.OutputLines.Enqueue(line);
                            _lifecycle.EmitOutputLine(line);
                        });

                    var responseText = string.Join("\n", feedbackResult.OutputLines);
                    run.Feedback = _feedbackService.ParseFeedbackFromResponse(responseText, FeedbackOutcome.Success, DateTime.UtcNow);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex, "Pipeline {RunId} feedback collection failed, using fallback", run.RunId);
                    run.Feedback = _feedbackService.CreateFallbackFeedback(FeedbackOutcome.Success,
                        $"Feedback collection failed: {ex.Message}", DateTime.UtcNow);
                }
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
    private Task UpdateFileChangeStatsAsync(PipelineRun run)
        => _prOrchestrator.UpdateFileChangeStatsAsync(run, _providerManager.ActiveRepoProvider!);
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
    private async Task SwapAgentLabelAsync(string issueId, string newLabel, CancellationToken ct)
    {
        try
        {
            foreach (var label in AgentLabels.All)
                await _providerManager.ActiveIssueProvider!.RemoveLabelAsync(issueId, label, ct);
            await _providerManager.ActiveIssueProvider!.AddLabelAsync(issueId, newLabel, ct);
        }
        catch (Exception ex) { _logger.Warning(ex, "Failed to swap agent label to {Label} on issue {Issue}", newLabel, issueId); }
    }
    internal async Task RemoveAllAgentLabelsAsync(string issueId, CancellationToken ct)
    {
        try
        {
            foreach (var label in AgentLabels.All)
                await _providerManager.ActiveIssueProvider!.RemoveLabelAsync(issueId, label, ct);
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
        await _providerManager.DisposeAsync();
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
        public Task ReportBrainSyncResult(bool contextLoaded, int knowledgeFileCount)
        {
            run.BrainContextLoaded = contextLoaded;
            run.BrainKnowledgeFileCount = knowledgeFileCount;
            svc._lifecycle.NotifyChange();
            return Task.CompletedTask;
        }
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

    private sealed class NoOpLabelSwapper : IIssueProviderLabelSwapper
    {
        internal static readonly NoOpLabelSwapper Instance = new();
        public Task SwapLabelAsync(string issueProviderConfigId, string issueIdentifier, string newLabel, CancellationToken ct) => Task.CompletedTask;
    }
}
