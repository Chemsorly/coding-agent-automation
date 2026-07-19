﻿﻿using System.Diagnostics;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using Serilog.Context;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Singleton service that coordinates the automated development pipeline.
/// Manages provider resolution, execution orchestration, label swaps, and PR creation.
/// Delegates run state, lifecycle transitions, events, and cancellation to <see cref="PipelineRunLifecycleService"/>.
/// Pipeline execution is handled by remote agents via <see cref="CreateDispatchedRunAsync"/> and
/// <c>LocalPipelineExecutor</c>. Multi-agent dispatch uses concurrent runs tracked via <see cref="IOrchestratorRunService"/>.
/// </summary>
// Provider lifecycle management (resolution, disposal, active provider tracking) is delegated
// to PipelineProviderManager, extracted per spec 017 / MAINT-09.
//
// IProviderOperationsFacade evaluation: IPipelineCallbacks already covers SwapAgentLabel,
// RemoveAllAgentLabels, and CreatePullRequest. A separate facade would add indirection
// without meaningful simplification. Revisit if pipeline steps accumulate more
// provider-operation parameters beyond what IPipelineCallbacks covers.
public class PipelineOrchestrationService : IDisposable, IAsyncDisposable, IOrchestrationShutdownAction, IDispatchRunCreator, IChangeNotifier
{
    private readonly PipelineRunLifecycleService _lifecycle;
    private readonly IPipelineConfigStore _pipelineConfigStore;
    private readonly IProviderConfigStore _providerConfigStore;
    private readonly IProviderFactory _providerFactory;
    private readonly ILabelService _labelService;
#pragma warning disable CS0414 // Field assigned but never used — constructor parameter retained for backward compatibility
    // TODO: Consider removing _issueParser field entirely — it is dead code after ExecutePipelineStepsAsync was removed.
    // The constructor signature stability concern only applies to sealed/derived classes; this class is non-sealed but
    // no known subclass (except tests) depends on this parameter. Removing it would also trim IssueDescriptionParser from the DI graph.
    private readonly IssueDescriptionParser _issueParser;
#pragma warning restore CS0414
    private readonly IPipelineExecutionFacade _executionFacade;
    private readonly IPipelineCompletionFacade _completionFacade;
    private readonly IPipelineCancellationFacade _cancellationFacade;
    private readonly Serilog.ILogger _logger;

    protected readonly PipelineProviderManager _providerManager;
    protected PipelineConfiguration? _activeConfig;

    protected IssueDetail? _activeIssue;
    protected ParsedIssue? _activeParsedIssue;
    protected IReadOnlyList<IssueComment>? _activeIssueComments;

    // â”€â”€ Delegating properties (backward compatibility) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

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

    /// <summary>The currently active pipeline run (used by test infrastructure), or null if idle. Delegates to lifecycle service.</summary>
    public PipelineRun? ActiveRun
    {
        get => _lifecycle.ActiveRun;
        protected set => _lifecycle.ActiveRun = value;
    }

    /// <summary>Whether a pipeline run is currently in progress (test infrastructure only in production). Delegates to lifecycle service.</summary>
    public bool IsRunning => _lifecycle.IsRunning;

    /// <summary>Whether any pipeline run is active (in-process or agent-dispatched). Delegates to lifecycle service.</summary>
    public bool HasAnyActiveRuns => _lifecycle.HasAnyActiveRuns;

    /// <summary>
    /// Returns all active runs â€” both the in-process run (if any) and all agent-dispatched runs.
    /// Delegates to lifecycle service.
    /// </summary>
    public IReadOnlyList<PipelineRun> GetAllActiveRuns() => _lifecycle.GetAllActiveRuns();

    /// <summary>
    /// Checks whether the given issue is being processed by any active run (in-process or agent-dispatched).
    /// Delegates to lifecycle service.
    /// </summary>
    public bool IsIssueBeingProcessed(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId) => _lifecycle.IsIssueBeingProcessed(issueIdentifier, issueProviderConfigId.Value);

