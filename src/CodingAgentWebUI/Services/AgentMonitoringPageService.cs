using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.AspNetCore.SignalR;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Encapsulates the business logic for the AgentMonitoring page — data refresh, cancellation
/// orchestration, and state management. The Blazor component delegates to this service
/// and retains only UI state (modals, timers, JS interop, StateHasChanged).
/// Registered as Scoped because it holds per-page mutable state.
/// </summary>
public class AgentMonitoringPageService
{
    private static readonly ILogger Logger = Log.ForContext<AgentMonitoringPageService>();

    private readonly IActiveRunQueryService _activeRunQuery;
    private readonly IAgentRegistryService _registry;
    private readonly JobDeduplicationGuardService _dispatcher;
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

    public AgentMonitoringPageService(
        IActiveRunQueryService activeRunQuery,
        IAgentRegistryService registry,
        JobDeduplicationGuardService dispatcher,
        IOrchestratorRunService runService,
        PipelineOrchestrationService pipelineService,
        IConfigurationStore configStore,
        IConsolidationService consolidationService,
        IPendingWorkQuery pendingWorkQuery,
        IWorkDistributor workDistributor,
        IHubContext<AgentHub, IAgentHubClient> hubContext,
        ILabelService labelService,
        IPipelineRunHistoryService historyService,
        IRunLifecycleManager lifecycleManager)
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
    }

    // ── State ──

    public IReadOnlyList<ActiveRunSummary> ActiveRuns { get; private set; } = [];
    public IReadOnlyList<AgentEntry> Agents { get; private set; } = [];
    public IReadOnlyList<PendingJob> QueuedJobs { get; private set; } = [];
    public IReadOnlyList<ConsolidationRun> ActiveConsolidationRuns { get; private set; } = [];
    public IReadOnlyList<ConsolidationRun> QueuedConsolidationRuns { get; private set; } = [];
    // TODO: Expose as IReadOnlyDictionary<string, T> to prevent consumers from mutating service state.
    public Dictionary<string, ProviderConfig> ProviderConfigLookup { get; private set; } = new();
    public Dictionary<string, AgentProfile> ProfileLookup { get; private set; } = new();
    public Dictionary<string, QualityGateConfiguration> QgcLookup { get; private set; } = new();
    public IReadOnlyList<PipelineRunSummary> RunHistory { get; private set; } = [];
    public int MaxRetries { get; private set; } = 3;

    // ── Initialization ──

    public async Task InitializeAsync()
    {
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

    public async Task RefreshConsolidationAsync()
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

    // ── Orchestration Methods ──

    public async Task CancelAgentRunByIdAsync(string runId)
    {
        // TODO: Original code used PipelineService.GetAllActiveRuns() which also includes the legacy
        // PipelineRunLifecycleService.ActiveRun. Using _runService.GetRun only checks OrchestratorRunService.
        // Align if legacy ActiveRun is ever used in production.
        var run = _runService.GetRun(runId);
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
                // WorkItem might already be terminal (Failed/Succeeded) — just log
                Logger.Information("WorkItem {RunId} could not be cancelled (already terminal or not found) — refreshing UI", runId);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to cancel WorkItem {RunId} via IWorkDistributor", runId);
        }

        await RefreshDataAsync();
    }

    public async Task CancelAgentRunAsync(PipelineRun run)
    {
        var agent = run.AgentId is not null ? _registry.GetByAgentId(run.AgentId) : null;

        // If agent not found (disconnected, never registered, or null),
        // delegate to lifecycle manager — there's no agent to send CancelJob to.
        if (agent == null)
        {
            Logger.Information("Cancel: agent '{AgentId}' not found for run {RunId}, delegating to lifecycle manager", run.AgentId, run.RunId);
            await _lifecycleManager.CancelRunAsync(run.RunId, CancellationToken.None, "Cancelled — agent not available");
            await RefreshDataAsync();
            return;
        }

        // Short-circuit: if the run is orphan-restored (agent crashed and lost its state),
        // the agent can't act on CancelJob — delegate to lifecycle manager.
        if (agent.OrphanRestoredAt is not null)
        {
            Logger.Information("Cancel short-circuit: run {RunId} is orphan-restored, delegating to lifecycle manager", run.RunId);
            await _lifecycleManager.CancelRunAsync(run.RunId, CancellationToken.None, "Cancelled — agent lost job state (container restart)");
            await RefreshDataAsync();
            return;
        }

        try
        {
            await _hubContext.Clients.Client(agent.ConnectionId).CancelJob(run.RunId);
        }
        catch (Exception ex) { Logger.Warning(ex, "Failed to send CancelJob to agent {AgentId}", run.AgentId); }

        // Delegate to lifecycle manager with failure reason
        await _lifecycleManager.CancelRunAsync(run.RunId, CancellationToken.None, "Cancelled by user");

        await RefreshDataAsync();
    }

    public async Task RemoveFromQueueAsync(string issueIdentifier, string issueProviderId)
    {
        // In DB/K8s mode, pending jobs are WorkItem rows — cancel via WorkDistributor.
        // Find the WorkItemId from the cached queue data.
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
    }

    public async Task CancelConsolidationRunAsync(string runId)
    {
        await _consolidationService.CancelQueuedRunAsync(runId, CancellationToken.None);
        await RefreshDataAsync(includeConsolidation: true);
    }

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
                    // TODO: Original code only set CompletedAt (not CompletedAtOffset). MarkCompleted() now sets both — verify no downstream logic relies on CompletedAtOffset==null for force-disconnected runs.
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

    public void EnableAgent(AgentEntry agent) => agent.Disabled = false;

    public void DisableAgent(AgentEntry agent) => agent.Disabled = true;

    // ── Resolvers ──

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
}
