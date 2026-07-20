using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Encapsulates the business logic for the AgentMonitoring page — state management,
/// data refresh, cancellation orchestration, and lookup helpers. The Blazor component
/// delegates to this service and retains only UI state (modals, scroll flags, JS interop).
/// Registered as Scoped because it holds per-page mutable state.
/// </summary>
public class AgentMonitoringPageService : IDisposable
{
    private static readonly ILogger Logger = Log.ForContext<AgentMonitoringPageService>();

    private readonly IActiveRunQueryService _activeRunQuery;
    private readonly IAgentRegistryService _registry;
    private readonly JobDispatcherService _dispatcher;
    private readonly IOrchestratorRunService _runService;
    private readonly PipelineOrchestrationService _pipelineService;
    private readonly IConfigurationStore _configStore;
    private readonly IConsolidationService _consolidationService;
    private readonly IPendingWorkQuery _pendingWorkQuery;
    private readonly IWorkDistributor _workDistributor;
    private readonly IHubContext<AgentHub, IAgentHubClient> _hubContext;
    private readonly ILabelService _labelService;
    private readonly IPipelineRunHistoryService _historyService;
    private readonly IRunLifecycleManager _lifecycleManager;
    private readonly TimeProvider _clock;

    private Timer? _refreshTimer;
    private bool _disposed;

    public AgentMonitoringPageService(
        IActiveRunQueryService activeRunQuery,
        IAgentRegistryService registry,
        JobDispatcherService dispatcher,
        IOrchestratorRunService runService,
        PipelineOrchestrationService pipelineService,
        IConfigurationStore configStore,
        IConsolidationService consolidationService,
        IPendingWorkQuery pendingWorkQuery,
        IWorkDistributor workDistributor,
        IHubContext<AgentHub, IAgentHubClient> hubContext,
        ILabelService labelService,
        IPipelineRunHistoryService historyService,
        IRunLifecycleManager lifecycleManager,
        TimeProvider clock)
    {
        _activeRunQuery = activeRunQuery;
        _registry = registry;
        _dispatcher = dispatcher;
        _runService = runService;
        _pipelineService = pipelineService;
        _configStore = configStore;
        _consolidationService = consolidationService;
        _pendingWorkQuery = pendingWorkQuery;
        _workDistributor = workDistributor;
        _hubContext = hubContext;
        _labelService = labelService;
        _historyService = historyService;
        _lifecycleManager = lifecycleManager;
        _clock = clock;
    }

    // ── Events ──

    /// <summary>
    /// Raised after any data refresh (timer, event, or action-triggered).
    /// The component should call InvokeAsync(StateHasChanged) in its handler.
    /// </summary>
    public event Action? OnStateChanged;

    // ── State ──

    public IReadOnlyList<ActiveRunSummary> ActiveRuns { get; private set; } = [];
    public IReadOnlyList<AgentEntry> Agents { get; private set; } = [];
    public IReadOnlyList<PendingJob> QueuedJobs { get; private set; } = [];
    public IReadOnlyList<ConsolidationRun> ActiveConsolidationRuns { get; private set; } = [];
    public IReadOnlyList<ConsolidationRun> QueuedConsolidationRuns { get; private set; } = [];
    public IReadOnlyList<PipelineRunSummary> RunHistory { get; private set; } = [];
    // TODO: These should be IReadOnlyDictionary<> to prevent consumers from mutating contents
    public Dictionary<string, ProviderConfig> ProviderConfigLookup { get; private set; } = new();
    public Dictionary<string, AgentProfile> ProfileLookup { get; private set; } = new();
    public Dictionary<string, QualityGateConfiguration> QgcLookup { get; private set; } = new();
    public int MaxRetries { get; private set; } = 3;
    public DateTimeOffset LastSuccessfulRefresh { get; private set; }
    public bool LastRefreshFailed { get; private set; }

    /// <summary>
    /// Returns the current UTC time from the injected TimeProvider (testable).
    /// </summary>
    public DateTimeOffset GetUtcNow() => _clock.GetUtcNow();

    // ── Initialization ──