    public PipelineOrchestrationService(
        IPipelineConfigStore pipelineConfigStore,
        IProviderConfigStore providerConfigStore,
        IProviderFactory providerFactory,
        IssueDescriptionParser issueParser,
        IPipelineExecutionFacade executionFacade,
        IPipelineCompletionFacade completionFacade,
        IPipelineCancellationFacade cancellationFacade,
        PipelineRunLifecycleService lifecycle,
        ILabelService labelService,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(pipelineConfigStore);
        ArgumentNullException.ThrowIfNull(providerConfigStore);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(issueParser);
        ArgumentNullException.ThrowIfNull(executionFacade);
        ArgumentNullException.ThrowIfNull(completionFacade);
        ArgumentNullException.ThrowIfNull(cancellationFacade);
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(labelService);
        ArgumentNullException.ThrowIfNull(logger);

        _pipelineConfigStore = pipelineConfigStore;
        _providerConfigStore = providerConfigStore;
        _providerFactory = providerFactory;
        _labelService = labelService;
        _issueParser = issueParser;
        _logger = logger;
        _executionFacade = executionFacade;
        _completionFacade = completionFacade;
        _cancellationFacade = cancellationFacade;
        _providerManager = new PipelineProviderManager(providerConfigStore, providerFactory, logger);
        _lifecycle = lifecycle;
    }

    /// <summary>
    /// Creates a <see cref="PipelineRun"/> for dispatch to a remote agent.
    /// The run is tracked via <see cref="IOrchestratorRunService"/> (not the local <see cref="ActiveRun"/>).
    /// Does NOT execute the pipeline locally â€” the agent handles execution.
    /// </summary>
    /// <returns>The created <see cref="PipelineRun"/> ready for dispatch, or <c>null</c> if the issue is already being processed.</returns>
    public async Task<PipelineRun?> CreateDispatchedRunAsync(
        ProviderConfigId issueProviderId, ProviderConfigId repoProviderId, IssueIdentifier issueIdentifier,
        ProviderConfigId agentProviderId, string? agentId, CancellationToken ct,
        string? brainProviderId = null, string? pipelineProviderId = null,
        string initiatedBy = "dispatch",
        PipelineRunType runType = PipelineRunType.Implementation)
    {
        // TODO: Validate that ProviderConfigId.Value is not null/empty for issueProviderId,
        // repoProviderId, and agentProviderId. The previous string parameters had
        // ArgumentNullException.ThrowIfNull guards that are now lost because structs can't be null,
        // but default(ProviderConfigId) or implicit conversion from null still produces Value = null.

        if (_lifecycle.IsIssueBeingProcessed(issueIdentifier, issueProviderId.Value))
        {
            _logger.Warning("Issue {IssueIdentifier} is already being processed, skipping dispatch", issueIdentifier);
            return null;
        }

        var config = await _pipelineConfigStore.LoadPipelineConfigAsync(ct);

        // Resolve repo provider config to get repository name
        var repoProviderConfig = await _providerManager.ResolveProviderConfigAsync(repoProviderId.Value, ProviderKind.Repository, ct);
        await using var tempRepoProvider = _providerFactory.CreateRepositoryProvider(repoProviderConfig);
        var agentProviderConfig = await _providerManager.ResolveProviderConfigAsync(agentProviderId.Value, ProviderKind.Agent, ct);
        var configuredModel = agentProviderConfig.Settings.GetValueOrDefault(ProviderSettingKeys.Model, "auto");

        var run = PipelineRun.Create(
            runId: Guid.NewGuid().ToString(),
            issueIdentifier: issueIdentifier.Value,
            issueTitle: string.Empty,
            issueProviderConfigId: issueProviderId.Value,
            repoProviderConfigId: repoProviderId.Value,
            runType: runType,
            initiatedBy: initiatedBy,
            agentId: agentId,
            agentProviderConfigId: agentProviderId.Value,
            brainProviderConfigId: brainProviderId);
        run.RepositoryName = tempRepoProvider.RepositoryFullName;
        run.ModelName = configuredModel;
        run.PipelineProviderConfigId = pipelineProviderId;

        // Delegate registration to lifecycle service
        if (!_lifecycle.RegisterDispatchedRun(run))
            return null;

        _logger.Information(
            "Dispatched run {RunId} created for issue {IssueIdentifier} â†’ agent {AgentId}",
            run.RunId, issueIdentifier, agentId);

        return run;
    }

