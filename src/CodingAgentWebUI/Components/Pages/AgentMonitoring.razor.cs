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
    [Inject] private PipelineOrchestrationService PipelineService { get; set; } = default!;
    [Inject] private IConsolidationService ConsolidationService { get; set; } = default!;
    [Inject] private IAgentRegistryService Registry { get; set; } = default!;
    [Inject] private IOrchestratorRunService RunService { get; set; } = default!;

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
    private ElementReference _historyModalOverlayRef;
    private bool _disposed;
    private Timer? _refreshTimer;
    private bool _showDisconnectConfirm;
    private DateTimeOffset _lastSuccessfulRefresh;
    private bool _lastRefreshFailed;

    // ── Lifecycle ──

    protected override async Task OnInitializedAsync()
    {
        _lastSuccessfulRefresh = Clock.GetUtcNow();
        PipelineService.OnChange += HandleStateChanged;
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
        catch (ObjectDisposedException) { }
        catch
        {
            try
            {
                await InvokeAsync(() => { _lastRefreshFailed = true; StateHasChanged(); });
            }
            catch (ObjectDisposedException) { }
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
        catch (ObjectDisposedException) { }
        catch
        {
            try
            {
                await InvokeAsync(() => { _lastRefreshFailed = true; StateHasChanged(); });
            }
            catch (ObjectDisposedException) { }
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

    private void HandleHistoryModalKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
            DismissHistoryDetailModal();
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
        var run = _activeRuns.FirstOrDefault(r => r.AgentId == agentId);
        if (run != null)
        {
            OpenRunDetail(run.RunId);
        }
    }

    private void EnableAgent(AgentEntry agent) => PageService.EnableAgent(agent);

    private void DisableAgent(AgentEntry agent) => PageService.DisableAgent(agent);

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

    // ── Static UI Formatters ──

    private static string GetStatusColorClass(AgentStatus status) => status switch
    {
        AgentStatus.Idle => "text-success",
        AgentStatus.Busy => "text-warning",
        AgentStatus.Disconnected => "text-danger",
        _ => ""
    };

    private static string FormatRunType(PipelineRunType runType) => runType switch
    {
        PipelineRunType.Review => "PR Review",
        PipelineRunType.DecompositionAnalysis => "Decomposition (Analysis)",
        PipelineRunType.Decomposition => "Decomposition",
        _ => "Implementation"
    };

    private static string FormatConsolidationRunType(ConsolidationRunType type) => type switch
    {
        ConsolidationRunType.BrainConsolidation => "Brain Consolidation",
        ConsolidationRunType.RefactoringDetection => "Refactoring Detection",
        ConsolidationRunType.HarnessSuggestions => "Harness Suggestions",
        _ => type.ToString()
    };

    private static string FormatConsolidationRunTypeShort(ConsolidationRunType type) => type switch
    {
        ConsolidationRunType.BrainConsolidation => "Brain",
        ConsolidationRunType.RefactoringDetection => "Refactor",
        ConsolidationRunType.HarnessSuggestions => "Harness",
        _ => type.ToString()
    };

    private static string GetConsolidationTypeIconName(ConsolidationRunType type) => type switch
    {
        ConsolidationRunType.BrainConsolidation => "brain",
        ConsolidationRunType.RefactoringDetection => "refresh-cw",
        ConsolidationRunType.HarnessSuggestions => "sparkles",
        _ => "clipboard-list"
    };

    private static string FormatDuration(DateTime startedAt, DateTime? completedAt)
    {
        if (completedAt is null) return "—";
        var duration = completedAt.Value - startedAt;
        return duration.ToString(@"hh\:mm\:ss");
    }

    private static string FormatTimestamp(DateTime timestamp)
    {
        var local = timestamp.Kind == DateTimeKind.Utc ? timestamp.ToLocalTime() : timestamp;
        var ago = DateTime.Now - local;
        if (ago.TotalMinutes < 60) return $"{(int)ago.TotalMinutes}m ago";
        if (ago.TotalHours < 24) return $"{(int)ago.TotalHours}h ago";
        if (ago.TotalDays < 7) return $"{(int)ago.TotalDays}d ago";
        return local.ToString("yyyy-MM-dd HH:mm");
    }

    // ── Dispose ──

    public void Dispose()
    {
        _disposed = true;
        _refreshTimer?.Dispose();
        PipelineService.OnChange -= HandleStateChanged;
        ConsolidationService.OnChange -= HandleStateChanged;
    }
}