    public async Task InitializeAsync()
    {
        LastSuccessfulRefresh = _clock.GetUtcNow();
        _pipelineService.OnChange += HandleExternalStateChanged;
        _consolidationService.OnChange += HandleExternalStateChanged;

        _refreshTimer = new Timer(RefreshTick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

        try
        {
            var config = await _configStore.LoadPipelineConfigAsync(CancellationToken.None);
            MaxRetries = config.MaxRetries;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to load pipeline config, using defaults");
        }

        try
        {
            var allConfigs = new List<ProviderConfig>();
            foreach (var kind in Enum.GetValues<ProviderKind>())
                allConfigs.AddRange(await _configStore.LoadProviderConfigsAsync(kind, CancellationToken.None));
            ProviderConfigLookup = allConfigs.DistinctBy(c => c.Id).ToDictionary(c => c.Id);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to load provider configs");
        }

        try
        {
            var profiles = await _configStore.LoadAgentProfilesAsync(CancellationToken.None);
            ProfileLookup = profiles.ToDictionary(p => p.Id);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to load agent profiles");
        }

        try
        {
            var qgcs = await _configStore.LoadQualityGateConfigsAsync(CancellationToken.None);
            QgcLookup = qgcs.ToDictionary(q => q.Id);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to load quality gate configs");
        }

        await RefreshDataAsync(includeConsolidation: true);
    }

    // ── Data Refresh ──

    // TODO: Add a CancellationTokenSource (cancelled in Dispose) and propagate through RefreshDataAsync
    // to prevent in-flight async calls from setting state on a disposed instance after page navigation.
    public async Task RefreshDataAsync(bool includeConsolidation = false)
    {
        ActiveRuns = (await _activeRunQuery.GetActiveRunsAsync())
            .Where(r => !string.IsNullOrEmpty(r.AgentId))
            .ToList();
        Agents = _registry.GetAllAgents();
        RunHistory = await _pipelineService.GetRunHistoryAsync();
        var allQueuedJobs = await _pendingWorkQuery.GetPendingJobsAsync();
        var consolidationJobs = allQueuedJobs.Where(j => j.IsConsolidation).ToList();
        QueuedJobs = allQueuedJobs.Where(j => !j.IsConsolidation).ToList();

        if (consolidationJobs.Count > 0)
        {
            Logger.Debug("AgentMonitoring: {Total} pending jobs, {Consolidation} consolidation (filtered out), {Pipeline} pipeline shown. " +
                "Consolidation IDs: [{Ids}]",
                allQueuedJobs.Count, consolidationJobs.Count, QueuedJobs.Count,
                string.Join(", ", consolidationJobs.Select(j => $"{j.WorkItemId}(type={j.ConsolidationRunType})")));
        }

        if (includeConsolidation)
            await RefreshConsolidationAsync();
    }

    private async Task RefreshConsolidationAsync()
    {
        try
        {
            var allConsolidationRuns = await _consolidationService.GetRunHistoryAsync(CancellationToken.None);
            ActiveConsolidationRuns = allConsolidationRuns
                .Where(r => r.Status == ConsolidationRunStatus.Running)
                .ToList();
            QueuedConsolidationRuns = allConsolidationRuns
                .Where(r => r.Status == ConsolidationRunStatus.Queued)
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Failed to load consolidation runs for monitoring page");
        }
    }

    // ── Run Access (for modal rendering) ──

    /// <summary>
    /// Returns the full PipelineRun object for the given run ID (needed for modal detail rendering).
    /// </summary>
    public PipelineRun? GetFullRun(string runId) =>
        _pipelineService.GetAllActiveRuns().FirstOrDefault(r => r.RunId == runId);

    /// <summary>
    /// Returns the output buffer for the given run (needed for modal output panel).
    /// </summary>
    public OutputRingBuffer GetOutputBuffer(string runId) =>
        _runService.GetOutputBuffer(runId);

    /// <summary>
    /// Returns the agent entry for the given agent ID.
    /// </summary>
    public AgentEntry? GetAgentByAgentId(string agentId) =>
        _registry.GetByAgentId(agentId);

    // ── Cancellation ──

    public async Task CancelAgentRunByIdAsync(string runId)
    {
        var run = _pipelineService.GetAllActiveRuns().FirstOrDefault(r => r.RunId == runId);
        if (run != null)
        {
            await CancelAgentRunAsync(run);
            return;
        }

        // Run not in-memory (DB mode after restart) — cancel WorkItem directly
        try
        {
            var cancelled = await _workDistributor.CancelJobAsync(runId, CancellationToken.None);
            if (cancelled)
            {
                Logger.Information("Cancelled WorkItem {RunId} via IWorkDistributor", runId);
            }
            else
            {
                Logger.Information("WorkItem {RunId} could not be cancelled (already terminal or not found) — refreshing UI", runId);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to cancel WorkItem {RunId} via IWorkDistributor", runId);
        }

        await RefreshDataAsync();
        NotifyStateChanged();
    }

    public async Task CancelAgentRunAsync(PipelineRun run)
    {
        var agent = run.AgentId is not null ? _registry.GetByAgentId(run.AgentId) : null;

        // For connected, non-orphan agents: send cancel signal first
        // TODO: Race condition (matches original behavior): agent may still be running after CancelRunAsync
        // clears state, since agent hasn't acknowledged the cancel yet. Consider awaiting acknowledgment.
        if (agent is not null && agent.OrphanRestoredAt is null)
        {
            try { await _hubContext.Clients.Client(agent.ConnectionId).CancelJob(run.RunId); }
            catch (Exception ex) { Logger.Warning(ex, "Failed to send CancelJob to agent {AgentId}", run.AgentId); }
        }

        // Set FailureReason BEFORE CancelRunAsync (mutate-before-persist pattern)
        run.FailureReason = agent is null
            ? "Cancelled — agent not available"
            : agent.OrphanRestoredAt is not null
                ? "Cancelled — agent lost job state (container restart)"
                : "Cancelled by user";

        // Delegate full lifecycle (removes run, persists, clears agent, swaps label, deletes K8s job)
        var result = await _lifecycleManager.CancelRunAsync(run.RunId, CancellationToken.None);

        // Fallback: if run wasn't in memory (already removed by another path), cancel WorkItem in DB
        if (result is null)
        {
            try { await _workDistributor.CancelJobAsync(run.RunId, CancellationToken.None); }
            catch (Exception ex) { Logger.Warning(ex, "Failed to cancel WorkItem for run {RunId}", run.RunId); }
        }

        await RefreshDataAsync();
        NotifyStateChanged();
    }

    // ── Queue Management ──

    public async Task RemoveFromQueueAsync(string issueIdentifier, string issueProviderId)
    {
        // In DB/K8s mode, pending jobs are WorkItem rows — cancel via WorkDistributor.
        var job = QueuedJobs.FirstOrDefault(j => j.IssueIdentifier == issueIdentifier && j.IssueProviderId == issueProviderId);
        if (job?.WorkItemId is not null)
        {
            try
            {
                await _workDistributor.CancelJobAsync(job.WorkItemId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to cancel pending WorkItem {WorkItemId} for issue {IssueIdentifier}", job.WorkItemId, issueIdentifier);
            }
        }
        else
        {
            // Legacy in-memory mode fallback
            _dispatcher.RemoveFromQueue(issueIdentifier, issueProviderId);
        }

        await RefreshDataAsync();
        NotifyStateChanged();
    }

    public async Task CancelConsolidationRunAsync(string runId)
    {
        await _consolidationService.CancelQueuedRunAsync(runId, CancellationToken.None);
        await RefreshDataAsync(includeConsolidation: true);
        NotifyStateChanged();
    }

    // ── Agent Actions ──

    public void EnableAgent(AgentEntry agent) => agent.Disabled = false;

    public void DisableAgent(AgentEntry agent) => agent.Disabled = true;

    public async Task ForceDisconnectAsync(AgentEntry agent)
    {
        try
        {
            if (agent.Status != AgentStatus.Disconnected)
            {
                try { await _hubContext.Clients.Client(agent.ConnectionId).ForceDisconnect(); }
                catch (Exception ex) { Logger.Warning(ex, "Agent {AgentId} did not respond to force disconnect", agent.AgentId); }
            }

            if (agent.ActiveJobId != null)
            {
                var activeRun = _pipelineService.GetAllActiveRuns()
                    .FirstOrDefault(r => r.RunId == agent.ActiveJobId);
                if (activeRun != null)
                {
                    activeRun.FailureReason = "Force disconnected by operator";
                    activeRun.CurrentStep = PipelineStep.Failed;
                    activeRun.MarkCompleted();
                }
            }

            _registry.Deregister(agent.AgentId);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Force disconnect failed for agent {AgentId}", agent.AgentId);
        }
    }

    // ── Lookup Helpers ──

    public ProviderConfig? ResolveProvider(string? configId)
    {
        if (string.IsNullOrEmpty(configId)) return null;
        return ProviderConfigLookup.GetValueOrDefault(configId);
    }

    public string ResolveProfileName(string profileId)
    {
        var profile = ProfileLookup.GetValueOrDefault(profileId);
        return profile != null ? profile.DisplayName : $"{UiFormatters.Truncate(profileId, 8)} (deleted)";
    }

    public string ResolveQgcName(string qgcId)
    {
        var qgc = QgcLookup.GetValueOrDefault(qgcId);
        return qgc != null ? qgc.DisplayName : $"{UiFormatters.Truncate(qgcId, 8)} (deleted)";
    }

    // ── Timer / Event Handling ──

    // TODO: RefreshTick runs on a ThreadPool thread without synchronization. While reference
    // assignments are atomic, consider using a SemaphoreSlim to prevent overlapping refreshes.
    private async void RefreshTick(object? state)
    {
        if (_disposed) return;
        try
        {
            await RefreshDataAsync();
            LastSuccessfulRefresh = _clock.GetUtcNow();
            LastRefreshFailed = false;
            NotifyStateChanged();
        }
        catch (ObjectDisposedException) { }
        catch
        {
            LastRefreshFailed = true;
        }
    }

    private async void HandleExternalStateChanged()
    {
        if (_disposed) return;
        try
        {
            await RefreshDataAsync(includeConsolidation: true);
            LastSuccessfulRefresh = _clock.GetUtcNow();
            LastRefreshFailed = false;
            NotifyStateChanged();
        }
        catch (ObjectDisposedException) { }
        catch
        {
            LastRefreshFailed = true;
        }
    }

    private void NotifyStateChanged()
    {
        try { OnStateChanged?.Invoke(); }
        catch (Exception ex) { Logger.Warning(ex, "OnStateChanged handler threw an exception"); }
    }

    // ── Dispose ──

    public void Dispose()
    {
        _disposed = true;
        _refreshTimer?.Dispose();
        _pipelineService.OnChange -= HandleExternalStateChanged;
        _consolidationService.OnChange -= HandleExternalStateChanged;
    }
}
