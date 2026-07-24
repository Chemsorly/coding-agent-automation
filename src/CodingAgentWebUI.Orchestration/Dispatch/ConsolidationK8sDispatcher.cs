using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;
using static CodingAgentWebUI.Orchestration.Dispatch.DispatchService;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Handles K8s dispatch of consolidation WorkItems. Extracted from DispatchService
/// to separate consolidation-specific concerns (run status transitions, provider config
/// resolution, cascade failure) from the shared dispatch lifecycle.
/// </summary>
internal sealed class ConsolidationK8sDispatcher
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ConsolidationK8sDispatcher>();

    private readonly WorkItemTransitionService _transitionService;
    private readonly IConsolidationRunStore? _consolidationRunStore;
    private readonly IConsolidationService? _consolidationService;
    private readonly IConsolidationJobPreparationService? _consolidationJobPreparer;
    private readonly IPipelineConfigStore? _pipelineConfigStore;
    private readonly IProjectStore? _projectStore;

    public ConsolidationK8sDispatcher(
        WorkItemTransitionService transitionService,
        IConsolidationRunStore? consolidationRunStore = null,
        IConsolidationService? consolidationService = null,
        IConsolidationJobPreparationService? consolidationJobPreparer = null,
        IPipelineConfigStore? pipelineConfigStore = null,
        IProjectStore? projectStore = null)
    {
        _transitionService = transitionService;
        _consolidationRunStore = consolidationRunStore;
        _consolidationService = consolidationService;
        _consolidationJobPreparer = consolidationJobPreparer;
        _pipelineConfigStore = pipelineConfigStore;
        _projectStore = projectStore;
    }

    // ── Delegate types for shared lifecycle access ──────────────────────

    public delegate Task ExecuteLifecycleDelegate(
        PipelineDbContext db,
        PendingWorkItemProjection item,
        JobTemplate template,
        bool isKiroAgent,
        List<string> availablePvcs,
        Dictionary<string, int> concurrencyBySelector,
        string logPrefix,
        Func<WorkItemEntity, Task<(bool shouldContinue, Dictionary<string, string>? projectSecrets)>> prepareVariant,
        Func<WorkItemEntity, Task>? onDispatchSuccess,
        CancellationToken ct);

    public delegate Task FailWorkItemDelegate(
        Guid workItemId, string errorMessage, WorkItemTaskType taskType,
        string? issueIdentifier, CancellationToken ct);

    public delegate Task<Dictionary<string, string>?> LoadProjectSecretsDelegate(
        PipelineDbContext db, string projectId, CancellationToken ct);

    // ── Main dispatch method ────────────────────────────────────────────

    /// <summary>
    /// Dispatches a consolidation WorkItem as a K8s Job.
    /// Builds provider configs from scratch (not present in payload), vends tokens,
    /// updates payload, creates K8s Job, transitions ConsolidationRunStatus.
    /// </summary>
    public async Task DispatchAsync(
        PipelineDbContext db,
        PendingWorkItemProjection item,
        JobTemplate template,
        bool isKiroAgent,
        List<string> availablePvcs,
        Dictionary<string, int> concurrencyBySelector,
        ExecuteLifecycleDelegate executeLifecycle,
        FailWorkItemDelegate failWorkItem,
        LoadProjectSecretsDelegate loadProjectSecrets,
        CancellationToken ct)
    {
        // Cancel-during-dispatch race guard: check if run was cancelled while queued
        // TODO: Consider also cancelling when consolidationRun is null (record not found/purged),
        // aligning with PendingWorkItemDrainService (line 183) which uses `consolidationRun is null ||`.
        // Current behavior: missing record allows dispatch to proceed (defensive for K8s timing issues).
        if (_consolidationRunStore is not null && !string.IsNullOrEmpty(item.IssueIdentifier))
        {
            var runId = item.IssueIdentifier;
            var consolidationRun = await _consolidationRunStore.GetByIdAsync(runId, ct);
            if (consolidationRun is not null &&
                (consolidationRun.Status == ConsolidationRunStatus.Cancelled ||
                 consolidationRun.Status == ConsolidationRunStatus.Failed))
            {
                Log.Information(
                    "ConsolidationK8sDispatcher: consolidation run {RunId} is {Status}, skipping dispatch for WorkItem {WorkItemId}",
                    runId, consolidationRun.Status, item.Id);
                await _transitionService.TransitionAsync(
                    item.Id, WorkItemStatus.Cancelled,
                    entity => entity.CompletedAt = DateTimeOffset.UtcNow, ct);
                return;
            }
        }

        // Capture updatedRequest outside the delegate so onDispatchSuccess can reference it
        JobDistributionRequest? updatedRequest = null;

        await executeLifecycle(db, item, template, isKiroAgent, availablePvcs, concurrencyBySelector, "consolidation ",
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
                        Log.Warning(ex, "ConsolidationK8sDispatcher: failed to deserialize consolidation WorkItem {WorkItemId} payload", item.Id);
                    }
                }

                if (request is null)
                {
                    await failWorkItem(item.Id, "Consolidation WorkItem has no valid payload", item.TaskType, item.IssueIdentifier, ct);
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
                        Log.Error("ConsolidationK8sDispatcher: IConsolidationJobPreparationService not available for consolidation WorkItem {WorkItemId}", item.Id);
                        await failWorkItem(item.Id, "IConsolidationJobPreparationService not registered", item.TaskType, item.IssueIdentifier, ct);
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
                    Log.Error(ex, "ConsolidationK8sDispatcher: failed to resolve provider configs for consolidation WorkItem {WorkItemId}", item.Id);
                    await failWorkItem(item.Id, $"Provider config resolution failed: {ex.Message}", item.TaskType, item.IssueIdentifier, ct);
                    return (false, null);
                }

                // Update payload with resolved configs so GET /api/work-items/{id}/assignment returns complete data
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
                    projectSecrets = await loadProjectSecrets(db, resolvedProjectId, ct);
                }

                return (true, projectSecrets);
            },
            async _ =>
            {
                // Transition ConsolidationRunStatus: Queued → Running (best-effort, after successful dispatch)
                if (updatedRequest is not null)
                    await TransitionConsolidationRunToRunningAsync(updatedRequest, ct);
            },
            ct);
    }

    // ── Cascade Failure ─────────────────────────────────────────────────

    /// <summary>
    /// Cascades a failure to a ConsolidationRun, transitioning it from Queued/Running to Failed.
    /// Delegates to <see cref="IConsolidationService.UpdateRunAsync"/> which handles cache invalidation,
    /// _runningRuns cleanup, OnChange event, and workspace management.
    /// Falls back to direct store write if IConsolidationService is unavailable.
    /// Safe to call from any failure path (DispatchService, ReconciliationService).
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
                Log.Information("ConsolidationK8sDispatcher: cascaded failure to ConsolidationRun {RunId} via IConsolidationService", runId);
            }
            catch (OperationCanceledException)
            {
                // Graceful shutdown — cascade skipped, ReconciliationService or startup cleanup will handle it
                Log.Debug("ConsolidationK8sDispatcher: cascade to ConsolidationRun {RunId} cancelled (shutdown)", runId);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ConsolidationK8sDispatcher: failed to cascade failure to ConsolidationRun {RunId} (non-fatal)", runId);
            }
            return;
        }

        // Fallback: direct store write — skips _runningRuns cleanup, OnChange, workspace cleanup.
        // Only executes in test scenarios or misconfigured DI. Log at Warning for visibility.
        Log.Warning("ConsolidationK8sDispatcher: IConsolidationService unavailable, using direct store fallback for ConsolidationRun {RunId}. " +
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
                Log.Information("ConsolidationK8sDispatcher: cascaded failure to ConsolidationRun {RunId} (direct store)", runId);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Debug("ConsolidationK8sDispatcher: cascade to ConsolidationRun {RunId} cancelled during shutdown (fallback path)", runId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ConsolidationK8sDispatcher: failed to cascade failure to ConsolidationRun {RunId} (non-fatal)", runId);
        }
    }

    // ── Run Status Transitions ──────────────────────────────────────────

    /// <summary>
    /// Transitions the ConsolidationRun status from Queued → Running after successful K8s Job creation.
    /// Guarded: only transitions if current status is Queued. No-op if run not found.
    /// </summary>
    private async Task TransitionConsolidationRunToRunningAsync(JobDistributionRequest request, CancellationToken ct)
    {
        var runId = request.RunId ?? request.IssueIdentifier;
        if (string.IsNullOrEmpty(runId))
            return;

        // Use IConsolidationService.TransitionToRunningAsync to ensure StartedAtUtc is reset
        // (timeout anchor), the in-memory _runningRuns tracker is updated, and the
        // GetRunHistoryAsync cache is invalidated.
        if (_consolidationService is not null)
        {
            try
            {
                await _consolidationService.TransitionToRunningAsync(runId, ct);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ConsolidationK8sDispatcher: failed to transition consolidation run {RunId} to Running (non-fatal)", runId);
            }
            return;
        }

        // Fallback: direct store write when IConsolidationService not available (shouldn't happen in production)
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
            Log.Warning(ex, "ConsolidationK8sDispatcher: failed to transition consolidation run {RunId} to Running (non-fatal)", runId);
        }
    }
}
