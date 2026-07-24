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
using Microsoft.Extensions.Hosting;
using Serilog;
using static CodingAgentWebUI.Orchestration.Dispatch.DispatchService;

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
    private readonly JobTemplateProvider _templateProvider;
    private readonly DispatchServiceOptions _options;
    private readonly WorkItemTransitionService _transitionService;
    private readonly IConsolidationRunStore? _consolidationRunStore;
    private readonly IConsolidationService? _consolidationService;
    private readonly IConsolidationJobPreparationService? _consolidationJobPreparer;
    private readonly IPipelineConfigStore? _pipelineConfigStore;
    private readonly IProjectStore? _projectStore;
    private readonly IAgentProfileStore? _agentProfileStore;
    // TODO: TokenBucketRateLimiter implements IDisposable but this class does not override
    // Dispose(bool) to dispose it. The internal Timer will leak on host shutdown.
    // Add: override void Dispose(bool disposing) { _rateLimiter.Dispose(); base.Dispose(disposing); }
    private readonly TokenBucketRateLimiter _rateLimiter;

    public ConsolidationDispatchHandler(
        IDbContextFactory<PipelineDbContext> dbFactory,
        ILeaderElectionService leaderElection,
        DispatchLifecycleService lifecycle,
        JobTemplateProvider templateProvider,
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
        _templateProvider = templateProvider;
        _transitionService = transitionService;
        _consolidationRunStore = consolidationRunStore;
        _consolidationService = consolidationService;
        _consolidationJobPreparer = consolidationJobPreparer;
        _pipelineConfigStore = pipelineConfigStore;
        _projectStore = projectStore;
        _agentProfileStore = agentProfileStore;
        _options = new DispatchServiceOptions();
        InitializeOptions(configuration);
        _rateLimiter = CreateRateLimiter();
    }

    /// <summary>
    /// Test constructor accepting a pre-built JobTemplateProvider.
    /// </summary>
    internal ConsolidationDispatchHandler(
        IDbContextFactory<PipelineDbContext> dbFactory,
        ILeaderElectionService leaderElection,
        DispatchLifecycleService lifecycle,
        JobTemplateProvider templateProvider,
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
        _templateProvider = templateProvider;
        _transitionService = transitionService;
        _consolidationRunStore = consolidationRunStore;
        _consolidationService = consolidationService;
        _consolidationJobPreparer = consolidationJobPreparer;
        _pipelineConfigStore = pipelineConfigStore;
        _projectStore = projectStore;
        _agentProfileStore = agentProfileStore;
        _options = options;
        _rateLimiter = CreateRateLimiter();
    }

    private void InitializeOptions(IConfiguration configuration)
    {
        configuration.GetSection("WorkDistribution:Dispatch").Bind(_options);

        var pvcList = configuration.GetSection("WorkDistribution:CredentialPools:Kiro").Get<List<string>>();
        if (pvcList is not null)
            _options.KiroPvcPool = pvcList;

        _options.OrchestratorUrl = configuration.GetValue<string>("WorkDistribution:OrchestratorUrl") ?? "";
        _options.AgentApiKeySecretName = configuration.GetValue<string>("WorkDistribution:AgentApiKeySecretName") ?? "";
        _options.AgentServiceAccountName = configuration.GetValue<string>("WorkDistribution:AgentServiceAccountName") ?? "";
        _options.Namespace = configuration.GetValue<string>("WorkDistribution:Namespace")
            ?? Environment.GetEnvironmentVariable("POD_NAMESPACE")
            ?? "default";
        _options.OpencodeConfigSecretName = configuration.GetValue<string>("WorkDistribution:OpencodeConfigSecretName") ?? "";
    }

    private TokenBucketRateLimiter CreateRateLimiter() => new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = _options.RateLimitPerSecond,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 0,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        TokensPerPeriod = _options.RateLimitPerSecond,
        AutoReplenishment = true
    });

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

        // Build concurrency state: count running/dispatched per selector group
        var activeCounts = await db.WorkItems
            .Where(w => w.Status == WorkItemStatus.Dispatched || w.Status == WorkItemStatus.Running)
            .GroupBy(w => w.AgentSelector)
            .Select(g => new { Selector = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var concurrencyBySelector = activeCounts.ToDictionary(x => x.Selector, x => x.Count);

        // TODO: PVC double-claim race — both DispatchService and ConsolidationDispatchHandler independently
        // query claimed PVCs and build separate availablePvcs lists. Between the DB query and SaveChangesAsync
        // that persists ClaimedPvcName, both services can see the same PVC as available, leading to two K8s Jobs
        // mounting the same ReadWriteOnce PVC. Consider a shared PVC allocation mechanism or DB-level locking.
        // PVC pool: determine available PVCs for kiro agents
        var claimedPvcs = await db.WorkItems
            .Where(w => w.ClaimedPvcName != null &&
                        (w.Status == WorkItemStatus.Pending ||
                         w.Status == WorkItemStatus.Dispatched ||
                         w.Status == WorkItemStatus.Running))
            .Select(w => w.ClaimedPvcName!)
            .ToListAsync(ct);

        var availablePvcs = _options.KiroPvcPool
            .Except(claimedPvcs, StringComparer.Ordinal)
            .ToList();

        foreach (var item in pendingItems)
        {
            if (ct.IsCancellationRequested || !_leaderElection.IsLeader)
                break;

            // Rate limit
            using var lease = await _rateLimiter.AcquireAsync(1, ct);
            if (!lease.IsAcquired)
            {
                Log.Warning("ConsolidationDispatchHandler: rate limit hit, stopping dispatch cycle");
                break;
            }

            // Check concurrency limit from template
            var maxConcurrent = _templateProvider.GetMaxConcurrent(item.AgentSelector);
            if (maxConcurrent > 0)
            {
                var current = concurrencyBySelector.GetValueOrDefault(item.AgentSelector, 0);
                if (current >= maxConcurrent)
                {
                    Log.Debug("ConsolidationDispatchHandler: selector {Selector} at concurrency limit ({Current}/{Max}), skipping {WorkItemId}",
                        item.AgentSelector, current, maxConcurrent, item.Id);
                    continue;
                }
            }

            // Resolve template
            var template = _templateProvider.Resolve(item.AgentSelector);
            var effectiveSelector = item.AgentSelector;
            if (template is null)
            {
                // Fallback: AgentSelector might be a subset of the template's label set
                var (fallbackTemplate, resolvedSelector) = await ResolveTemplateViaProfileAsync(item.AgentSelector, ct);
                if (fallbackTemplate is null)
                {
                    await FailConsolidationWorkItemAsync(item.Id, $"No job template for selector: {item.AgentSelector}", item.IssueIdentifier, ct);
                    continue;
                }
                template = fallbackTemplate;
                effectiveSelector = resolvedSelector!;

                // Re-check concurrency limit against the resolved selector
                var resolvedMaxConcurrent = template.MaxConcurrent;
                if (resolvedMaxConcurrent > 0)
                {
                    var current = concurrencyBySelector.GetValueOrDefault(effectiveSelector, 0);
                    if (current >= resolvedMaxConcurrent)
                    {
                        Log.Debug("ConsolidationDispatchHandler: resolved selector {Selector} at concurrency limit ({Current}/{Max}), skipping {WorkItemId}",
                            effectiveSelector, current, resolvedMaxConcurrent, item.Id);
                        continue;
                    }
                }
            }

            var isKiroAgent = string.Equals(template.ProviderType, "kiro", StringComparison.OrdinalIgnoreCase);

            if (isKiroAgent && availablePvcs.Count == 0)
            {
                Log.Information("ConsolidationDispatchHandler: no PVC available for kiro agent, skipping WorkItem {WorkItemId}", item.Id);
                continue;
            }

            await DispatchConsolidationItemAsync(db, item, template, isKiroAgent, availablePvcs, concurrencyBySelector, ct);
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

    // ── Template Resolution ─────────────────────────────────────────────

    private async Task<(JobTemplate? Template, string? ResolvedSelector)> ResolveTemplateViaProfileAsync(string agentSelector, CancellationToken ct)
    {
        if (_agentProfileStore is null)
            return (null, null);

        var selectorLabels = agentSelector
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (selectorLabels.Count == 0)
            return (null, null);

        var profiles = await _agentProfileStore.LoadAgentProfilesAsync(ct);

        var resolver = new ProfileResolver();
        var profile = resolver.ResolveByRequiredLabels(profiles, selectorLabels);

        if (profile is null)
        {
            Log.Debug("ConsolidationDispatchHandler: no profile covers selector [{Selector}] for fallback template resolution",
                agentSelector);
            return (null, null);
        }

        var profileSelector = string.Join(",",
            profile.MatchLabels.OrderBy(l => l, StringComparer.Ordinal));

        var template = _templateProvider.Resolve(profileSelector);
        if (template is not null)
        {
            Log.Warning("ConsolidationDispatchHandler: AgentSelector [{Selector}] required profile expansion to resolve template. " +
                "Resolved via profile '{ProfileId}' → [{ProfileSelector}]",
                agentSelector, profile.Id, profileSelector);
        }

        return (template, profileSelector);
    }
}
