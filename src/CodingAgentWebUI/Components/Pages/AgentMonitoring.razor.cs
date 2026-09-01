using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.JSInterop;
using Serilog;

namespace CodingAgentWebUI.Components.Pages;

/// <summary>
/// Blazor component for the Agent Monitoring page. Live run streaming uses
/// <see cref="IAgentHubConnection"/> hub subscriptions (run-{jobId} groups).
/// Cancel/disconnect operations route through <see cref="AgentMonitoringPageService"/>.
/// </summary>
public partial class AgentMonitoring : IAsyncDisposable
{
    private const string JsScrollToBottom = "scrollToBottom";
    private const string JsScrollActiveStep = "scrollActiveStepIntoView";

    [Inject] private AgentMonitoringPageService PageService { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private TimeProvider Clock { get; set; } = default!;
    [Inject] private IConsolidationService ConsolidationService { get; set; } = default!;
    [Inject] private IAgentRegistryService Registry { get; set; } = default!;
    // Hub connection for live run streaming (scoped per circuit)
    [Inject] private IAgentHubConnection HubConnection { get; set; } = default!;
    // Reload run state from API on reconnect / initial load
    [Inject] private IPipelineApiRunHistoryClient RunHistoryClient { get; set; } = default!;

    // ── State forwarding from PageService ──

    private IReadOnlyList<ActiveRunSummary> _activeRuns => PageService.ActiveRuns;
    private IReadOnlyList<AgentEntry> _agents => PageService.Agents;
    private IReadOnlyList<PendingJob> _queuedJobs => PageService.QueuedJobs;
    private IReadOnlyList<ConsolidationRun> _activeConsolidationRuns => PageService.ActiveConsolidationRuns;
    private IReadOnlyList<ConsolidationRun> _queuedConsolidationRuns => PageService.QueuedConsolidationRuns;
    private IReadOnlyList<PipelineRunSummary> _runHistory =>
        PageService.RunHistory.Where(r => r.FinalStep.IsTerminal()).ToList();
    private int _maxRetries => PageService.MaxRetries;

    // ── UI-only state ──

    private bool _historyExpanded = true;
    private string? _selectedRunId;
    private bool _showRunDetailModal;
    private bool _scrollModalOnNextRender;
    private bool _focusModalOnNextRender;
    private ElementReference _modalOverlayRef;
    private PipelineRunSummary? _selectedHistoryRun;
    private bool _showHistoryDetailModal;
    private bool _disposed;
    private ITimer? _refreshTimer;
    private bool _showDisconnectConfirm;
    private DateTimeOffset _lastSuccessfulRefresh;
    private bool _lastRefreshFailed;

    // ── Live run streaming state ──

    // Loaded from API on modal open; updated on hub OnRunCompleted
    private PipelineRunSummary? _activeModalRun;
    // Accumulated output lines pushed via hub OnOutputLines; includes backlog from SubscribeToRun
    private readonly List<string> _activeModalOutputLines = [];
    // Current pipeline step from hub OnStepTransition
    private PipelineStep? _activeModalCurrentStep;

    // View model for PipelineSidebar — seeded from RunStateSnapshot hub event (active runs)
    // or constructed from PipelineRunSummary (completed runs). Null until first snapshot or summary loads.
    private PipelineRun? _activeModalRunModel;

    // Disposable hub event subscriptions
    private IDisposable? _outputLinesSub;
    private IDisposable? _stepTransitionSub;
    private IDisposable? _runCompletedSub;
    private IDisposable? _runStateSnapshotSub;

    // ── Lifecycle ──

    protected override async Task OnInitializedAsync()
    {
        _lastSuccessfulRefresh = Clock.GetUtcNow();
        // IChangeNotifier removed — change notification arrives via hub events.
        // ConsolidationService.OnChange still drives the consolidation panel refresh.
        ConsolidationService.OnChange += HandleStateChanged;

        // Register hub event handlers for live run streaming.
        // Handlers filter by _selectedRunId so events from other runs are ignored.
        RegisterHubEventHandlers();

        // Start hub connection if not already connected.
        if (HubConnection.State == HubConnectionState.Disconnected)
        {
            try
            {
                await HubConnection.StartAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "AgentMonitoring: hub connection start failed — live streaming unavailable until reconnect");
            }
        }

        // Refresh every 5 seconds for heartbeat/elapsed updates
        _refreshTimer = Clock.CreateTimer(RefreshTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

        await PageService.InitializeAsync();
    }

    /// <summary>
    /// Registers typed handlers for all hub push events this page uses.
    /// Handlers are fire-and-forget (async void) since Action callbacks cannot be awaited.
    /// Each handler filters on <see cref="_selectedRunId"/> to ignore other runs.
    /// </summary>
    private void RegisterHubEventHandlers()
    {
        // OnOutputLines(jobId, lines) — append lines to the active modal output buffer
        _outputLinesSub = HubConnection.On<string, IReadOnlyList<string>>(
            HubMethodNames.OnOutputLines,
            (jobId, lines) => HandleOutputLines(jobId, lines));

        // OnStepTransition(jobId, step, timestamp) — update current step indicator and HighWaterMark
        _stepTransitionSub = HubConnection.On<string, PipelineStep, DateTimeOffset>(
            HubMethodNames.OnStepTransition,
            (jobId, step, timestamp) => HandleStepTransition(jobId, step));

        // OnRunCompleted(jobId, payload) — reload run state from API for final metadata
        _runCompletedSub = HubConnection.On<string, JobCompletionPayload>(
            HubMethodNames.OnRunCompleted,
            (jobId, payload) => HandleRunCompleted(jobId));

        // OnRunStateSnapshot(jobId, snapshot) — seed PipelineSidebar view model on modal open
        _runStateSnapshotSub = HubConnection.On<string, RunStateSnapshot>(
            HubMethodNames.OnRunStateSnapshot,
            (jobId, snapshot) => HandleRunStateSnapshot(jobId, snapshot));

        // Re-subscribe to the active run group after reconnect to restore live streaming.
        HubConnection.Reconnected += HandleReconnected;
    }

    private void HandleOutputLines(string jobId, IReadOnlyList<string> lines)
    {
        if (_disposed || jobId != _selectedRunId) return;
        _ = InvokeAsync(() =>
        {
            if (_disposed) return;
            _activeModalOutputLines.AddRange(lines);
            _scrollModalOnNextRender = true;
            StateHasChanged();
        });
    }

    private void HandleStepTransition(string jobId, PipelineStep step)
    {
        if (_disposed || jobId != _selectedRunId) return;
        _ = InvokeAsync(() =>
        {
            if (_disposed) return;
            _activeModalCurrentStep = step;

            // Update the sidebar view model's CurrentStep and HighWaterMark.
            // HighWaterMark must use logical order (not raw enum int) because
            // RunningEnvironmentSetup = 29 (highest int) but is logically position 2.
            if (_activeModalRunModel != null)
            {
                _activeModalRunModel.CurrentStep = step;
                if (StepOrder.GetOrder(step) > StepOrder.GetOrder(_activeModalRunModel.HighWaterMark))
                    _activeModalRunModel.HighWaterMark = step;
            }

            StateHasChanged();
        });
    }

    private void HandleRunCompleted(string jobId)
    {
        if (_disposed || jobId != _selectedRunId) return;
        _ = InvokeAsync(async () =>
        {
            if (_disposed) return;
            if (Guid.TryParse(jobId, out var runGuid))
            {
                await ReloadCompletedRunAsync(jobId, runGuid);
            }
            StateHasChanged();
        });
    }

    /// <summary>
    /// Handles the <c>OnRunStateSnapshot</c> hub event pushed by the server on <c>SubscribeToRun</c>.
    /// Seeds the <see cref="_activeModalRunModel"/> if it has not yet been constructed (handles the
    /// race where the snapshot arrives before the API response from GetRunAsync).
    /// Updates all sidebar-relevant fields from the snapshot.
    /// </summary>
    private void HandleRunStateSnapshot(string jobId, RunStateSnapshot snapshot)
    {
        // TODO [WARNING]: _selectedRunId is read here on the SignalR I/O thread before entering InvokeAsync,
        // which is a data race — the field is written on the Blazor sync context. This is a pre-existing
        // pattern in the file (HandleStepTransition, HandleOutputLines have the same issue), but this
        // handler adds a third occurrence. Low probability of manifesting, but a stale/torn read could
        // silently drop a valid snapshot or process one for the wrong run.
        if (_disposed || jobId != _selectedRunId) return;
        _ = InvokeAsync(() =>
        {
            if (_disposed) return;

            if (_activeModalRunModel == null)
            {
                // Construct the sidebar view model from snapshot alone (snapshot-before-API-response race).
                // IssueProviderConfigId / RepoProviderConfigId are not in the snapshot; use string.Empty.
                // The sidebar does not read these fields so this is safe.
                // BrainProviderConfigId is init-only so must be set at construction time.
                _activeModalRunModel = new PipelineRun
                {
                    RunId = jobId,
                    IssueIdentifier = snapshot.IssueIdentifier ?? string.Empty,
                    IssueTitle = snapshot.IssueTitle ?? string.Empty,
                    IssueProviderConfigId = string.Empty,
                    RepoProviderConfigId = string.Empty,
                    BrainProviderConfigId = snapshot.BrainProviderConfigId,
                    RunType = snapshot.RunType,
                };
            }

            // Apply all snapshot fields to the view model.
            ApplySnapshotToRunModel(_activeModalRunModel, snapshot);

            // Also update the scalar tracking field used by the left-column "Step:" display.
            _activeModalCurrentStep = snapshot.CurrentStep;

            StateHasChanged();
        });
    }

    /// <summary>
    /// Applies the fields from a <see cref="RunStateSnapshot"/> to the sidebar view model.
    /// Called both when constructing the model from scratch and when updating an existing one.
    /// </summary>
    private static void ApplySnapshotToRunModel(PipelineRun model, RunStateSnapshot snapshot)
    {
        model.CurrentStep = snapshot.CurrentStep;
        model.HighWaterMark = snapshot.HighWaterMark;
        model.RetryCount = snapshot.RetryCount;
        model.BranchName = snapshot.BranchName;
        model.BaselineHealthPassed = snapshot.BaselineHealthPassed;
        model.BrainContextLoaded = snapshot.BrainContextLoaded;
        model.BrainKnowledgeFileCount = snapshot.BrainKnowledgeFileCount;
        model.IssueLabels = snapshot.IssueLabels;
        model.AnalysisSkipped = snapshot.AnalysisSkipped;
        model.AnalysisRecommendation = snapshot.AnalysisRecommendation;
        model.FilesChangedCount = snapshot.FilesChangedCount;
        model.LinesAdded = snapshot.LinesAdded;
        model.LinesRemoved = snapshot.LinesRemoved;
        model.CodeReviewIterationsCompleted = snapshot.CodeReviewIterationsCompleted;
        model.CodeReviewIterationInProgress = snapshot.CodeReviewIterationInProgress;
        model.CodeReviewIterationsTotal = snapshot.CodeReviewIterationsTotal;
        model.CodeReviewAgentsRun = snapshot.CodeReviewAgentsRun;
        model.SetCodeReviewCounts(snapshot.CodeReviewCriticalCount, snapshot.CodeReviewWarningCount, snapshot.CodeReviewSuggestionCount);
        model.LatestQualityReport = snapshot.LatestQualityReport;
        model.PullRequestUrl = snapshot.PullRequestUrl;
        model.PullRequestNumber = snapshot.PullRequestNumber;
        model.IsDraftPr = snapshot.IsDraftPr;
        model.BlacklistedFilesDetected = snapshot.BlacklistedFilesDetected;
        model.OpenIssuesDownloaded = snapshot.OpenIssuesDownloaded;
        model.BrainFilesCommitted = snapshot.BrainFilesCommitted;
        model.BrainUpdatesPushed = snapshot.BrainUpdatesPushed;
        model.DecompositionSubIssuesCreated = snapshot.DecompositionSubIssuesCreated;
        model.DecompositionSubIssuesAttempted = snapshot.DecompositionSubIssuesAttempted;
        model.FailureReason = snapshot.FailureReason;
        model.ModelName = snapshot.ModelName;
        model.RepositoryName = snapshot.RepositoryName;
        // Drain the queue before re-enqueuing so that reconnect does not produce duplicate entries.
        // Without this, a second ApplySnapshotToRunModel call (triggered by hub reconnect) would
        // append to the existing entries, producing duplicates (2 reports → 4 after one reconnect).
        while (model.QualityGateHistory.TryDequeue(out _)) { }
        foreach (var report in snapshot.QualityGateHistory)
            model.QualityGateHistory.Enqueue(report);
        // Use the public ResetStartedAt method since StartedAtOffset setter is internal.
        if (snapshot.StartedAtOffset != default)
            model.ResetStartedAt(snapshot.StartedAtOffset);
    }

    /// <summary>
    /// Seeds the sidebar view model from a completed run's <see cref="PipelineRunSummary"/>.
    /// For completed runs the hub will not push a <see cref="RunStateSnapshot"/> (GetRun returns null).
    /// Uses FinalStep as CurrentStep and HighWaterMark; sets BrainProviderConfigId if BrainRepoUsed.
    /// </summary>
    private static PipelineRun BuildRunModelFromSummary(PipelineRunSummary summary)
    {
        var model = new PipelineRun
        {
            RunId = summary.RunId,
            IssueIdentifier = summary.IssueIdentifier,
            IssueTitle = summary.IssueTitle,
            // IssueProviderConfigId / RepoProviderConfigId not in summary — use placeholders.
            // The sidebar does not read these fields.
            IssueProviderConfigId = string.Empty,
            RepoProviderConfigId = string.Empty,
            // BrainProviderConfigId: PipelineSidebar.IsStepHidden hides brain steps when == null.
            // For completed runs where BrainRepoUsed=true, set a placeholder so brain steps are shown.
            // NOTE: BrainProviderConfigId is init-only so must be set at construction time.
            BrainProviderConfigId = summary.BrainRepoUsed ? "placeholder" : null,
        };

        // Terminal step is both current and the high-water mark for completed runs.
        model.CurrentStep = summary.FinalStep;
        // NOTE: GetLastReachedStep() uses raw >= comparisons on HighWaterMark which is a pre-existing bug
        // (RunningEnvironmentSetup=29 highest int but position 2). We set HighWaterMark directly from FinalStep
        // to avoid incorrect results from GetLastReachedStep for runs that passed through RunningEnvironmentSetup.
        model.HighWaterMark = summary.FinalStep;
        model.RetryCount = summary.RetryCount;
        model.AnalysisRecommendation = summary.AnalysisRecommendation;
        model.CodeReviewAgentsRun = summary.CodeReviewAgentsRun;
        model.ModelName = summary.ModelName;
        model.PullRequestUrl = summary.PullRequestUrl;
        model.DecompositionSubIssuesCreated = summary.DecompositionSubIssuesCreated;
        model.DecompositionSubIssuesAttempted = summary.DecompositionSubIssuesAttempted;
        model.BrainUpdatesPushed = summary.BrainUpdatesPushed;
        // Use the public ResetStartedAt method since StartedAtOffset setter is internal.
        model.ResetStartedAt(summary.StartedAtOffset);

        return model;
    }

    private async Task ReloadCompletedRunAsync(string jobId, Guid runGuid)
    {
        try
        {
            _activeModalRun = await RunHistoryClient.GetRunAsync(runGuid, CancellationToken.None);

            // Update sidebar model for the terminal state from the reloaded summary.
            if (_activeModalRun != null && _activeModalRunModel != null)
            {
                _activeModalRunModel.CurrentStep = _activeModalRun.FinalStep;
                // Keep HighWaterMark at max of current value and FinalStep.
                if (StepOrder.GetOrder(_activeModalRun.FinalStep) > StepOrder.GetOrder(_activeModalRunModel.HighWaterMark))
                    _activeModalRunModel.HighWaterMark = _activeModalRun.FinalStep;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "AgentMonitoring: failed to reload completed run {RunId}", jobId);
        }
    }

    /// <summary>
    /// Re-subscribes to the active run group after a hub reconnection.
    /// Without this, the client would miss step transitions and output lines during the
    /// reconnect window and the sidebar state would be stale.
    /// </summary>
    private Task HandleReconnected(string? newConnectionId)
    {
        if (_disposed || _selectedRunId is null) return Task.CompletedTask;
        // TODO [WARNING]: The Task returned by InvokeAsync is discarded (fire-and-forget). If InvokeAsync
        // throws synchronously before entering the lambda (e.g. ObjectDisposedException when the component
        // is concurrently disposed after the outer _disposed check), the exception is silently swallowed.
        // The inner _disposed guard cannot protect against this specific race. Low probability, consistent
        // with the existing fire-and-forget pattern in this file, but worth hardening with a try/catch
        // around the InvokeAsync call itself.
        _ = InvokeAsync(async () =>
        {
            if (_disposed || _selectedRunId is null) return;
            try
            {
                await HubConnection.InvokeAsync(HubMethodNames.SubscribeToRun, _selectedRunId, CancellationToken.None);
                Log.Debug("AgentMonitoring: re-subscribed to run {RunId} after reconnect", _selectedRunId);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "AgentMonitoring: failed to re-subscribe to run {RunId} after reconnect", _selectedRunId);
            }
        });
        return Task.CompletedTask;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_scrollModalOnNextRender && _showRunDetailModal)
        {
            _scrollModalOnNextRender = false;
            try
            {
                await Task.Delay(50);
                await JS.InvokeVoidAsync(JsScrollActiveStep, "sidebarSteps");
                await JS.InvokeVoidAsync(JsScrollToBottom, "modalOutputPanel");
            }
            catch (Exception ex) { Log.Debug(ex, "JS interop scroll failed in modal render"); }
        }