    /// <inheritdoc />
    public async Task<RunReservation?> ReserveRunIdAsync(
        ProviderConfigId issueProviderId, ProviderConfigId repoProviderId, IssueIdentifier issueIdentifier,
        ProviderConfigId agentProviderId, string? agentId, CancellationToken ct,
        string? brainProviderId = null, string? pipelineProviderId = null,
        string initiatedBy = "dispatch")
    {
        // TODO: Validate that issueIdentifier.Value is not null/empty. The previous string parameter had
        // ArgumentNullException.ThrowIfNull that is now lost because structs can't be null, but
        // default(IssueIdentifier) or implicit conversion from null still produces Value = null.
        // Same issue exists in CreateDispatchedRunAsync above.

        // TODO: TOCTOU race — concurrent calls to ReserveRunIdAsync for the same issueIdentifier can
        // both pass this check before either registers the sentinel. The inner RegisterDispatchedRun
        // in PipelineRunLifecycleService has a second dedup check providing a defense layer, but async
        // provider resolution between the two checks widens the window. Pre-existing pattern from
        // CreateDispatchedRunAsync; not a regression but worth hardening with a lock or atomic reserve.
        if (_lifecycle.IsIssueBeingProcessed(issueIdentifier, issueProviderId.Value))
        {
            _logger.Warning("Issue {IssueIdentifier} is already being processed, skipping reservation", issueIdentifier);
            return null;
        }

        // Resolve repo provider config to get repository name
        var repoProviderConfig = await _providerManager.ResolveProviderConfigAsync(repoProviderId.Value, ProviderKind.Repository, ct);
        await using var tempRepoProvider = _providerFactory.CreateRepositoryProvider(repoProviderConfig);
        var agentProviderConfig = await _providerManager.ResolveProviderConfigAsync(agentProviderId.Value, ProviderKind.Agent, ct);
        var configuredModel = agentProviderConfig.Settings.GetValueOrDefault(ProviderSettingKeys.Model, "auto");

        var runId = Guid.NewGuid().ToString();
        var startedAt = DateTimeOffset.UtcNow;

        // Create a minimal sentinel run for dedup protection
        // TODO: Sentinel RunType is hardcoded to Implementation regardless of actual dispatch type
        // (Review or Decomposition). During the brief window between ReserveRunIdAsync and
        // RegisterDispatchedRun, code that inspects RunType on active runs (e.g. DispatchScheduler
        // decomposition concurrency enforcement) will see the wrong type. Consider adding a
        // runType parameter to ReserveRunIdAsync to set the correct type on the sentinel.
        var sentinel = PipelineRun.Create(
            runId: runId,
            issueIdentifier: issueIdentifier.Value,
            issueTitle: string.Empty,
            issueProviderConfigId: issueProviderId.Value,
            repoProviderConfigId: repoProviderId.Value,
            runType: PipelineRunType.Implementation,
            initiatedBy: initiatedBy,
            agentId: agentId,
            agentProviderConfigId: agentProviderId.Value,
            brainProviderConfigId: brainProviderId,
            startedAt: startedAt);
        sentinel.RepositoryName = tempRepoProvider.RepositoryFullName;
        sentinel.ModelName = configuredModel;
        sentinel.PipelineProviderConfigId = pipelineProviderId;

        // Register sentinel — dedup is active immediately
        if (!_lifecycle.RegisterDispatchedRun(sentinel))
            return null;

        _logger.Information(
            "Reserved run {RunId} for issue {IssueIdentifier} → agent {AgentId}",
            runId, issueIdentifier, agentId);

        return new RunReservation(runId, tempRepoProvider.RepositoryFullName, configuredModel, startedAt);
    }

