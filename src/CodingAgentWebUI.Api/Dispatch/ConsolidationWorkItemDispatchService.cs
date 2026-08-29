using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CodingAgentWebUI.Api.Dispatch;

/// <summary>
/// BackgroundService that polls for consolidation WorkItems (TaskType=Consolidation) and dispatches
/// them as K8s Jobs. Runs under leader election (same Lease as DispatchService).
/// Extracted from DispatchService to separate consolidation-specific concerns (run status transitions,
/// provider config resolution, cascade failure) from regular issue dispatch.
/// Relocated from CodingAgentWebUI.Orchestration to CodingAgentWebUI.Api.
/// </summary>
internal sealed class ConsolidationWorkItemDispatchService : LeaderElectedPollingService
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<ConsolidationWorkItemDispatchService>();

    private readonly DispatchLifecycleService _lifecycle;
    private readonly DispatchServiceOptions _options;
    private readonly WorkItemTransitionService _transitionService;
    private readonly IConsolidationRunStore? _consolidationRunStore;
    private readonly IConsolidationService? _consolidationService;
    private readonly IConsolidationJobPreparationService? _consolidationJobPreparer;
    private readonly IPipelineConfigStore? _pipelineConfigStore;
    private readonly IProjectStore? _projectStore;
    private readonly DispatchStateBuilder _stateBuilder;

    protected override string ServiceName => "ConsolidationWorkItemDispatchService";
    protected override int PollIntervalSeconds => _options.PollIntervalSeconds;

    public ConsolidationWorkItemDispatchService(ConsolidationWorkItemDispatchServiceDependencies deps)
        // Guard deps before the factory call dereferences deps.Configuration,
        // so a null argument throws ArgumentNullException instead of NullReferenceException.
        // Resolves DispatchServiceOptions exactly once and delegates to the internal constructor.
        : this(deps ?? throw new ArgumentNullException(nameof(deps)),
               DispatchServiceOptionsFactory.Create(deps.Configuration))
    { }

    /// <summary>
    /// Internal constructor accepting a pre-built <see cref="DispatchServiceOptions"/> instead of
    /// <see cref="IConfiguration"/>. All field assignments live here; the public constructor
    /// computes options once and delegates. Also used directly by tests.
    /// </summary>
    internal ConsolidationWorkItemDispatchService(ConsolidationWorkItemDispatchServiceDependencies deps, DispatchServiceOptions options)
        // Guard deps and options before the base-constructor dereferences them,
        // so null arguments throw ArgumentNullException instead of NullReferenceException.
        : base((deps ?? throw new ArgumentNullException(nameof(deps))).LeaderElection,
               (options ?? throw new ArgumentNullException(nameof(options))).RateLimitPerSecond)
    {
        _lifecycle = deps.Lifecycle;
        _transitionService = deps.TransitionService;
        _consolidationRunStore = deps.ConsolidationRunStore;
        _consolidationService = deps.ConsolidationService;
        _consolidationJobPreparer = deps.ConsolidationJobPreparer;
        _pipelineConfigStore = deps.PipelineConfigStore;
        _projectStore = deps.ProjectStore;
        _options = options;
#pragma warning disable S3236 // Explicit "StateBuilder" param name required to match test contracts and error messages
        ArgumentNullException.ThrowIfNull(deps.StateBuilder, "StateBuilder");
#pragma warning restore S3236
        _stateBuilder = deps.StateBuilder;
    }

    /// <inheritdoc/>
    protected override Task OnPollCycleAsync(CancellationToken ct) => PollAndDispatchConsolidationAsync(ct);

    internal async Task PollAndDispatchConsolidationAsync(CancellationToken ct)
    {
        // recordTelemetry:false — credential pool metrics are updated by DispatchService polls.
        var state = await _stateBuilder.BuildStateAsync(
            w => w.TaskType == WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            ct);
        if (state is null)
            return;

        await using (state.Db)
        {
            var rateLimiter = RateLimiter ?? throw new InvalidOperationException(
                "ConsolidationWorkItemDispatchService requires a rate limiter but RateLimiter is null. " +
                "Ensure the constructor passes rateLimitPerSecond to the base class.");
            await foreach (var candidate in _stateBuilder.GetEligibleCandidatesAsync(
                state, LeaderElection, rateLimiter,
                ServiceName,
                async (item, msg, token) =>
                    await FailConsolidationWorkItemAsync(item.Id, msg, item.IssueIdentifier, token),
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
        if (await CheckIfRunCancelledOrFailedAsync(item, ct))
            return;

        // Capture updatedRequest outside the delegate so onDispatchSuccess can reference it
        JobDistributionRequest? updatedRequest = null;

        await _lifecycle.ExecuteDispatchLifecycleAsync(
            new DispatchLifecycleContext(db, item, template, isKiroAgent, availablePvcs, concurrencyBySelector, "consolidation "),
            async workItem =>
            {
                var result = await PrepareConsolidationPayloadAsync(workItem, item, db, ct);
                if (result.Request is not null)
                    updatedRequest = result.Request;
                return (result.ShouldContinue, result.ProjectSecrets);
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

    private sealed record ConsolidationPrepareResult(
        bool ShouldContinue,
        Dictionary<string, string>? ProjectSecrets,
        JobDistributionRequest? Request);

    /// <summary>
    /// Returns true if the consolidation run for <paramref name="item"/> is already
    /// cancelled or failed (and transitions the WorkItem to Cancelled).
    /// Returns false if dispatch should proceed.
    /// </summary>
    private async Task<bool> CheckIfRunCancelledOrFailedAsync(
        PendingWorkItemProjection item, CancellationToken ct)
    {
        if (_consolidationRunStore is null || string.IsNullOrEmpty(item.IssueIdentifier))
            return false;

        var runId = item.IssueIdentifier;
        var consolidationRun = await _consolidationRunStore.GetByIdAsync(runId, ct);
        if (consolidationRun is null ||
            (consolidationRun.Status != ConsolidationRunStatus.Cancelled &&
             consolidationRun.Status != ConsolidationRunStatus.Failed))
            return false;

        Log.Information(
            "ConsolidationWorkItemDispatchService: consolidation run {RunId} is {Status}, skipping dispatch for WorkItem {WorkItemId}",
            runId, consolidationRun.Status, item.Id);
        await _transitionService.TransitionAsync(
            item.Id, WorkItemStatus.Cancelled,
            entity => entity.CompletedAt = DateTimeOffset.UtcNow, ct: ct);
        return true;
    }

    /// <summary>
    /// Variant-specific preparation for consolidation WorkItems: deserializes the payload,
    /// resolves provider configs, updates the workItem entity payload, and loads project secrets.
    /// Returns a result indicating whether dispatch should continue.
    /// </summary>
    private async Task<ConsolidationPrepareResult> PrepareConsolidationPayloadAsync(
        WorkItemEntity workItem,
        PendingWorkItemProjection item,
        PipelineDbContext db,
        CancellationToken ct)
    {
        var request = DeserializeConsolidationPayload(workItem.Payload, item.Id);
        if (request is null)
        {
            await FailConsolidationWorkItemAsync(item.Id, "Consolidation WorkItem has no valid payload", item.IssueIdentifier, ct);
            return new ConsolidationPrepareResult(false, null, null);
        }

        IReadOnlyList<ProviderConfig>? vendedConfigs;
        string repoProviderId;
        PipelineConfiguration? pipelineConfig;
        try
        {
            (vendedConfigs, repoProviderId, pipelineConfig) = await ResolveProviderConfigsAsync(item, request, ct);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ConsolidationWorkItemDispatchService: failed to resolve provider configs for consolidation WorkItem {WorkItemId}", item.Id);
            await FailConsolidationWorkItemAsync(item.Id, $"Provider config resolution failed: {ex.Message}", item.IssueIdentifier, ct);
            return new ConsolidationPrepareResult(false, null, null);
        }

        // Update payload with resolved configs
        var updatedRequest = request with
        {
            ProviderConfigs = vendedConfigs ?? [],
            RepoProviderConfigId = repoProviderId,
            PipelineConfiguration = pipelineConfig ?? new PipelineConfiguration()
        };
        workItem.Payload = JsonSerializer.Serialize(updatedRequest, PipelineJsonOptions.Default);

        // Load project secrets if project has them (resolve project from template if needed)
        Dictionary<string, string>? projectSecrets = null;
        var resolvedProjectId = await ResolveProjectIdAsync(item, request, ct);
        if (!string.IsNullOrEmpty(resolvedProjectId))
            projectSecrets = await DispatchLifecycleService.LoadProjectSecretsAsync(db, resolvedProjectId, ct);

        return new ConsolidationPrepareResult(true, projectSecrets, updatedRequest);
    }

    /// <summary>
    /// Deserializes the consolidation work item payload to a <see cref="JobDistributionRequest"/>.
    /// Returns null if the payload is missing or unparseable.
    /// </summary>
    private static JobDistributionRequest? DeserializeConsolidationPayload(string? payload, Guid workItemId)
    {
        if (string.IsNullOrEmpty(payload))
            return null;

        try
        {
            return JsonSerializer.Deserialize<JobDistributionRequest>(payload, PipelineJsonOptions.Default);
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "ConsolidationWorkItemDispatchService: failed to deserialize consolidation WorkItem {WorkItemId} payload", workItemId);
            return null;
        }
    }

    /// <summary>
    /// Resolves provider configs and pipeline configuration for a consolidation dispatch.
    /// Throws on failure; callers should catch and handle.
    /// </summary>
    private async Task<(IReadOnlyList<ProviderConfig>? vendedConfigs, string repoProviderId, PipelineConfiguration? pipelineConfig)>
        ResolveProviderConfigsAsync(PendingWorkItemProjection item, JobDistributionRequest request, CancellationToken ct)
    {
        // Parse agent labels from selector string
        var agentLabels = (item.AgentSelector ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList()
            .AsReadOnly();

        // Delegate config resolution and token vending to shared preparer
        if (_consolidationJobPreparer is null)
        {
            Log.Error("ConsolidationWorkItemDispatchService: IConsolidationJobPreparationService not available for consolidation WorkItem {WorkItemId}", item.Id);
            // Do not call FailConsolidationWorkItemAsync here — the caller's catch block handles failure
            // to avoid double-failing the same work item with conflicting messages.
            throw new InvalidOperationException("IConsolidationJobPreparationService not registered");
        }

        var preparation = await _consolidationJobPreparer.PrepareAsync(
            request.ConsolidationRunType ?? ConsolidationRunType.BrainConsolidation,
            string.IsNullOrEmpty(request.ConsolidationTemplateId) ? (TemplateId?)null : (TemplateId)request.ConsolidationTemplateId,
            agentLabels,
            ct);

        PipelineConfiguration? pipelineConfig = null;
        if (_pipelineConfigStore is not null)
            pipelineConfig = await _pipelineConfigStore.LoadPipelineConfigAsync(ct);

        return (preparation.ProviderConfigs, preparation.RepoProviderConfigId, pipelineConfig);
    }

    /// <summary>
    /// Resolves the project ID for a consolidation work item.
    /// Uses the item's direct project ID if set; falls back to a template-based lookup via the project store.
    /// </summary>
    private async Task<string?> ResolveProjectIdAsync(
        PendingWorkItemProjection item, JobDistributionRequest request, CancellationToken ct)
    {
        if (item.ProjectId.HasValue)
            return item.ProjectId.Value.ToString();

        if (_projectStore is null || string.IsNullOrEmpty(request.ConsolidationTemplateId))
            return null;

        var projects = await _projectStore.LoadProjectsAsync(ct);
        if (projects is null)
            return null;

        var ownerProject = projects.FirstOrDefault(p =>
            p.Enabled && p.TemplateIds.Contains(request.ConsolidationTemplateId));
        return ownerProject?.Id;
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
                Log.Information("ConsolidationWorkItemDispatchService: cascaded failure to ConsolidationRun {RunId} via IConsolidationService", runId);
            }
            catch (OperationCanceledException oce)
            {
                Log.Debug(oce, "ConsolidationWorkItemDispatchService: cascade to ConsolidationRun {RunId} cancelled (shutdown)", runId);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ConsolidationWorkItemDispatchService: failed to cascade failure to ConsolidationRun {RunId} (non-fatal)", runId);
            }
            return;
        }

        // Fallback: direct store write when IConsolidationService not available
        Log.Warning("ConsolidationWorkItemDispatchService: IConsolidationService unavailable, using direct store fallback for ConsolidationRun {RunId}. " +
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
                Log.Information("ConsolidationWorkItemDispatchService: cascaded failure to ConsolidationRun {RunId} (direct store)", runId);
            }
        }
        catch (OperationCanceledException oce)
        {
            Log.Debug(oce, "ConsolidationWorkItemDispatchService: cascade to ConsolidationRun {RunId} cancelled during shutdown (fallback path)", runId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ConsolidationWorkItemDispatchService: failed to cascade failure to ConsolidationRun {RunId} (non-fatal)", runId);
        }
    }

    // ── Run Status Transitions ──────────────────────────────────────────

    internal async Task TransitionConsolidationRunToRunningAsync(JobDistributionRequest request, CancellationToken ct)
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
                Log.Warning(ex, "ConsolidationWorkItemDispatchService: failed to transition consolidation run {RunId} to Running (non-fatal)", runId);
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
            Log.Warning(ex, "ConsolidationWorkItemDispatchService: failed to transition consolidation run {RunId} to Running (non-fatal)", runId);
        }
    }
}
