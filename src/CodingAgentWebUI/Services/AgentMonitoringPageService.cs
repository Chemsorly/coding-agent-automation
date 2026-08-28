using CodingAgentWebUI.Api.Client;
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
/// Active runs are derived from run history via <see cref="IPipelineApiRunHistoryClient"/>
/// by filtering non-terminal <see cref="PipelineStep"/> values. Config and run history
/// are loaded via <see cref="IPipelineApiConfigClient"/> and <see cref="IPipelineApiRunHistoryClient"/>.
/// Cancel/disconnect operations route through <see cref="IWorkDistributor"/>.
/// </para>
/// </summary>
public class AgentMonitoringPageService
{
    private static readonly ILogger Logger = Log.ForContext<AgentMonitoringPageService>();

    /// <summary>
    /// Terminal steps that indicate a run is complete and should appear in history,
    /// not the active-runs table. Must be kept in sync with PipelineStep enum.
    /// </summary>
    private static readonly HashSet<PipelineStep> s_terminalSteps =
    [
        PipelineStep.Completed,
        PipelineStep.Failed,
        PipelineStep.Cancelled
    ];

    private readonly IAgentRegistryService _registry;
    private readonly IPipelineApiConfigClient _configClient;
    private readonly IConsolidationService _consolidationService;
    private readonly IPendingWorkQuery _pendingWorkQuery;
    private readonly IWorkDistributor _workDistributor;
    private readonly IPipelineApiRunHistoryClient _runHistoryClient;

    public AgentMonitoringPageService(AgentMonitoringPageServiceDependencies deps)
    {
        _registry = deps.Registry;
        _configClient = deps.ConfigClient;
        _consolidationService = deps.ConsolidationService;
        _pendingWorkQuery = deps.PendingWorkQuery;
        _workDistributor = deps.WorkDistributor;
        _runHistoryClient = deps.RunHistoryClient;
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
            var config = await _configClient.GetPipelineConfigAsync(CancellationToken.None);
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
                allConfigs.AddRange(await _configClient.GetProviderConfigsAsync(kind, CancellationToken.None));
            ProviderConfigLookup = allConfigs.DistinctBy(c => c.Id).ToDictionary(c => c.Id);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to load provider configs");
        }

        try
        {
            var profiles = await _configClient.GetAgentProfilesAsync(CancellationToken.None);
            ProfileLookup = profiles.ToDictionary(p => p.Id);
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to load agent profiles");
        }

        try
        {
            var qgcs = await _configClient.GetQualityGateConfigsAsync(CancellationToken.None);
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
        Agents = _registry.GetAllAgents();
        // Fetch run history via the Pipeline API.
        // Request a large page to approximate "all" for the monitoring view; paging is
        // supported by the underlying endpoint if larger datasets require it in future.
        var historyPage = await _runHistoryClient.GetRunHistoryAsync(page: 1, pageSize: 1000, includeActive: true, ct: CancellationToken.None);
        RunHistory = historyPage.Items;

        // Derive active runs from history by filtering non-terminal steps.
        // Terminal steps (Completed, Failed, Cancelled) appear in history only.
        // Non-terminal entries still in the API's run history represent dispatched/running jobs.
        // Filter to runs that have an AgentId (assigned to an agent), matching the original
        // IActiveRunQueryService behaviour in AgentMonitoringPageService.RefreshDataAsync.
        ActiveRuns = historyPage.Items
            .Where(r => !s_terminalSteps.Contains(r.FinalStep) && r.AgentId != null)
            .Select(MapToActiveRunSummary)
            .ToList();

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

        // After consolidation data is refreshed, augment Agents with any agents that are
        // handling active consolidation runs but might not yet appear in the registry snapshot.
        // This ensures Connected/Busy counters and the Registered Agents list include consolidation
        // agents, especially in split-process deployments where the snapshot may briefly lag.
        // TODO: AugmentAgentsWithConsolidationRunners is called unconditionally even when
        // includeConsolidation=false (e.g. cancel operations). When called without refreshing
        // consolidation data, ActiveConsolidationRuns holds stale values from the previous cycle.
        // A completed/cancelled run's agent could be re-added with a stale Busy status, inflating
        // counters. Guard this call with `if (includeConsolidation)` or clear ActiveConsolidationRuns
        // at the start of RefreshDataAsync to avoid reading stale data.
        AugmentAgentsWithConsolidationRunners();
    }