        if (_focusModalOnNextRender && _showRunDetailModal)
        {
            _focusModalOnNextRender = false;
            try
            {
                await _modalOverlayRef.FocusAsync();
            }
            catch (Exception ex) { Log.Debug(ex, "Modal focus failed"); }
        }
    }

    // ── Timer & Event Handlers ──

    private async void RefreshTick(object? state)
    {
        if (_disposed) return;
        try
        {
            await InvokeAsync(async () =>
            {
                if (_disposed) return;
                await PageService.RefreshDataAsync(includeConsolidation: true);
                _lastSuccessfulRefresh = Clock.GetUtcNow();
                _lastRefreshFailed = false;
                StateHasChanged();
            });
        }
        catch (ObjectDisposedException) { /* Intentional: component disposed between timer tick and InvokeAsync; no action needed. */ }
        catch
        {
            try
            {
                await InvokeAsync(() => { _lastRefreshFailed = true; StateHasChanged(); });
            }
            catch (ObjectDisposedException) { /* Intentional: component disposed while recording refresh failure; no action needed. */ }
        }
    }

    private async void HandleStateChanged()
    {
        if (_disposed) return;
        try
        {
            await InvokeAsync(async () =>
            {
                if (_disposed) return;
                await PageService.RefreshDataAsync(includeConsolidation: true);
                _lastSuccessfulRefresh = Clock.GetUtcNow();
                _lastRefreshFailed = false;
                StateHasChanged();
            });
        }
        catch (ObjectDisposedException) { /* Intentional: component disposed during state-change notification; no action needed. */ }
        catch
        {
            try
            {
                await InvokeAsync(() => { _lastRefreshFailed = true; StateHasChanged(); });
            }
            catch (ObjectDisposedException) { /* Intentional: component disposed while recording refresh failure; no action needed. */ }
        }
    }

    // ── UI Event Handlers ──

    /// <summary>
    /// Opens the run detail modal and subscribes to the hub run group for live streaming.
    /// Server pushes the output backlog and RunStateSnapshot immediately on SubscribeToRun.
    /// All async work and StateHasChanged run inside InvokeAsync so exceptions stay on the
    /// Blazor synchronization context rather than propagating to the thread pool.
    /// </summary>
    private async Task OpenRunDetail(string runId)
    {
        await InvokeAsync(async () =>
        {
            if (_disposed) return;
            try
            {
                _selectedRunId = runId;
                _showRunDetailModal = true;
                _scrollModalOnNextRender = true;
                _focusModalOnNextRender = true;

                // Reset live streaming state for this run
                _activeModalRun = null;
                _activeModalRunModel = null;
                _activeModalOutputLines.Clear();
                _activeModalCurrentStep = null;

                // Subscribe to hub group — server pushes backlog and RunStateSnapshot immediately.
                // The snapshot may arrive before _activeModalRun is populated (race condition handled
                // in HandleRunStateSnapshot by constructing the model from the snapshot alone).
                if (HubConnection.State == HubConnectionState.Connected)
                {
                    try
                    {
                        await HubConnection.InvokeAsync(HubMethodNames.SubscribeToRun, runId, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "AgentMonitoring: failed to subscribe to run {RunId}", runId);
                    }
                }

                // Load current run state from API for run metadata
                if (Guid.TryParse(runId, out var runGuid))
                {
                    try
                    {
                        _activeModalRun = await RunHistoryClient.GetRunAsync(runGuid, CancellationToken.None);

                        // For completed runs (GetRun returns null for them), seed the sidebar view model
                        // from the summary if the snapshot handler hasn't already created it.
                        // For active runs, the snapshot handler creates the model; we only fill in
                        // any fields that the snapshot doesn't cover (IssueIdentifier, IssueTitle).
                        if (_activeModalRun != null)
                        {
                            if (_activeModalRunModel == null)
                            {
                                // Completed run — no snapshot will arrive; build from summary.
                                _activeModalRunModel = BuildRunModelFromSummary(_activeModalRun);
                            }
                            else
                            {
                                // Active run — snapshot already created the model.
                                // IssueTitle is settable (not init-only), so update it if available.
                                // IssueIdentifier is required init — it was set from the snapshot's IssueIdentifier;
                                // if the summary provides a more accurate title, update it.
                                _activeModalRunModel.IssueTitle = _activeModalRun.IssueTitle;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "AgentMonitoring: failed to load run {RunId} from API", runId);
                    }
                }

                StateHasChanged();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "AgentMonitoring: OpenRunDetail failed for run {RunId}", runId);
            }
        });
    }

    /// <summary>
    /// Returns true if the run is still active (not in a terminal state).
    /// Used to control the PipelineSidebar IsRunning parameter.
    /// </summary>
    private static bool IsActiveRun(PipelineRunSummary run) => !run.FinalStep.IsTerminal();

    /// <summary>
    /// Closes the run detail modal and unsubscribes from the hub run group.
    /// All async work and StateHasChanged run inside InvokeAsync so exceptions stay on the
    /// Blazor synchronization context rather than propagating to the thread pool.
    /// </summary>
    private async Task DismissRunDetailModal()
    {
        await InvokeAsync(async () =>
        {
            if (_disposed) return;
            try
            {
                var runId = _selectedRunId;

                _showRunDetailModal = false;
                _selectedRunId = null;
                _activeModalRun = null;
                _activeModalRunModel = null;
                _activeModalOutputLines.Clear();
                _activeModalCurrentStep = null;

                if (runId is not null && HubConnection.State == HubConnectionState.Connected)
                {
                    try
                    {
                        await HubConnection.InvokeAsync(HubMethodNames.UnsubscribeFromRun, runId, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "AgentMonitoring: failed to unsubscribe from run {RunId}", runId);
                    }
                }

                StateHasChanged();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "AgentMonitoring: DismissRunDetailModal failed");
            }
        });
    }

    private void OpenHistoryRunDetail(PipelineRunSummary run)
    {
        _selectedHistoryRun = run;
        _showHistoryDetailModal = true;
    }

    private void DismissHistoryDetailModal()
    {
        _showHistoryDetailModal = false;
        _selectedHistoryRun = null;
    }

    private async Task HandleModalKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
            await DismissRunDetailModal();
    }

    private async Task CancelAgentRunById(string runId)
    {
        await PageService.CancelAgentRunByIdAsync(runId);
        StateHasChanged();
    }

    private async Task RemoveFromQueue(string issueIdentifier, string issueProviderId)
    {
        await PageService.RemoveFromQueueAsync(issueIdentifier, issueProviderId);
        StateHasChanged();
    }

    private async Task CancelConsolidationRun(string runId)
    {
        await PageService.CancelConsolidationRunAsync(runId);
        StateHasChanged();
    }

    private async Task SelectAgent(string agentId)
    {
        // Find the active run for this agent and open the run detail modal
        var run = _activeRuns.FirstOrDefault(r => r.AgentId?.Value == agentId);
        if (run != null)
        {
            await OpenRunDetail(run.RunId);
        }
    }

    private static void EnableAgent(AgentEntry agent) => AgentMonitoringPageService.EnableAgent(agent);

    private static void DisableAgent(AgentEntry agent) => AgentMonitoringPageService.DisableAgent(agent);

    private void ShowDisconnectConfirm() => _showDisconnectConfirm = true;

    private async Task ForceDisconnect(AgentEntry agent)
    {
        await PageService.ForceDisconnectAsync(agent);
        _showDisconnectConfirm = false;
        await DismissRunDetailModal();
    }

    // ── Resolvers (delegate to PageService) ──

    private ProviderConfig? ResolveProvider(string? configId) => PageService.ResolveProvider(configId);

    // ── Sub-component callback adapters ──

    private async Task HandleRemoveFromQueue((string IssueIdentifier, string IssueProviderId) args)
    {
        await RemoveFromQueue(args.IssueIdentifier, args.IssueProviderId);
    }

    // ── Dispose ──

    /// <summary>
    /// Disposes hub event subscriptions and the refresh timer.
    /// Does NOT dispose <see cref="HubConnection"/> — it is scoped to the circuit (DI scope)
    /// and shared across navigations within the same tab. Disposing it here would leave a
    /// dead connection the next time this page is opened in the same circuit.
    /// The DI container disposes the scoped <see cref="IAgentHubConnection"/> when the
    /// circuit tears down (tab close / timeout).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _outputLinesSub?.Dispose();
        _stepTransitionSub?.Dispose();
        _runCompletedSub?.Dispose();
        _runStateSnapshotSub?.Dispose();

        // Unregister reconnect handler to prevent memory leak.
        HubConnection.Reconnected -= HandleReconnected;

        _refreshTimer?.Dispose();
        ConsolidationService.OnChange -= HandleStateChanged;

        // Unsubscribe from the run group if a modal is still open (circuit disconnect / tab close)
        if (_selectedRunId is not null && HubConnection.State == HubConnectionState.Connected)
        {
            try
            {
                await HubConnection.InvokeAsync(HubMethodNames.UnsubscribeFromRun, _selectedRunId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "AgentMonitoring DisposeAsync: failed to unsubscribe from run {RunId}", _selectedRunId);
            }
        }

        // Do NOT dispose HubConnection here — scoped to the circuit, not the component.

        GC.SuppressFinalize(this);
    }
}
