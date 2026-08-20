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
/// Spec 045: Live streaming restored via <see cref="IAgentHubConnection"/> hub subscriptions.
/// IOrchestratorRunService, IRunLifecycleManager, IHubContext, PipelineRunLifecycleService, and
/// IChangeNotifier injections removed — the monolith no longer owns in-memory run state.
/// Active run output is streamed from the API hub using run-{jobId} groups.
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
    // Spec 045 Req 3.1-3.6: hub connection for live run streaming (scoped per circuit)
    [Inject] private IAgentHubConnection HubConnection { get; set; } = default!;
    // Spec 045 Req 3.4: reload run state from API on reconnect / initial load
    [Inject] private IPipelineApiRunHistoryClient RunHistoryClient { get; set; } = default!;

    // ── State forwarding from PageService ──

    private IReadOnlyList<ActiveRunSummary> _activeRuns => PageService.ActiveRuns;
    private IReadOnlyList<AgentEntry> _agents => PageService.Agents;
    private IReadOnlyList<PendingJob> _queuedJobs => PageService.QueuedJobs;
    private IReadOnlyList<ConsolidationRun> _activeConsolidationRuns => PageService.ActiveConsolidationRuns;
    private IReadOnlyList<ConsolidationRun> _queuedConsolidationRuns => PageService.QueuedConsolidationRuns;
    private IReadOnlyList<PipelineRunSummary> _runHistory => PageService.RunHistory;
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
    private Timer? _refreshTimer;
    private bool _showDisconnectConfirm;
    private DateTimeOffset _lastSuccessfulRefresh;
    private bool _lastRefreshFailed;

    // ── Live run streaming state (Spec 045 Req 3.1-3.6) ──

    // Loaded from API on modal open; updated on hub OnRunCompleted
    private PipelineRunSummary? _activeModalRun;
    // Accumulated output lines pushed via hub OnOutputLines; includes backlog from SubscribeToRun
    private readonly List<string> _activeModalOutputLines = [];
    // Current pipeline step from hub OnStepTransition
    private PipelineStep? _activeModalCurrentStep;

    // Disposable hub event subscriptions
    private IDisposable? _outputLinesSub;
    private IDisposable? _stepTransitionSub;
    private IDisposable? _runCompletedSub;

    // ── Lifecycle ──

    protected override async Task OnInitializedAsync()
    {
        _lastSuccessfulRefresh = Clock.GetUtcNow();
        // Spec 045: IChangeNotifier fully removed — NullChangeNotifier stub deleted.
        // Change notification for the monitoring page arrives via hub events.
        // ConsolidationService.OnChange still drives the consolidation panel refresh.
        ConsolidationService.OnChange += HandleStateChanged;

        // Register hub event handlers for live run streaming (Spec 045 Req 3.2).
        // Handlers filter by _selectedRunId so events from other runs are ignored.
        RegisterHubEventHandlers();

        // Start hub connection if not already connected.
        // Uses the scoped AgentHubConnection registered in Program.cs (Req 3.6 L1).
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
        _refreshTimer = new Timer(RefreshTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

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
            (jobId, lines) =>
            {
                if (_disposed || jobId != _selectedRunId) return;
                _ = InvokeAsync(() =>
                {
                    if (_disposed) return;
                    _activeModalOutputLines.AddRange(lines);
                    _scrollModalOnNextRender = true;
                    StateHasChanged();
                });
            });

        // OnStepTransition(jobId, step, timestamp) — update current step indicator
        _stepTransitionSub = HubConnection.On<string, PipelineStep, DateTimeOffset>(
            HubMethodNames.OnStepTransition,
            (jobId, step, timestamp) =>
            {
                if (_disposed || jobId != _selectedRunId) return;
                var unused1 = InvokeAsync(() =>
                {
                    if (_disposed) return;
                    _activeModalCurrentStep = step;
                    StateHasChanged();
                });
            });

        // OnRunCompleted(jobId, payload) — reload run state from API for final metadata
        _runCompletedSub = HubConnection.On<string, JobCompletionPayload>(
            HubMethodNames.OnRunCompleted,
            (jobId, payload) =>
            {
                if (_disposed || jobId != _selectedRunId) return;
                var unused2 = InvokeAsync(async () =>
                {
                    if (_disposed) return;
                    // Reload from API for authoritative final state (Req 3.4)
                    if (Guid.TryParse(jobId, out var runGuid))
                    {
                        try
                        {
                            _activeModalRun = await RunHistoryClient.GetRunAsync(runGuid, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "AgentMonitoring: failed to reload completed run {RunId}", jobId);
                        }
                    }
                    StateHasChanged();
                });
            });
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
    /// Server pushes the output backlog immediately on SubscribeToRun (Req 3.4a).
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
                _activeModalOutputLines.Clear();
                _activeModalCurrentStep = null;

                // Subscribe to hub group — server pushes backlog immediately (Req 3.4a)
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

                // Load current run state from API for run metadata (Req 3.2 step 1)
                if (Guid.TryParse(runId, out var runGuid))
                {
                    try
                    {
                        _activeModalRun = await RunHistoryClient.GetRunAsync(runGuid, CancellationToken.None);
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
    /// Closes the run detail modal and unsubscribes from the hub run group (Req 3.3).
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

    private async Task CancelAgentRun(PipelineRun run)
    {
        await PageService.CancelAgentRunAsync(run);
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

    private string ResolveProfileName(string profileId) => PageService.ResolveProfileName(profileId);

    private string ResolveQgcName(string qgcId) => PageService.ResolveQgcName(qgcId);

    // ── Sub-component callback adapters ──

    private async Task HandleRemoveFromQueue((string IssueIdentifier, string IssueProviderId) args)
    {
        await RemoveFromQueue(args.IssueIdentifier, args.IssueProviderId);
    }

    // ── Dispose ──

    /// <summary>
    /// Disposes hub event subscriptions, the refresh timer, and event handler registrations.
    /// Unsubscribes from the active run group if the run detail modal was open (Req 3.3).
    /// Implements IAsyncDisposable (Spec 045 Req 3.6).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _outputLinesSub?.Dispose();
        _stepTransitionSub?.Dispose();
        _runCompletedSub?.Dispose();

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

        GC.SuppressFinalize(this);
    }
}