    /// <inheritdoc />
    public void RegisterDispatchedRun(PipelineRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        _lifecycle.ReplaceDispatchedRun(run);
        _logger.Information(
            "Registered dispatched run {RunId} for issue {IssueIdentifier}",
            run.RunId, run.IssueIdentifier);
    }

    /// <summary>Cancels the active pipeline run. Delegates state transitions to lifecycle service.</summary>
    public async Task CancelPipelineAsync()
    {
        if (ActiveRun == null || !IsRunning) return;
        var run = ActiveRun;
        using var _ = LogContext.PushProperty("PipelineRunId", run.RunId);

        // Label swap requires the active issue provider (orchestration concern)
        if (_providerManager.ActiveIssueProvider != null || run.RunType == PipelineRunType.Review)
            await SwapAgentLabelAsync(run, run.IssueIdentifier.Value, AgentLabels.Cancelled, CancellationToken.None);

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

        // Send CancelJob to each agent in parallel (2s per-agent timeout)
        // Non-fatal â€” agent may already be disconnected
        if (_cancellationFacade.AgentCancellation is not null)
        {
            try
            {
                // TODO: run.AgentId is string? — null-forgiving operator defers null detection to
                // SendCancelJobAsync's ThrowIfNullOrEmpty, which reports confusing parameter name.
                // Consider guarding with !string.IsNullOrEmpty(run.AgentId) before calling.
                var cancelTasks = allRuns.Select(run =>
                    _cancellationFacade.AgentCancellation.SendCancelJobAsync(run.AgentId!, run.RunId, CancellationToken.None));
                await Task.WhenAll(cancelTasks);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "One or more CancelJob sends failed during shutdown â€” proceeding with cleanup");
            }
        }

        foreach (var run in allRuns)
        {
            var targetKind = run.LabelTargetKind;

            await _labelService.SwapLabelAsync(
                run.ProviderConfigIdForLabel, run.IssueIdentifier.Value, AgentLabels.Cancelled, targetKind, CancellationToken.None);
        }

        // Delegate state changes to lifecycle â€” returns cancelled issue identifiers
        var cancelledIssues = await _lifecycle.MarkAgentRunsCancelled();

