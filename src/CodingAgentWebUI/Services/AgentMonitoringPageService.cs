using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Encapsulates the business logic for the AgentMonitoring page — data refresh, cancellation
/// orchestration, and state management. The Blazor component delegates to this service
/// and retains only UI state (modals, timers, JS interop, StateHasChanged).
/// Registered as Scoped because it holds per-page mutable state.
/// <para>
/// Spec 044: Operates in degraded (history-only) mode. The monolith no longer owns
/// in-memory run state — IOrchestratorRunService, IRunLifecycleManager, and IHubContext
/// have been removed. Cancel/disconnect operations route through IWorkDistributor.
/// Full live streaming is restored in Spec 045.
/// </para>
/// </summary>
public class AgentMonitoringPageService
{
    private static readonly ILogger Logger = Log.ForContext<AgentMonitoringPageService>();

    private readonly IActiveRunQueryService _activeRunQuery;
    private readonly IAgentRegistryService _registry;
    private readonly JobDeduplicationGuardService _dispatcher;
    private readonly IConfigurationStore _configStore;
    private readonly IConsolidationService _consolidationService;
    private readonly IPendingWorkQuery _pendingWorkQuery;
    private readonly IWorkDistributor _workDistributor;
    private readonly IPipelineRunHistoryService _historyService;

    public AgentMonitoringPageService(AgentMonitoringPageServiceDependencies deps)
    {
        _activeRunQuery = deps.ActiveRunQuery;
        _registry = deps.Registry;
        _dispatcher = deps.Dispatcher;
        _configStore = deps.ConfigStore;
        _consolidationService = deps.ConsolidationService;
        _pendingWorkQuery = deps.PendingWorkQuery;
        _workDistributor = deps.WorkDistributor;
        _historyService = deps.HistoryService;
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
            .Where(r => r.AgentId.HasValue)
            .ToList();
        Agents = _registry.GetAllAgents();
        RunHistory = await _historyService.GetRunHistoryAsync();
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
            var allConsolidationRuns = await _consolidationService.GetRunHistoryAsync(CancellationToken.None) ?? [];
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

    /// <summary>
    /// Spec 044 (degraded mode): routes all cancel requests through IWorkDistributor.
    /// IOrchestratorRunService and IRunLifecycleManager are no longer in the monolith.
    /// </summary>
    public async Task CancelAgentRunByIdAsync(string runId)
    {
        try
        {
            var cancelled = await _workDistributor.CancelJobAsync(runId, CancellationToken.None);
            if (cancelled)
                Logger.Information("Cancelled WorkItem {RunId} via IWorkDistributor", runId);
            else
                Logger.Information("WorkItem {RunId} could not be cancelled (already terminal or not found) — refreshing UI", runId);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to cancel WorkItem {RunId} via IWorkDistributor", runId);
        }

        await RefreshDataAsync();
    }

    /// <summary>
    /// Spec 044 (degraded mode): CancelJob hub message cannot be sent (hub is in the API).
    /// Cancels via WorkDistributor only.
    /// </summary>
    public async Task CancelAgentRunAsync(PipelineRun run)
    {
        // Spec 044: no hub context available in the monolith — cancel via WorkDistributor.
        // CancelJob signal to the agent is not sent here; the agent will be notified via
        // the API hub once 045 restores live streaming.
        Logger.Information("Cancel (degraded mode): run {RunId} — delegating to IWorkDistributor", run.RunId);
        try
        {
            await _workDistributor.CancelJobAsync(run.RunId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to cancel WorkItem {RunId} via IWorkDistributor", run.RunId);
        }

        await RefreshDataAsync();
    }

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
            _dispatcher.RemoveFromQueue(issueIdentifier, issueProviderId);
        }

        await RefreshDataAsync();
    }

    public async Task CancelConsolidationRunAsync(string runId)
    {
        await _consolidationService.CancelQueuedRunAsync(runId, CancellationToken.None);
        await RefreshDataAsync(includeConsolidation: true);
    }

    /// <summary>
    /// Spec 044 (degraded mode): ForceDisconnect hub message cannot be sent (hub is in the API).
    /// Deregisters the agent from the local registry only.
    /// </summary>
    public Task ForceDisconnectAsync(AgentEntry agent)
    {
        try
        {
            // Spec 044: no hub context available — skip ForceDisconnect signal.
            // The agent will be swept by HeartbeatMonitorService in the API.
            Logger.Information("ForceDisconnect (degraded mode): deregistering agent {AgentId} from local registry", agent.AgentId);
            _registry.Deregister(agent.AgentId);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Force disconnect failed for agent {AgentId}", agent.AgentId);
        }
        return Task.CompletedTask;
    }

    public static void EnableAgent(AgentEntry agent) => agent.Disabled = false;

    public static void DisableAgent(AgentEntry agent) => agent.Disabled = true;

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