    /// <summary>
    /// Merges agents referenced by active consolidation runs into the <see cref="Agents"/> list.
    /// An agent handling a consolidation run is looked up by <see cref="ConsolidationRun.AgentId"/>
    /// and added to <see cref="Agents"/> if it is already in the registry but missing from the
    /// current snapshot (e.g., due to snapshot lag in <c>ApiAgentRegistryService</c>).
    /// This ensures the Connected/Busy counters reflect consolidation agents.
    /// </summary>
    private void AugmentAgentsWithConsolidationRunners()
    {
        if (ActiveConsolidationRuns.Count == 0) return;

        var existingAgentIds = new HashSet<string>(
            Agents.Select(a => a.AgentId.Value),
            StringComparer.Ordinal);

        List<AgentEntry>? extras = null;
        foreach (var run in ActiveConsolidationRuns)
        {
            if (run.AgentId is null) continue;
            if (existingAgentIds.Contains(run.AgentId)) continue;

            var entry = _registry.GetByAgentId(run.AgentId);
            if (entry is null) continue;

            extras ??= [];
            extras.Add(entry);
            existingAgentIds.Add(run.AgentId);
        }

        if (extras is { Count: > 0 })
            Agents = [.. Agents, .. extras];
    }

    /// <summary>
    /// Maps a non-terminal <see cref="PipelineRunSummary"/> to an <see cref="ActiveRunSummary"/>
    /// for display in the Active Runs table. FinalStep (the last recorded step) is used as
    /// CurrentStep — for in-flight runs the API stores the current step in FinalStep until
    /// the run reaches a terminal state.
    /// </summary>
    private static ActiveRunSummary MapToActiveRunSummary(PipelineRunSummary run) => new()
    {
        RunId = run.RunId,
        IssueIdentifier = run.IssueIdentifier,
        IssueTitle = run.IssueTitle,
        RunType = run.RunType,
        AgentId = run.AgentId != null ? (AgentId?)new AgentId(run.AgentId) : null,
        StartedAt = run.StartedAtOffset,
        ProjectName = run.ProjectName,
        CurrentStep = run.FinalStep
    };

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
    /// Cancels a run by ID via IWorkDistributor.
    /// IOrchestratorRunService is not available in the monolith.
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
    /// Cancels a run via IWorkDistributor. CancelJob hub message is not sent from the monolith —
    /// the agent hub lives in CodingAgentWebUI.Api.
    /// </summary>
    public async Task CancelAgentRunAsync(PipelineRun run)
    {
        // Cancel via WorkDistributor — no hub context in monolith. Agent receives cancellation via the API hub.
        Logger.Information("Cancel: run {RunId} — delegating to IWorkDistributor", run.RunId);
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
            // Pending jobs are WorkItem rows in K8s mode, so a queued job without a WorkItemId
            // should not occur. The in-memory queue this used to fall back to has been removed —
            // deduplication and queueing are owned by the WorkItems table.
            Logger.Warning(
                "Cannot cancel pending job for issue {IssueIdentifier} (provider {IssueProviderId}) — no WorkItem row found",
                issueIdentifier, issueProviderId);
        }

        await RefreshDataAsync();
    }

    public async Task CancelConsolidationRunAsync(string runId)
    {
        await _consolidationService.CancelQueuedRunAsync(runId, CancellationToken.None);
        await RefreshDataAsync(includeConsolidation: true);
    }

    /// <summary>
    /// Deregisters the agent from the local registry. ForceDisconnect hub message is not sent
    /// from the monolith — the agent hub lives in CodingAgentWebUI.Api.
    /// </summary>
    public Task ForceDisconnectAsync(AgentEntry agent)
    {
        try
        {
            // No hub context in monolith — ForceDisconnect signal not sent. Deregistering from local registry.
            Logger.Information("ForceDisconnect: deregistering agent {AgentId} from local registry", agent.AgentId);
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
