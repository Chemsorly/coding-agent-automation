using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Serilog;

namespace CodingAgentWebUI.Components.Pages;

public partial class AgentMonitoring : IDisposable
{
    private const string JsScrollToBottom = "scrollToBottom";
    private const string JsScrollActiveStep = "scrollActiveStepIntoView";

    [Inject] private AgentMonitoringPageService PageService { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private TimeProvider Clock { get; set; } = default!;
    [Inject] private IChangeNotifier ChangeNotifier { get; set; } = default!;
    [Inject] private IConsolidationService ConsolidationService { get; set; } = default!;
    [Inject] private IAgentRegistryService Registry { get; set; } = default!;
    [Inject] private IOrchestratorRunService RunService { get; set; } = default!;
    [Inject] private PipelineRunLifecycleService Lifecycle { get; set; } = default!;

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

    // ── Lifecycle ──

    protected override async Task OnInitializedAsync()
    {
        _lastSuccessfulRefresh = Clock.GetUtcNow();
        ChangeNotifier.OnChange += HandleStateChanged;
        ConsolidationService.OnChange += HandleStateChanged;

        // Refresh every 5 seconds for heartbeat/elapsed updates
        _refreshTimer = new Timer(RefreshTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

        await PageService.InitializeAsync();
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

    private void OpenRunDetail(string runId)
    {
        _selectedRunId = runId;
        _showRunDetailModal = true;
        _scrollModalOnNextRender = true;
        _focusModalOnNextRender = true;
    }

    private void DismissRunDetailModal()
    {
        _showRunDetailModal = false;
        _selectedRunId = null;
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

    private void HandleModalKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
            DismissRunDetailModal();
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

    private void SelectAgent(string agentId)
    {
        // Find the active run for this agent and open the run detail modal
        var run = _activeRuns.FirstOrDefault(r => r.AgentId?.Value == agentId);
        if (run != null)
        {
            OpenRunDetail(run.RunId);
        }
    }

    private static void EnableAgent(AgentEntry agent) => AgentMonitoringPageService.EnableAgent(agent);

    private static void DisableAgent(AgentEntry agent) => AgentMonitoringPageService.DisableAgent(agent);

    private void ShowDisconnectConfirm() => _showDisconnectConfirm = true;

    private async Task ForceDisconnect(AgentEntry agent)
    {
        await PageService.ForceDisconnectAsync(agent);
        _showDisconnectConfirm = false;
        DismissRunDetailModal();
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

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            _refreshTimer?.Dispose();
            ChangeNotifier.OnChange -= HandleStateChanged;
            ConsolidationService.OnChange -= HandleStateChanged;
        }
        _disposed = true;
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
