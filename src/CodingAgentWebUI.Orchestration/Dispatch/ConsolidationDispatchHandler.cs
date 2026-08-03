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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// BackgroundService that polls for consolidation WorkItems (TaskType=Consolidation) and dispatches
/// them as K8s Jobs. Runs under leader election (same Lease as DispatchService).
/// Extracted from DispatchService to separate consolidation-specific concerns (run status transitions,
/// provider config resolution, cascade failure) from regular issue dispatch.
/// </summary>
internal sealed class ConsolidationDispatchHandler : BackgroundService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ConsolidationDispatchHandler>();

    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly ILeaderElectionService _leaderElection;
    private readonly DispatchLifecycleService _lifecycle;
    private readonly DispatchServiceOptions _options;
    private readonly WorkItemTransitionService _transitionService;
    private readonly IConsolidationRunStore? _consolidationRunStore;
    private readonly IConsolidationService? _consolidationService;
    private readonly IConsolidationJobPreparationService? _consolidationJobPreparer;
    private readonly IPipelineConfigStore? _pipelineConfigStore;
    private readonly IProjectStore? _projectStore;
    private readonly DispatchEligibilityChecker _eligibilityChecker;
    private readonly TokenBucketRateLimiter _rateLimiter;

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
    {
        _dbFactory = dbFactory;
        _leaderElection = leaderElection;
        _lifecycle = lifecycle;
        _transitionService = transitionService;
        _consolidationRunStore = consolidationRunStore;
        _consolidationService = consolidationService;
        _consolidationJobPreparer = consolidationJobPreparer;
        _pipelineConfigStore = pipelineConfigStore;
        _projectStore = projectStore;
        _options = DispatchServiceOptionsFactory.Create(configuration);
        _eligibilityChecker = new DispatchEligibilityChecker(templateProvider, agentProfileStore);
        _rateLimiter = RateLimiterFactory.CreateTokenBucket(_options.RateLimitPerSecond);
    }

    /// <summary>
    /// Test constructor accepting a pre-built JobTemplateStore.
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
    {
        _dbFactory = dbFactory;
        _leaderElection = leaderElection;
        _lifecycle = lifecycle;
        _transitionService = transitionService;
        _consolidationRunStore = consolidationRunStore;
        _consolidationService = consolidationService;
        _consolidationJobPreparer = consolidationJobPreparer;
        _pipelineConfigStore = pipelineConfigStore;
        _projectStore = projectStore;
        _options = options;
        _eligibilityChecker = new DispatchEligibilityChecker(templateProvider, agentProfileStore);
        _rateLimiter = RateLimiterFactory.CreateTokenBucket(_options.RateLimitPerSecond);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("ConsolidationDispatchHandler started — waiting for leader election");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait for leadership
            while (!stoppingToken.IsCancellationRequested && !_leaderElection.IsLeader)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }

            if (stoppingToken.IsCancellationRequested) break;

            Log.Information("ConsolidationDispatchHandler: leader acquired, entering poll loop");

            // Create linked token: cancels on EITHER host stop OR leadership loss
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, _leaderElection.LeaderToken);
            var ct = linked.Token;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await PollAndDispatchConsolidationAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "ConsolidationDispatchHandler: unhandled error in poll cycle");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            if (!stoppingToken.IsCancellationRequested)
            {
                Log.Information("ConsolidationDispatchHandler: leadership lost, re-entering wait loop");
            }
        }

        Log.Information("ConsolidationDispatchHandler: exiting (stopping)");
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _rateLimiter.Dispose();
        base.Dispose();
    }

    internal async Task PollAndDispatchConsolidationAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Query only consolidation items
        var pendingItems = await db.WorkItems
            .Where(w => w.Status == WorkItemStatus.Pending && w.TaskType == WorkItemTaskType.Consolidation)
            .OrderBy(w => w.CreatedAt)
            .Select(w => new PendingWorkItemProjection
            {
                Id = w.Id,
                AgentSelector = w.AgentSelector,
                CreatedAt = w.CreatedAt,
                TimeoutSeconds = w.TimeoutSeconds,
                ProjectId = w.ProjectId,
                IssueIdentifier = w.IssueIdentifier,
                IssueProviderConfigId = w.IssueProviderConfigId,
                TaskType = w.TaskType
            })
            .ToListAsync(ct);

        if (pendingItems.Count == 0)
            return;

        var (concurrencyBySelector, availablePvcs) = await BuildDispatchStateAsync(db, ct);

        foreach (var item in pendingItems)
        {
            if (ct.IsCancellationRequested || !_leaderElection.IsLeader)
                break;

            using var lease = await _rateLimiter.AcquireAsync(1, ct);
            if (!lease.IsAcquired)
            {
                Log.Warning("ConsolidationDispatchHandler: rate limit hit, stopping dispatch cycle");
                break;
            }

            var result = await _eligibilityChecker.CheckEligibilityAsync(item, concurrencyBySelector, availablePvcs.Count, ct);

            // TODO: Add explicit default/Eligible case to prevent silent fall-through if new EligibilityOutcome values are added
            switch (result.Outcome)
            {
                case EligibilityOutcome.AtConcurrencyLimit:
                case EligibilityOutcome.NoPvcAvailable:
                    continue;
                case EligibilityOutcome.NoTemplate:
                    await FailConsolidationWorkItemAsync(item.Id, result.ErrorMessage!, item.IssueIdentifier, ct);
                    continue;
            }

            await DispatchConsolidationItemAsync(db, item, result.Template!, result.IsKiroAgent, availablePvcs, concurrencyBySelector, ct);
        }
    }

    /// <summary>
    /// Queries the database to build concurrency state (active counts per selector group)
    /// and determines available PVCs for kiro agents.
    /// </summary>
    private async Task<(Dictionary<string, int> ConcurrencyBySelector, List<string> AvailablePvcs)> BuildDispatchStateAsync(
        PipelineDbContext db, CancellationToken ct)
    {
        var activeCounts = await db.WorkItems
            .Where(w => w.Status == WorkItemStatus.Dispatched || w.Status == WorkItemStatus.Running)
            .GroupBy(w => w.AgentSelector)
            .Select(g => new { Selector = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var concurrencyBySelector = activeCounts.ToDictionary(x => x.Selector, x => x.Count);

        var pvcResult = await DispatchLifecycleService.QueryAvailablePvcsAsync(db, _options.KiroPvcPool, ct);
        WorkDistributionTelemetry.UpdateCredentialPoolMetrics(pvcResult.AvailablePvcs.Count, pvcResult.ClaimedCount);
        var availablePvcs = pvcResult.AvailablePvcs;

        return (concurrencyBySelector, availablePvcs);
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
                    entity => entity.CompletedAt = DateTimeOffset.UtcNow, ct: ct);
                return;
            }
        }

        // Capture updatedRequest outside the delegate so onDispatchSuccess can reference it
        JobDistributionRequest? updatedRequest = null;

        await _lifecycle.ExecuteDispatchLifecycleAsync(
            new DispatchLifecycleContext(db, item, template, isKiroAgent, availablePvcs, concurrencyBySelector, "consolidation "),
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
                    projectSecrets = await DispatchLifecycleService.LoadProjectSecretsAsync(db, resolvedProjectId, ct);
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
