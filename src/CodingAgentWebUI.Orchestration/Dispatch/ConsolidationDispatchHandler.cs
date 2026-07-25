using System.Text.Json;
using System.Threading.RateLimiting;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Orchestration.Telemetry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// BackgroundService that polls for consolidation WorkItems (TaskType=Consolidation) and dispatches
/// them as K8s Jobs. Runs under leader election (same Lease as DispatchService).
/// Extracted from DispatchService to separate consolidation-specific concerns (run status transitions,
/// provider config resolution, cascade failure) from regular issue dispatch.
/// </summary>
internal sealed class ConsolidationDispatchHandler : LeaderElectedPollingService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ConsolidationDispatchHandler>();

    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly DispatchLifecycleService _lifecycle;
    private readonly JobTemplateStore _templateProvider;
    private readonly DispatchTemplateResolver _templateResolver;
    private readonly DispatchServiceOptions _options;
    private readonly WorkItemTransitionService _transitionService;
    private readonly IConsolidationRunStore? _consolidationRunStore;
    private readonly IConsolidationService? _consolidationService;
    private readonly IConsolidationJobPreparationService? _consolidationJobPreparer;
    private readonly IPipelineConfigStore? _pipelineConfigStore;
    private readonly IProjectStore? _projectStore;
    private readonly TokenBucketRateLimiter _rateLimiter;
    private readonly DispatchStateBuilder _stateBuilder;

    protected override string ServiceName => "ConsolidationDispatchHandler";
    protected override int PollIntervalSeconds => _options.PollIntervalSeconds;

    public ConsolidationDispatchHandler(
        IDbContextFactory<PipelineDbContext> dbFactory,
        ILeaderElectionService leaderElection,
        DispatchLifecycleService lifecycle,
        JobTemplateStore templateProvider,
        IConfiguration configuration,
        WorkItemTransitionService transitionService,
        IConsolidationRunStore? consolidationRunStore = null,
        IConsolidationService? consolidationService = null,
        IConsolidationJobPreparationService? consolidationJobPreparer = null,
        IPipelineConfigStore? pipelineConfigStore = null,
        IProjectStore? projectStore = null,
        IAgentProfileStore? agentProfileStore = null)
        : base(leaderElection)
    {
        _dbFactory = dbFactory;
        _lifecycle = lifecycle;
        _templateProvider = templateProvider;
        _transitionService = transitionService;
        _consolidationRunStore = consolidationRunStore;
        _consolidationService = consolidationService;
        _consolidationJobPreparer = consolidationJobPreparer;
        _pipelineConfigStore = pipelineConfigStore;
        _projectStore = projectStore;
        _templateResolver = new DispatchTemplateResolver(agentProfileStore, templateProvider);
        _options = DispatchServiceOptionsFactory.Create(configuration);
        _rateLimiter = _options.CreateRateLimiter();
        _stateBuilder = new DispatchStateBuilder(dbFactory, lifecycle, templateProvider, _templateResolver, _options);
    }

    /// <summary>
    /// Test constructor accepting pre-built options (skips IConfiguration binding).
    /// </summary>
    internal ConsolidationDispatchHandler(
        IDbContextFactory<PipelineDbContext> dbFactory,
        ILeaderElectionService leaderElection,
        DispatchLifecycleService lifecycle,
        JobTemplateStore templateProvider,
        DispatchServiceOptions options,
        WorkItemTransitionService transitionService,
        IConsolidationRunStore? consolidationRunStore = null,
        IConsolidationService? consolidationService = null,
        IConsolidationJobPreparationService? consolidationJobPreparer = null,
        IPipelineConfigStore? pipelineConfigStore = null,
        IProjectStore? projectStore = null,
        IAgentProfileStore? agentProfileStore = null)
        : base(leaderElection)
    {
        _dbFactory = dbFactory;
        _lifecycle = lifecycle;
        _templateProvider = templateProvider;
        _transitionService = transitionService;
        _consolidationRunStore = consolidationRunStore;
        _consolidationService = consolidationService;
        _consolidationJobPreparer = consolidationJobPreparer;
        _pipelineConfigStore = pipelineConfigStore;
        _projectStore = projectStore;
        _templateResolver = new DispatchTemplateResolver(agentProfileStore, templateProvider);
        _options = options;
        _rateLimiter = _options.CreateRateLimiter();
        _stateBuilder = new DispatchStateBuilder(dbFactory, lifecycle, templateProvider, _templateResolver, _options);
    }

    protected override Task OnPollCycleAsync(CancellationToken ct) => PollAndDispatchConsolidationAsync(ct);

    /// <inheritdoc/>
    public override void Dispose()
    {
        _rateLimiter.Dispose();
        base.Dispose();
    }

    internal async Task PollAndDispatchConsolidationAsync(CancellationToken ct)
    {
        var state = await _stateBuilder.BuildStateAsync(
            w => w.TaskType == WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            ct);

        if (state is null)
            return;

        await using (state.Db)
        {
            await foreach (var candidate in _stateBuilder.GetEligibleCandidatesAsync(
                state, LeaderElection, _rateLimiter, nameof(ConsolidationDispatchHandler),
                async (item, errorMessage, innerCt) =>
                {
                    await FailConsolidationWorkItemAsync(item.Id, errorMessage, item.IssueIdentifier, innerCt);
                },
                ct))
            {
                await DispatchConsolidationItemAsync(state.Db, candidate.Item, candidate.Template,
                    candidate.IsKiroAgent, state.AvailablePvcs, state.ConcurrencyBySelector, ct);
            }
        }
    }

    // ── Consolidation-specific dispatch ─────────────────────────────────

    private async Task DispatchConsolidationItemAsync(
        PipelineDbContext db,
        PendingWorkItemProjection item,
        JobTemplate template,
        bool isKiroAgent,
        List<string> availablePvcs,
        Dictionary<string, int> concurrencyBySelector,
        CancellationToken ct)
    {
        // Cancel-during-dispatch race guard: check if run was cancelled while queued
        if (_consolidationRunStore is not null && !string.IsNullOrEmpty(item.IssueIdentifier))
        {
            var runId = item.IssueIdentifier;
            var consolidationRun = await _consolidationRunStore.GetByIdAsync(runId, ct);
            if (consolidationRun is not null &&
                (consolidationRun.Status == ConsolidationRunStatus.Cancelled ||
                 consolidationRun.Status == ConsolidationRunStatus.Failed))
            {
                Log.Information(
                    "ConsolidationDispatchHandler: consolidation run {RunId} is {Status}, skipping dispatch for WorkItem {WorkItemId}",
                    runId, consolidationRun.Status, item.Id);
                await _transitionService.TransitionAsync(
                    item.Id, WorkItemStatus.Cancelled,
                    entity => entity.CompletedAt = DateTimeOffset.UtcNow, ct);
                return;
            }
        }

        // Capture updatedRequest outside the delegate so onDispatchSuccess can reference it
        JobDistributionRequest? updatedRequest = null;

        await _lifecycle.ExecuteDispatchLifecycleAsync(db, item, template, isKiroAgent, availablePvcs, concurrencyBySelector, "consolidation ",
            async workItem =>
            {
                // Deserialize payload to extract consolidation fields
                JobDistributionRequest? request = null;
                if (!string.IsNullOrEmpty(workItem.Payload))
                {
                    try
                    {
                        request = JsonSerializer.Deserialize<JobDistributionRequest>(workItem.Payload, PipelineJsonOptions.Default);
                    }
                    catch (JsonException ex)
                    {
                        Log.Warning(ex, "ConsolidationDispatchHandler: failed to deserialize consolidation WorkItem {WorkItemId} payload", item.Id);
                    }
                }

                if (request is null)
                {
                    await FailConsolidationWorkItemAsync(item.Id, "Consolidation WorkItem has no valid payload", item.IssueIdentifier, ct);
                    return (false, null);
                }

                // Build provider configs and vend tokens at dispatch time
                IReadOnlyList<ProviderConfig>? vendedConfigs = null;
                string repoProviderId = "";
                PipelineConfiguration? pipelineConfig = null;

                try
                {
                    // Parse agent labels from selector string
                    var agentLabels = (item.AgentSelector ?? "")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList()
                        .AsReadOnly();

                    // Delegate config resolution and token vending to shared preparer
                    if (_consolidationJobPreparer is null)
                    {
                        Log.Error("ConsolidationDispatchHandler: IConsolidationJobPreparationService not available for consolidation WorkItem {WorkItemId}", item.Id);
                        await FailConsolidationWorkItemAsync(item.Id, "IConsolidationJobPreparationService not registered", item.IssueIdentifier, ct);
                        return (false, null);
                    }

                    var preparation = await _consolidationJobPreparer.PrepareAsync(
                        request.ConsolidationRunType ?? ConsolidationRunType.BrainConsolidation,
                        request.ConsolidationTemplateId,
                        agentLabels,
                        ct);
                    vendedConfigs = preparation.ProviderConfigs;
                    repoProviderId = preparation.RepoProviderConfigId;

                    // Load pipeline configuration for the agent
                    if (_pipelineConfigStore is not null)
                    {
                        pipelineConfig = await _pipelineConfigStore.LoadPipelineConfigAsync(ct);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "ConsolidationDispatchHandler: failed to resolve provider configs for consolidation WorkItem {WorkItemId}", item.Id);
                    await FailConsolidationWorkItemAsync(item.Id, $"Provider config resolution failed: {ex.Message}", item.IssueIdentifier, ct);
                    return (false, null);
                }

                // Update payload with resolved configs
                updatedRequest = request with
                {
                    ProviderConfigs = vendedConfigs ?? [],
                    RepoProviderConfigId = repoProviderId,
                    PipelineConfiguration = pipelineConfig ?? new PipelineConfiguration()
                };
                workItem.Payload = JsonSerializer.Serialize(updatedRequest, PipelineJsonOptions.Default);

                // Load project secrets if project has them (resolve project from template if needed)
                Dictionary<string, string>? projectSecrets = null;
                string? resolvedProjectId = item.ProjectId;
                if (string.IsNullOrEmpty(resolvedProjectId) && _projectStore is not null
                    && !string.IsNullOrEmpty(request.ConsolidationTemplateId))
                {
                    var projects = await _projectStore.LoadProjectsAsync(ct);
                    if (projects is not null)
                    {
                        var ownerProject = projects.FirstOrDefault(p =>
                            p.Enabled && p.TemplateIds.Contains(request.ConsolidationTemplateId));
                        resolvedProjectId = ownerProject?.Id;
                    }
                }

                if (!string.IsNullOrEmpty(resolvedProjectId))
                {
                    projectSecrets = await _lifecycle.LoadProjectSecretsAsync(db, resolvedProjectId, ct);
                }

                return (true, projectSecrets);
            },
            async _ =>
            {
                // Transition ConsolidationRunStatus: Queued → Running (best-effort, after successful dispatch)
                if (updatedRequest is not null)
                    await TransitionConsolidationRunToRunningAsync(updatedRequest, ct);
            },
            ct,
            onFailure: async (_, errorMessage) =>
            {
                // Cascade failure to ConsolidationRun when K8s Job creation fails
                if (item.IssueIdentifier is not null)
                    await CascadeFailureAsync(item.IssueIdentifier, errorMessage, ct);
            });
    }

    // ── Failure Handling ────────────────────────────────────────────────

    /// <summary>
    /// Fails a consolidation work item and cascades the failure to the ConsolidationRun.
    /// </summary>
    private async Task FailConsolidationWorkItemAsync(
        Guid workItemId, string errorMessage, string? issueIdentifier, CancellationToken ct)
    {
        await _lifecycle.FailWorkItemAsync(workItemId, errorMessage, ct);

        // Cascade to ConsolidationRun: transition to Failed so it doesn't stay stuck in Queued/Running
        if (issueIdentifier is not null)
            await CascadeFailureAsync(issueIdentifier, errorMessage, ct);
    }

    /// <summary>
    /// Cascades a failure to a ConsolidationRun, transitioning it from Queued/Running to Failed.
    /// Delegates to <see cref="IConsolidationService.UpdateRunAsync"/> which handles cache invalidation,
    /// _runningRuns cleanup, OnChange event, and workspace management.
    /// Falls back to direct store write if IConsolidationService is unavailable.
    /// Safe to call from any failure path.
    /// </summary>
    internal async Task CascadeFailureAsync(string runId, string errorMessage, CancellationToken ct)
    {
        if (_consolidationService is not null)
        {
            try
            {
                await _consolidationService.UpdateRunAsync(
                    runId,
                    ConsolidationRunStatus.Failed,
                    $"WorkItem dispatch failed: {errorMessage}",
                    ct);
                Log.Information("ConsolidationDispatchHandler: cascaded failure to ConsolidationRun {RunId} via IConsolidationService", runId);
            }
            catch (OperationCanceledException)
            {
                Log.Debug("ConsolidationDispatchHandler: cascade to ConsolidationRun {RunId} cancelled (shutdown)", runId);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ConsolidationDispatchHandler: failed to cascade failure to ConsolidationRun {RunId} (non-fatal)", runId);
            }
            return;
        }

        // Fallback: direct store write when IConsolidationService not available
        Log.Warning("ConsolidationDispatchHandler: IConsolidationService unavailable, using direct store fallback for ConsolidationRun {RunId}. " +
            "This skips cache invalidation and OnChange events.", runId);

        if (_consolidationRunStore is null)
            return;

        try
        {
            var run = await _consolidationRunStore.GetByIdAsync(runId, ct);
            if (run is not null && run.Status is ConsolidationRunStatus.Queued or ConsolidationRunStatus.Running)
            {
                run.Status = ConsolidationRunStatus.Failed;
                run.Summary = $"WorkItem dispatch failed: {errorMessage}";
                run.CompletedAtUtc = DateTimeOffset.UtcNow;
                await _consolidationRunStore.SaveRunAsync(run, ct);
                Log.Information("ConsolidationDispatchHandler: cascaded failure to ConsolidationRun {RunId} (direct store)", runId);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Debug("ConsolidationDispatchHandler: cascade to ConsolidationRun {RunId} cancelled during shutdown (fallback path)", runId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ConsolidationDispatchHandler: failed to cascade failure to ConsolidationRun {RunId} (non-fatal)", runId);
        }
    }

    // ── Run Status Transitions ──────────────────────────────────────────

    private async Task TransitionConsolidationRunToRunningAsync(JobDistributionRequest request, CancellationToken ct)
    {
        var runId = request.RunId ?? request.IssueIdentifier;
        if (string.IsNullOrEmpty(runId))
            return;

        if (_consolidationService is not null)
        {
            try
            {
                await _consolidationService.TransitionToRunningAsync(runId, ct);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ConsolidationDispatchHandler: failed to transition consolidation run {RunId} to Running (non-fatal)", runId);
            }
            return;
        }

        // Fallback: direct store write when IConsolidationService not available
        if (_consolidationRunStore is null)
            return;

        try
        {
            var run = await _consolidationRunStore.GetByIdAsync(runId, ct);
            if (run is not null && run.Status == ConsolidationRunStatus.Queued)
            {
                run.Status = ConsolidationRunStatus.Running;
                run.StartedAtUtc = DateTimeOffset.UtcNow;
                await _consolidationRunStore.SaveRunAsync(run, ct);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ConsolidationDispatchHandler: failed to transition consolidation run {RunId} to Running (non-fatal)", runId);
        }
    }
}