        // Release dedup guards so issues become re-dispatchable after restart
        if (_cancellationFacade.DedupGuard is not null)
        {
            foreach (var (issueId, providerId) in cancelledIssues)
            {
                _cancellationFacade.DedupGuard.MarkIssueComplete(issueId, providerId);
            }
        }
    }

    /// <summary>Returns the run history.</summary>
    public Task<IReadOnlyList<PipelineRunSummary>> GetRunHistoryAsync(CancellationToken ct = default)
        => _completionFacade.HistoryService.GetRunHistoryAsync(ct);

    // --- Private helpers ---
    private async Task CreatePullRequestAsync(
        PipelineRun run, QualityGateReport report, bool isDraft, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("CreatePullRequest");
        activity?.SetTag("pipeline.run_id", run.RunId);
        activity?.SetTag("pipeline.issue", run.IssueIdentifier);
        activity?.SetTag("pipeline.pr.is_draft", isDraft);
        PipelineTelemetry.SetProjectTags(activity, run.ProjectId, run.ProjectName);

        _lifecycle.TransitionTo(run, PipelineStep.CreatingPullRequest);
        try
        {
            // Set PR info from linked PR before calling the orchestrator (rework mode)
            if (run.LinkedPullRequest != null)
            {
                run.PullRequestUrl = run.LinkedPullRequest.Url;
                run.PullRequestNumber = run.LinkedPullRequest.Number.ToString();
            }

            var prUrl = await _completionFacade.PrOrchestrator.CreatePullRequestAsync(
                run, report, isDraft, _providerManager.ActiveRepoProvider!, _activeIssue,
                _activeIssueComments, _activeConfig!, ct, line => _lifecycle.EmitOutputLine(line),
                isRework: run.LinkedPullRequest != null,
                issueReference: _providerManager.ActiveIssueProvider?.FormatIssueReference(run.IssueIdentifier.Value));

            await HandlePrCreationResultAsync(run, prUrl, isDraft, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            _logger.Error(ex, "Pipeline {RunId} failed to create pull request", run.RunId);
            await FailRunAsync(run, $"PR creation failed: {ex.Message}");
        }
    }
    private Task UpdateFileChangeStatsAsync(PipelineRun run)
        => _completionFacade.PrOrchestrator.UpdateFileChangeStatsAsync(run, _providerManager.ActiveRepoProvider!);

    private async Task CreateDraftPrIfNotExistsAsync(PipelineRun run, CancellationToken ct)
    {
        try
        {
            var prUrl = await _completionFacade.PrOrchestrator.CreateDraftPrIfNotExistsAsync(
                run, _providerManager.ActiveRepoProvider!, ct,
                issueReference: _providerManager.ActiveIssueProvider?.FormatIssueReference(run.IssueIdentifier.Value));
            if (prUrl != null)
                _lifecycle.EmitOutputLine($"ðŸ“‹ Draft PR #{run.PullRequestNumber} created");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Non-fatal: CI can still run without a PR existing
            _logger.Warning(ex, "Pipeline {RunId} failed to create draft PR, continuing", run.RunId);
        }
    }

    private async Task FinalizePullRequestAsync(
        PipelineRun run, QualityGateReport report, bool isDraft, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("FinalizePullRequest");
        activity?.SetTag("pipeline.run_id", run.RunId);
        activity?.SetTag("pipeline.issue", run.IssueIdentifier);
        activity?.SetTag("pipeline.pr.is_draft", isDraft);
        PipelineTelemetry.SetProjectTags(activity, run.ProjectId, run.ProjectName);

        _lifecycle.TransitionTo(run, PipelineStep.CreatingPullRequest);
        try
        {
            // If no draft PR was created (e.g., CreateDraftPrIfNotExists failed or was skipped),
            // fall back to the original CreatePullRequest flow
            if (string.IsNullOrEmpty(run.PullRequestNumber))
            {
                _logger.Information("Pipeline {RunId} no existing PR to finalize, falling back to CreatePullRequest", run.RunId);
                await CreatePullRequestAsync(run, report, isDraft, ct);
                return;
            }

            var prUrl = await _completionFacade.PrOrchestrator.FinalizePullRequestAsync(
                run, report, isDraft, _providerManager.ActiveRepoProvider!, _activeIssue,
                _activeIssueComments, _activeConfig!, ct, line => _lifecycle.EmitOutputLine(line),
                issueReference: _providerManager.ActiveIssueProvider?.FormatIssueReference(run.IssueIdentifier.Value));

            await HandlePrCreationResultAsync(run, prUrl, isDraft, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            _logger.Error(ex, "Pipeline {RunId} failed to finalize pull request", run.RunId);
            await FailRunAsync(run, $"PR finalization failed: {ex.Message}");
        }
    }

    private async Task HandlePrCreationResultAsync(PipelineRun run, string? prUrl, bool isDraft, CancellationToken ct)
    {
        if (prUrl == null)
        { await FailRunAsync(run, "Agent did not produce any changes. No commits ahead of base branch.", ct); return; }

        await PostPullRequestCompletionAsync(run, isDraft, ct);
    }

    private async Task PostPullRequestCompletionAsync(PipelineRun run, bool isDraft, CancellationToken ct)
    {
        var finalStep = isDraft ? PipelineStep.Failed : PipelineStep.Completed;
        if (isDraft)
        {
            run.FailureReason = "Quality gates failed after max retries; draft PR created.";
            await SwapAgentLabelAsync(run, run.IssueIdentifier.Value, AgentLabels.Error, ct);
        }
        else
            await SwapAgentLabelAsync(run, run.IssueIdentifier.Value, AgentLabels.Done, ct);

        await _completionFacade.Finalization.RunPostPrSequenceAsync(
            run, isDraft, _providerManager.ActiveAgentProvider!, _providerManager.ActiveRepoProvider!,
            _activeConfig!, _executionFacade.BrainSync, _providerManager.ActiveBrainProvider, _completionFacade.FeedbackService,
            _completionFacade.HistoryService, line => _lifecycle.EmitOutputLine(line),
            step => { _lifecycle.TransitionTo(run, step); return Task.CompletedTask; },
            ct);

        _lifecycle.TransitionTo(run, finalStep);
        await _lifecycle.AddRunToHistoryAsync(run).ConfigureAwait(false);

        var duration = run.CompletedAtOffset!.Value - run.StartedAtOffset;
        if (finalStep == PipelineStep.Completed)
            _lifecycle.EmitOutputLine($"✅ Pipeline completed in {(int)duration.TotalMinutes}m {duration.Seconds}s");
        else
            _lifecycle.EmitOutputLine($"❌ Pipeline failed: {run.FailureReason}");

        if (finalStep == PipelineStep.Completed)
            _completionFacade.HistoryService.TryDeleteWorkspace(run.WorkspacePath, run.RunId, _activeConfig!.WorkspaceBaseDirectory);
        _logger.Information("Pipeline {RunId} {Outcome} in {Duration}. Retries: {RetryCount}. PR: {PullRequestUrl}",
            run.RunId, finalStep, run.CompletedAtOffset!.Value - run.StartedAtOffset, run.RetryCount, run.PullRequestUrl);
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
            await _pipelineConfigStore.UpdatePipelineConfigAsync(
                current => current with { LastUsedProviderIds = lastUsed }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { _logger.Warning(ex, "Pipeline {RunId} failed to persist last-used provider IDs", ActiveRun?.RunId); }
    }
    // TODO: Add unit test that exercises SwapAgentLabelAsync for Implementation runs during normal pipeline execution
    // (existing tests use TestPipelineRunner which bypasses ILabelService and doesn't validate this code path).
    private async Task SwapAgentLabelAsync(PipelineRun run, string issueId, string newLabel, CancellationToken ct)
    {
        _logger.Information(
            "Pipeline {RunId} SwapAgentLabelAsync: {IssueIdentifier} â†’ {Label} (runType={RunType}, step={CurrentStep})",
            run.RunId, issueId, newLabel, run.RunType, run.CurrentStep);
        try
        {
            var targetKind = run.LabelTargetKind;

            await _labelService.SwapLabelAsync(run.ProviderConfigIdForLabel, issueId, newLabel, targetKind, ct);
        }
        catch (Exception ex) { _logger.Warning(ex, "Failed to swap agent label to {Label} on {Identifier}", newLabel, issueId); }
    }

    // TODO: Add unit test that validates RemoveAllAgentLabelsAsync routes through ILabelService with string.Empty,
    // selecting the correct providerConfigId and targetKind for Implementation runs.
    internal async Task RemoveAllAgentLabelsAsync(PipelineRun run, string issueId, CancellationToken ct)
    {
        try
        {
            var targetKind = run.LabelTargetKind;

            await _labelService.SwapLabelAsync(run.ProviderConfigIdForLabel, issueId, string.Empty, targetKind, ct);
        }
        catch (Exception ex) { _logger.Warning(ex, "Failed to remove agent labels from {Identifier}", issueId); }
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
        run.MarkCompleted();
        _logger.Information(
            "Pipeline {RunId} PipelineOrchestrationService.FailRunAsync swapping label to agent:error for issue {IssueIdentifier} (reason={Reason}, step={CurrentStep})",
            run.RunId, run.IssueIdentifier, reason, run.CurrentStep);
        await SwapAgentLabelAsync(run, run.IssueIdentifier.Value, AgentLabels.Error, ct);
        _lifecycle.EmitOutputLine($"❌ Pipeline failed: {reason}");
        _lifecycle.TransitionTo(run, PipelineStep.Failed);
        await _lifecycle.AddRunToHistoryAsync(run).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        await _providerManager.DisposeAsync();
        GC.SuppressFinalize(this);
    }
    public void Dispose()
    {
        // Do not call DisposePreviousProvidersAsync synchronously â€” .GetAwaiter().GetResult()
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
    // TODO: OrchestratorCallbacks and its delegate methods (CreatePullRequestAsync, FinalizePullRequestAsync,
    // HandlePrCreationResultAsync, PostPullRequestCompletionAsync, CreateDraftPrIfNotExistsAsync, UpdateFileChangeStatsAsync,
    // HandlePipelineErrorAsync, FailRunAsync) appear to be dead code after ExecutePipelineStepsAsync was removed.
    // Evaluate whether they can be safely deleted or if they are still referenced via other paths.
    private sealed class OrchestratorCallbacks(
        PipelineOrchestrationService svc,
        PipelineRun run,
        Func<Steps.PipelineStepContext?> ctxAccessor) : PipelineCallbacksBase
    {
        protected override PipelineRun Run => run;
        public override void TransitionTo(PipelineStep step) => svc._lifecycle.TransitionTo(run, step);
        public override void EmitOutputLine(string line) => svc._lifecycle.EmitOutputLine(line);
        public override void NotifyChange() => svc._lifecycle.NotifyChange();
        public override Task AddRunToHistoryAsync(PipelineRun r) => svc._lifecycle.AddRunToHistoryAsync(r);
        public override Task UpdateFileChangeStats(PipelineRun r) => svc.UpdateFileChangeStatsAsync(r);
        public override Task SwapAgentLabel(string issueIdentifier, string label, CancellationToken ct)
            => svc.SwapAgentLabelAsync(run, issueIdentifier, label, ct);
        public override Task RemoveAllAgentLabels(string issueIdentifier, CancellationToken ct)
            => svc.RemoveAllAgentLabelsAsync(run, issueIdentifier, ct);
        public override Task CreatePullRequest(PipelineRun r, QualityGateReport report, bool isDraft, CancellationToken ct)
        {
            SyncActiveIssueContext();
            return svc.CreatePullRequestAsync(r, report, isDraft, ct);
        }
        public override Task CreateDraftPrIfNotExists(PipelineRun r, CancellationToken ct)
            => svc.CreateDraftPrIfNotExistsAsync(r, ct);
        public override Task FinalizePullRequest(PipelineRun r, QualityGateReport report, bool isDraft, CancellationToken ct)
        {
            SyncActiveIssueContext();
            return svc.FinalizePullRequestAsync(r, report, isDraft, ct);
        }
        private void SyncActiveIssueContext()
        {
            var ctx = ctxAccessor();
            svc._activeIssue = ctx?.Issue;
            svc._activeParsedIssue = ctx?.ParsedIssue;
            svc._activeIssueComments = ctx?.IssueComments;
        }
        public override Task ReportBrainSyncResult(bool contextLoaded, int knowledgeFileCount)
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
    public void NotifyChatResponse(string sessionId, IReadOnlyList<string> lines)
        => _lifecycle.NotifyChatResponse(sessionId, lines);

    /// <summary>
    /// Notifies subscribers that a chat session has completed.
    /// Delegates to lifecycle service.
    /// </summary>
    public void NotifyChatCompleted(string sessionId, int exitCode, string? error)
        => _lifecycle.NotifyChatCompleted(sessionId, exitCode, error);

    /// <summary>
    /// Notifies subscribers of a state change. Delegates to lifecycle service.
    /// Called by AgentHub for agent-dispatched run state updates.
    /// </summary>
    public void NotifyChange() => _lifecycle.NotifyChange();

}
