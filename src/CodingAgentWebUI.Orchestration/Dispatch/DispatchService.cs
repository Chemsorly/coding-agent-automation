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
using k8s;
using k8s.Autorest;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// K8s mode only: polls WorkItems WHERE Status=Pending ORDER BY CreatedAt ASC,
/// resolves container image via JobTemplateProvider, creates K8s Jobs via JobSpecBuilder,
/// updates to Dispatched. Runs under leader election (same Lease as PipelineLoopService).
/// Rate-limited: default 10 Jobs/s. Skips items whose selector group is at concurrency limit.
/// </summary>
public sealed class DispatchService : BackgroundService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DispatchService>();

    /// <summary>Default path for job templates ConfigMap mount.</summary>
    internal const string DefaultJobTemplatesPath = "/app/config/job-templates.yaml";

    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly ILeaderElectionService _leaderElection;
    private readonly IKubernetesJobClient _kubeClient;
    private readonly WorkItemTransitionService _transitionService;
    private readonly DispatchServiceOptions _options;
    private readonly JobTemplateProvider _templateProvider;
    private readonly ILabelService? _labelService;
    private readonly ITokenVendingService? _tokenVending;
    private readonly IConsolidationRunStore? _consolidationRunStore;
    private readonly IConsolidationService? _consolidationService;
    private readonly IProviderConfigStore? _providerConfigStore;
    private readonly IAgentProfileStore? _agentProfileStore;
    private readonly IProjectStore? _projectStore;
    private readonly IPipelineConfigStore? _pipelineConfigStore;
    private readonly IConsolidationJobPreparer? _consolidationJobPreparer;
    private readonly TokenBucketRateLimiter _rateLimiter;

    public DispatchService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        ILeaderElectionService leaderElection,
        IKubernetesJobClient kubeClient,
        WorkItemTransitionService transitionService,
        IConfiguration configuration,
        ILabelService? labelService = null,
        ITokenVendingService? tokenVending = null,
        IConsolidationRunStore? consolidationRunStore = null,
        IConsolidationService? consolidationService = null,
        IProviderConfigStore? providerConfigStore = null,
        IAgentProfileStore? agentProfileStore = null,
        IProjectStore? projectStore = null,
        IPipelineConfigStore? pipelineConfigStore = null,
        IConsolidationJobPreparer? consolidationJobPreparer = null)
    {
        _dbFactory = dbFactory;
        _leaderElection = leaderElection;
        _kubeClient = kubeClient;
        _transitionService = transitionService;
        _labelService = labelService;
        _tokenVending = tokenVending;
        _consolidationRunStore = consolidationRunStore;
        _consolidationService = consolidationService;
        _providerConfigStore = providerConfigStore;
        _agentProfileStore = agentProfileStore;
        _projectStore = projectStore;
        _pipelineConfigStore = pipelineConfigStore;
        _consolidationJobPreparer = consolidationJobPreparer;
        _options = new DispatchServiceOptions();
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

        // Load job templates from ConfigMap-mounted file (required for K8s mode)
        var templatesPath = configuration.GetValue<string>("WorkDistribution:JobTemplatesPath") ?? DefaultJobTemplatesPath;
        // Also check .json path for format flexibility
        if (!File.Exists(templatesPath) && templatesPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            var jsonFallback = Path.ChangeExtension(templatesPath, ".json");
            if (File.Exists(jsonFallback))
                templatesPath = jsonFallback;
        }
        _templateProvider = JobTemplateProvider.LoadFromFile(templatesPath);
        Log.Information("DispatchService: loaded {Count} job template(s) from {Path}",
            _templateProvider.GetAllTemplates().Count, templatesPath);

        _rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = _options.RateLimitPerSecond,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            TokensPerPeriod = _options.RateLimitPerSecond,
            AutoReplenishment = true
        });
    }

    /// <summary>
    /// Constructor overload accepting a pre-built JobTemplateProvider (for testing).
    /// </summary>
    internal DispatchService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        ILeaderElectionService leaderElection,
        IKubernetesJobClient kubeClient,
        WorkItemTransitionService transitionService,
        IConfiguration configuration,
        JobTemplateProvider templateProvider,
        ILabelService? labelService = null,
        ITokenVendingService? tokenVending = null,
        IConsolidationRunStore? consolidationRunStore = null,
        IConsolidationService? consolidationService = null,
        IProviderConfigStore? providerConfigStore = null,
        IAgentProfileStore? agentProfileStore = null,
        IProjectStore? projectStore = null,
        IPipelineConfigStore? pipelineConfigStore = null,
        IConsolidationJobPreparer? consolidationJobPreparer = null)
    {
        _dbFactory = dbFactory;
        _leaderElection = leaderElection;
        _kubeClient = kubeClient;
        _transitionService = transitionService;
        _labelService = labelService;
        _tokenVending = tokenVending;
        _consolidationRunStore = consolidationRunStore;
        _consolidationService = consolidationService;
        _providerConfigStore = providerConfigStore;
        _agentProfileStore = agentProfileStore;
        _projectStore = projectStore;
        _pipelineConfigStore = pipelineConfigStore;
        _consolidationJobPreparer = consolidationJobPreparer;
        _templateProvider = templateProvider;
        _options = new DispatchServiceOptions();
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

        _rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = _options.RateLimitPerSecond,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            TokensPerPeriod = _options.RateLimitPerSecond,
            AutoReplenishment = true
        });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("DispatchService started — waiting for leader election");

        while (!stoppingToken.IsCancellationRequested)
        {
            // Wait for leadership
            while (!stoppingToken.IsCancellationRequested && !_leaderElection.IsLeader)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }

            if (stoppingToken.IsCancellationRequested) break;

            Log.Information("DispatchService: leader acquired, entering poll loop");

            // Create linked token: cancels on EITHER host stop OR leadership loss
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, _leaderElection.LeaderToken);
            var ct = linked.Token;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await PollAndDispatchAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "DispatchService: unhandled error in poll cycle");
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
                Log.Information("DispatchService: leadership lost, re-entering wait loop");
            }
        }

        Log.Information("DispatchService: exiting (stopping)");
    }

    private async Task PollAndDispatchAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Column projection — no Payload loading
        var pendingItems = await db.WorkItems
            .Where(w => w.Status == WorkItemStatus.Pending)
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

        WorkDistributionTelemetry.RecordLastPollEpoch();

        if (pendingItems.Count == 0)
        {
            WorkDistributionTelemetry.DispatcherPollCount.Add(1);
            return;
        }

        // Build concurrency state: count running/dispatched per selector group
        var activeCounts = await db.WorkItems
            .Where(w => w.Status == WorkItemStatus.Dispatched || w.Status == WorkItemStatus.Running)
            .GroupBy(w => w.AgentSelector)
            .Select(g => new { Selector = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var concurrencyBySelector = activeCounts.ToDictionary(x => x.Selector, x => x.Count);

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

        // Update credential pool metrics
        WorkDistributionTelemetry.UpdateCredentialPoolMetrics(availablePvcs.Count, claimedPvcs.Count);

        foreach (var item in pendingItems)
        {
            if (ct.IsCancellationRequested || !_leaderElection.IsLeader)
                break;

            // Rate limit
            using var lease = await _rateLimiter.AcquireAsync(1, ct);
            if (!lease.IsAcquired)
            {
                Log.Warning("DispatchService: rate limit hit, stopping dispatch cycle");
                break;
            }

            // Check concurrency limit from template
            var maxConcurrent = _templateProvider.GetMaxConcurrent(item.AgentSelector);
            if (maxConcurrent > 0)
            {
                var current = concurrencyBySelector.GetValueOrDefault(item.AgentSelector, 0);
                if (current >= maxConcurrent)
                {
                    Log.Debug("DispatchService: selector {Selector} at concurrency limit ({Current}/{Max}), skipping {WorkItemId}",
                        item.AgentSelector, current, maxConcurrent, item.Id);
                    continue;
                }
            }

            // Resolve template — fail immediately if no match (before PVC gating)
            var template = _templateProvider.Resolve(item.AgentSelector);
            var effectiveSelector = item.AgentSelector;
            if (template is null)
            {
                // Fallback: AgentSelector might be a subset of the template's label set
                // (e.g., consolidation items store requiredLabels ["dotnet","dotnet10"] but template
                // is keyed by full profile MatchLabels ["dotnet","dotnet10","kiro"]).
                // Resolve profile to get the full label set, then retry template lookup.
                var (fallbackTemplate, resolvedSelector) = await ResolveTemplateViaProfileAsync(item.AgentSelector, ct);
                if (fallbackTemplate is null)
                {
                    await FailWorkItem(item.Id, $"No job template for selector: {item.AgentSelector}", item.TaskType, item.IssueIdentifier, ct);
                    continue;
                }
                template = fallbackTemplate;
                effectiveSelector = resolvedSelector!;

                // Re-check concurrency limit against the resolved selector (the actual template key)
                var resolvedMaxConcurrent = template.MaxConcurrent;
                if (resolvedMaxConcurrent > 0)
                {
                    var current = concurrencyBySelector.GetValueOrDefault(effectiveSelector, 0);
                    if (current >= resolvedMaxConcurrent)
                    {
                        Log.Debug("DispatchService: resolved selector {Selector} at concurrency limit ({Current}/{Max}), skipping {WorkItemId}",
                            effectiveSelector, current, resolvedMaxConcurrent, item.Id);
                        continue;
                    }
                }
            }

            var isKiroAgent = string.Equals(template.ProviderType, "kiro", StringComparison.OrdinalIgnoreCase);

            if (isKiroAgent && availablePvcs.Count == 0)
            {
                // No PVC available — skip, leave Pending for next poll cycle (NOT failed)
                Log.Information("DispatchService: no PVC available for kiro agent, skipping WorkItem {WorkItemId}", item.Id);
                continue;
            }

            await DispatchSingleItemAsync(db, item, template, isKiroAgent, availablePvcs, concurrencyBySelector, ct);
        }

        WorkDistributionTelemetry.DispatcherPollCount.Add(1);
    }

    private async Task DispatchSingleItemAsync(
        PipelineDbContext db,
        PendingWorkItemProjection item,
        JobTemplate template,
        bool isKiroAgent,
        List<string> availablePvcs,
        Dictionary<string, int> concurrencyBySelector,
        CancellationToken ct)
    {
        // Route consolidation items to their dedicated dispatch path
        if (item.TaskType == WorkItemTaskType.Consolidation)
        {
            await DispatchConsolidationItemAsync(db, item, template, isKiroAgent, availablePvcs, concurrencyBySelector, ct);
            return;
        }

        // Generate deterministic job name
        var jobName = GenerateJobName(item.Id);

        // Claim PVC for kiro agents
        string? claimedPvc = null;
        if (isKiroAgent)
        {
            claimedPvc = availablePvcs[0];
            availablePvcs.RemoveAt(0);
        }

        // Pre-write K8sJobName (and ClaimedPvcName) to WorkItem BEFORE K8s API call
        var workItem = await db.WorkItems.FindAsync([item.Id], ct);
        if (workItem is null || workItem.Status != WorkItemStatus.Pending)
        {
            // Item was modified by another process
            if (claimedPvc is not null) availablePvcs.Add(claimedPvc);
            return;
        }

        workItem.K8sJobName = jobName;
        if (claimedPvc is not null)
            workItem.ClaimedPvcName = claimedPvc;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            Log.Warning("DispatchService: concurrency conflict pre-writing K8sJobName for {WorkItemId}", item.Id);
            if (claimedPvc is not null) availablePvcs.Add(claimedPvc);
            return;
        }

        // Load project secrets if project has them
        Dictionary<string, string>? projectSecrets = null;
        if (!string.IsNullOrEmpty(item.ProjectId))
        {
            projectSecrets = await LoadProjectSecretsAsync(db, item.ProjectId, ct);
        }

        // Create K8s Job via JobSpecBuilder
        try
        {
            var buildCtx = new JobSpecBuilder.BuildContext
            {
                WorkItemId = item.Id,
                AgentSelector = item.AgentSelector,
                TimeoutSeconds = item.TimeoutSeconds,
                JobName = jobName,
                ClaimedPvc = claimedPvc,
                OrchestratorUrl = _options.OrchestratorUrl,
                AgentApiKeySecretName = _options.AgentApiKeySecretName,
                AgentServiceAccountName = _options.AgentServiceAccountName,
                Namespace = _options.Namespace,
                OpencodeConfigSecretName = _options.OpencodeConfigSecretName,
                ProjectSecrets = projectSecrets
            };
            var job = JobSpecBuilder.Build(template, buildCtx);
            await _kubeClient.CreateJobAsync(job, _options.Namespace, ct);
        }
        catch (HttpOperationException httpEx) when (httpEx.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // 409 Conflict = Job already exists = success (idempotent)
            Log.Information("DispatchService: K8s Job {JobName} already exists (409 Conflict), treating as success", jobName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DispatchService: failed to create K8s Job {JobName} for WorkItem {WorkItemId}", jobName, item.Id);
            if (claimedPvc is not null)
            {
                workItem.ClaimedPvcName = null;
                availablePvcs.Add(claimedPvc);
                // TODO: SaveChangesAsync can throw DbUpdateConcurrencyException if another process modified
                // the work item concurrently, which would bypass FailWorkItem. Window is narrow and this is
                // strictly better than the prior behavior (permanent PVC orphan), but consider wrapping in try-catch.
                await db.SaveChangesAsync(ct);
            }
            await FailWorkItem(item.Id, $"K8s Job creation failed: {ex.Message}", item.TaskType, item.IssueIdentifier, ct);
            return;
        }

        // Create per-job K8s Secret if project has secrets
        if (projectSecrets is not null && projectSecrets.Count > 0)
        {
            try
            {
                await CreateJobSecretAsync(jobName, item.Id, projectSecrets, ct);
            }
            catch (HttpOperationException httpEx) when (httpEx.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                // Secret already exists — idempotent
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "DispatchService: failed to create project-secrets K8s Secret for Job {JobName}", jobName);
                // Non-fatal: job can still run without project secrets in degraded mode
            }
        }

        // Update to Dispatched — clear change tracker first to get fresh state
        // (avoids stale entity if another service modified the item during K8s API call)
        db.ChangeTracker.Clear();
        workItem = await db.WorkItems.FindAsync([item.Id], ct);
        if (workItem is null) return;

        if (workItem.Status != WorkItemStatus.Pending)
        {
            // Someone else already transitioned it
            return;
        }

        workItem.Status = WorkItemStatus.Dispatched;
        workItem.DispatchedAt = DateTimeOffset.UtcNow;

        try
        {
            await db.SaveChangesAsync(ct);

            // Record dispatch latency / pending duration metric
            // TODO: Use OriginalEnqueuedAt ?? CreatedAt instead of CreatedAt to reflect true time since original enqueue for re-dispatched items (see BUG-10 review findings)
            var latency = (workItem.DispatchedAt.Value - workItem.CreatedAt).TotalSeconds;
            WorkDistributionTelemetry.DispatchLatency.Record(latency,
                new KeyValuePair<string, object?>("agent_selector", item.AgentSelector));
            WorkDistributionTelemetry.PendingDuration.Record(latency,
                new KeyValuePair<string, object?>("agent_selector", item.AgentSelector));

            // Track concurrency
            concurrencyBySelector[item.AgentSelector] =
                concurrencyBySelector.GetValueOrDefault(item.AgentSelector, 0) + 1;

            Log.Information(
                "DispatchService: WorkItem {WorkItemId} dispatched as Job {JobName} (selector={Selector}, pvc={Pvc})",
                item.Id, jobName, item.AgentSelector, claimedPvc ?? "none");

            // Swap issue label to agent:in-progress (non-fatal — best effort)
            if (_labelService is not null &&
                !string.IsNullOrEmpty(item.IssueIdentifier) &&
                !string.IsNullOrEmpty(item.IssueProviderConfigId))
            {
                try
                {
                    await _labelService.SwapLabelAsync(
                        item.IssueProviderConfigId, item.IssueIdentifier, AgentLabels.InProgress, ct);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex,
                        "DispatchService: failed to swap label to agent:in-progress for {IssueIdentifier}",
                        item.IssueIdentifier);
                }
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            Log.Warning("DispatchService: concurrency conflict updating WorkItem {WorkItemId} to Dispatched", item.Id);
            // Job exists in K8s — ReconciliationService will reconcile
        }
    }

    // ── Consolidation Dispatch ─────────────────────────────────────────────

    /// <summary>
    /// Dispatches a consolidation WorkItem as a K8s Job.
    /// Builds provider configs from scratch (not present in payload), vends tokens,
    /// updates payload, creates K8s Job, transitions ConsolidationRunStatus.
    /// </summary>
    private async Task DispatchConsolidationItemAsync(
        PipelineDbContext db,
        PendingWorkItemProjection item,
        JobTemplate template,
        bool isKiroAgent,
        List<string> availablePvcs,
        Dictionary<string, int> concurrencyBySelector,
        CancellationToken ct)
    {
        // TODO: Add cancellation race guard — check whether the ConsolidationRun has been cancelled/failed
        // before dispatching, consistent with PendingWorkItemDrainService (line 178-189). Without this,
        // a cancelled consolidation run could still have its WorkItem dispatched as a K8s Job.

        var jobName = GenerateJobName(item.Id);

        // Claim PVC for kiro agents
        string? claimedPvc = null;
        if (isKiroAgent)
        {
            claimedPvc = availablePvcs[0];
            availablePvcs.RemoveAt(0);
        }

        // Load full WorkItem to access + update Payload
        var workItem = await db.WorkItems.FindAsync([item.Id], ct);
        if (workItem is null || workItem.Status != WorkItemStatus.Pending)
        {
            if (claimedPvc is not null) availablePvcs.Add(claimedPvc);
            return;
        }

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
                Log.Warning(ex, "DispatchService: failed to deserialize consolidation WorkItem {WorkItemId} payload", item.Id);
            }
        }

        if (request is null)
        {
            if (claimedPvc is not null) availablePvcs.Add(claimedPvc);
            await FailWorkItem(item.Id, "Consolidation WorkItem has no valid payload", item.TaskType, item.IssueIdentifier, ct);
            return;
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
                Log.Error("DispatchService: IConsolidationJobPreparer not available for consolidation WorkItem {WorkItemId}", item.Id);
                if (claimedPvc is not null) availablePvcs.Add(claimedPvc);
                await FailWorkItem(item.Id, "IConsolidationJobPreparer not registered", item.TaskType, item.IssueIdentifier, ct);
                return;
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
            Log.Error(ex, "DispatchService: failed to resolve provider configs for consolidation WorkItem {WorkItemId}", item.Id);
            if (claimedPvc is not null) availablePvcs.Add(claimedPvc);
            await FailWorkItem(item.Id, $"Provider config resolution failed: {ex.Message}", item.TaskType, item.IssueIdentifier, ct);
            return;
        }

        // Update payload with resolved configs so GET /api/work-items/{id}/assignment returns complete data
        var updatedRequest = request with
        {
            ProviderConfigs = vendedConfigs ?? [],
            RepoProviderConfigId = repoProviderId,
            PipelineConfiguration = pipelineConfig ?? new PipelineConfiguration()
        };
        workItem.Payload = JsonSerializer.Serialize(updatedRequest, PipelineJsonOptions.Default);

        // Pre-write K8sJobName + updated Payload
        workItem.K8sJobName = jobName;
        if (claimedPvc is not null)
            workItem.ClaimedPvcName = claimedPvc;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            Log.Warning("DispatchService: concurrency conflict pre-writing consolidation WorkItem {WorkItemId}", item.Id);
            if (claimedPvc is not null) availablePvcs.Add(claimedPvc);
            return;
        }

        // Create K8s Job
        // TODO: Add project secrets handling — load secrets via LoadProjectSecretsAsync and pass them
        // in BuildContext (consistent with pipeline dispatch path lines 380-435). Without this,
        // consolidation K8s Jobs for projects with secrets won't have access to them.
        try
        {
            var buildCtx = new JobSpecBuilder.BuildContext
            {
                WorkItemId = item.Id,
                AgentSelector = item.AgentSelector ?? "",
                TimeoutSeconds = item.TimeoutSeconds,
                JobName = jobName,
                ClaimedPvc = claimedPvc,
                OrchestratorUrl = _options.OrchestratorUrl,
                AgentApiKeySecretName = _options.AgentApiKeySecretName,
                AgentServiceAccountName = _options.AgentServiceAccountName,
                Namespace = _options.Namespace,
                OpencodeConfigSecretName = _options.OpencodeConfigSecretName
            };
            var job = JobSpecBuilder.Build(template, buildCtx);
            await _kubeClient.CreateJobAsync(job, _options.Namespace, ct);
        }
        catch (HttpOperationException httpEx) when (httpEx.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            Log.Information("DispatchService: K8s Job {JobName} already exists (409 Conflict), treating as success", jobName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DispatchService: failed to create K8s Job {JobName} for consolidation WorkItem {WorkItemId}", jobName, item.Id);
            if (claimedPvc is not null)
            {
                workItem.ClaimedPvcName = null;
                availablePvcs.Add(claimedPvc);
                // TODO: SaveChangesAsync can throw DbUpdateConcurrencyException if another process modified
                // the work item concurrently, which would bypass FailWorkItem. Window is narrow and this is
                // strictly better than the prior behavior (permanent PVC orphan), but consider wrapping in try-catch.
                await db.SaveChangesAsync(ct);
            }
            await FailWorkItem(item.Id, $"K8s Job creation failed: {ex.Message}", item.TaskType, item.IssueIdentifier, ct);
            return;
        }

        // Transition to Dispatched
        db.ChangeTracker.Clear();
        workItem = await db.WorkItems.FindAsync([item.Id], ct);
        // TODO: If status is no longer Pending (another process dispatched it), the K8s Job was already
        // created but we skip the Dispatched status update, and claimedPvc is not released back to
        // availablePvcs. This creates a state inconsistency window. Same pattern as pipeline path.
        if (workItem is null || workItem.Status != WorkItemStatus.Pending)
            return;

        workItem.Status = WorkItemStatus.Dispatched;
        workItem.DispatchedAt = DateTimeOffset.UtcNow;

        try
        {
            await db.SaveChangesAsync(ct);

            var latency = (workItem.DispatchedAt.Value - workItem.CreatedAt).TotalSeconds;
            WorkDistributionTelemetry.DispatchLatency.Record(latency,
                new KeyValuePair<string, object?>("agent_selector", item.AgentSelector));
            WorkDistributionTelemetry.PendingDuration.Record(latency,
                new KeyValuePair<string, object?>("agent_selector", item.AgentSelector));

            concurrencyBySelector[item.AgentSelector ?? ""] =
                concurrencyBySelector.GetValueOrDefault(item.AgentSelector ?? "", 0) + 1;

            Log.Information(
                "DispatchService: consolidation WorkItem {WorkItemId} dispatched as Job {JobName} (selector={Selector}, pvc={Pvc})",
                item.Id, jobName, item.AgentSelector, claimedPvc ?? "none");
        }
        catch (DbUpdateConcurrencyException)
        {
            Log.Warning("DispatchService: concurrency conflict updating consolidation WorkItem {WorkItemId} to Dispatched", item.Id);
            return;
        }

        // Transition ConsolidationRunStatus: Queued → Running (best-effort, after successful dispatch)
        // TODO: This runs after the Dispatched status is persisted. If TransitionConsolidationRunToRunningAsync
        // fails despite its try/catch (e.g., non-standard exception), the WorkItem is Dispatched but the
        // ConsolidationRun remains Queued — a state inconsistency. Document recovery path or consider
        // reconciliation logic.
        await TransitionConsolidationRunToRunningAsync(updatedRequest, ct);
    }

    /// <summary>
    /// Transitions the ConsolidationRun status from Queued → Running after successful K8s Job creation.
    /// Guarded: only transitions if current status is Queued. No-op if run not found.
    /// </summary>
    private async Task TransitionConsolidationRunToRunningAsync(JobDistributionRequest request, CancellationToken ct)
    {
        var runId = request.RunId ?? request.IssueIdentifier;
        if (string.IsNullOrEmpty(runId))
            return;

        // Use IConsolidationService.UpdateRunAsync (not direct store write) to ensure
        // the GetRunHistoryAsync cache is invalidated. Without this, the Active Runs
        // section shows "(0)" even when an agent is busy with a consolidation job.
        if (_consolidationService is not null)
        {
            try
            {
                await _consolidationService.UpdateRunAsync(
                    runId, ConsolidationRunStatus.Running, null, ct);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "DispatchService: failed to transition consolidation run {RunId} to Running (non-fatal)", runId);
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
                await _consolidationRunStore.SaveRunAsync(run, ct);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DispatchService: failed to transition consolidation run {RunId} to Running (non-fatal)", runId);
        }
    }

    private async Task CreateJobSecretAsync(
        string jobName, Guid workItemId, Dictionary<string, string> secrets, CancellationToken ct)
    {
        var secretName = $"caa-secrets-{workItemId.ToString("N")[..8]}";

        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = secretName,
                NamespaceProperty = _options.Namespace,
                OwnerReferences =
                [
                    new V1OwnerReference
                    {
                        ApiVersion = "batch/v1",
                        Kind = "Job",
                        Name = jobName,
                        Uid = await GetJobUidAsync(jobName, ct) ?? ""
                    }
                ]
            },
            StringData = secrets
        };

        await _kubeClient.CreateSecretAsync(secret, _options.Namespace, ct);
    }

    private async Task<string?> GetJobUidAsync(string jobName, CancellationToken ct)
    {
        try
        {
            var job = await _kubeClient.ReadJobAsync(jobName, _options.Namespace, ct);
            return job.Metadata?.Uid;
        }
        catch
        {
            return null;
        }
    }

    private async Task<Dictionary<string, string>?> LoadProjectSecretsAsync(
        PipelineDbContext db, string projectId, CancellationToken ct)
    {
        if (!Guid.TryParse(projectId, out var projGuid))
            return null;

        var settingsJson = await db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projGuid)
            .Select(p => p.Settings)
            .FirstOrDefaultAsync(ct);

        if (settingsJson is null)
            return null;

        // Read Secrets from the Settings JSONB — stored under a "Secrets" property
        using var project = JsonDocument.Parse(settingsJson);
        if (project.RootElement.TryGetProperty("Secrets", out var secretsElement) &&
            secretsElement.ValueKind == JsonValueKind.Object)
        {
            var result = new Dictionary<string, string>();
            foreach (var prop in secretsElement.EnumerateObject())
            {
                result[prop.Name] = prop.Value.GetString() ?? "";
            }
            return result.Count > 0 ? result : null;
        }

        return null;
    }

    /// <summary>
    /// Fallback template resolution: when the work item's AgentSelector is a subset of the template's
    /// label set (e.g., consolidation items that store only requiredLabels), resolve the matching profile
    /// to get the full MatchLabels, then retry template lookup with the profile's labels.
    /// This aligns with how DispatchOrchestrationService uses profile.MatchLabels as the AgentSelector.
    /// </summary>
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
            Log.Debug("DispatchService: no profile covers selector [{Selector}] for fallback template resolution",
                agentSelector);
            return (null, null);
        }

        // Use profile's MatchLabels as the template key (same as DispatchOrchestrationService.MapToRequest)
        var profileSelector = string.Join(",",
            profile.MatchLabels.OrderBy(l => l, StringComparer.Ordinal));

        var template = _templateProvider.Resolve(profileSelector);
        if (template is not null)
        {
            Log.Warning("DispatchService: AgentSelector [{Selector}] required profile expansion to resolve template. " +
                "Upstream code path may not be setting AgentSelector to full profile.MatchLabels. " +
                "Resolved via profile '{ProfileId}' → [{ProfileSelector}]",
                agentSelector, profile.Id, profileSelector);
        }

        return (template, profileSelector);
    }

    private async Task FailWorkItem(Guid workItemId, string errorMessage,
        WorkItemTaskType taskType, string? issueIdentifier, CancellationToken ct)
    {
        await _transitionService.TransitionAsync(
            workItemId,
            WorkItemStatus.Failed,
            item =>
            {
                item.ErrorMessage = errorMessage;
                item.FailureReason = FailureReason.InfrastructureFailure;
                item.CompletedAt = DateTimeOffset.UtcNow;
            },
            ct);

        Log.Warning("DispatchService: WorkItem {WorkItemId} failed: {Error}", workItemId, errorMessage);

        // Cascade to ConsolidationRun: transition to Failed so it doesn't stay stuck in Queued/Running
        if (taskType == WorkItemTaskType.Consolidation && issueIdentifier is not null)
            await CascadeFailureToConsolidationRunAsync(issueIdentifier, errorMessage, ct);
    }

    /// <summary>
    /// Cascades a failure to a ConsolidationRun, transitioning it from Queued/Running to Failed.
    /// Delegates to <see cref="IConsolidationService.UpdateRunAsync"/> which handles cache invalidation,
    /// _runningRuns cleanup, OnChange event, and workspace management.
    /// Falls back to direct store write if IConsolidationService is unavailable.
    /// Safe to call from any failure path (DispatchService, ReconciliationService).
    /// </summary>
    internal async Task CascadeFailureToConsolidationRunAsync(string runId, string errorMessage, CancellationToken ct)
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
                Log.Information("DispatchService: cascaded failure to ConsolidationRun {RunId} via IConsolidationService", runId);
            }
            catch (OperationCanceledException)
            {
                // Graceful shutdown — cascade skipped, ReconciliationService or startup cleanup will handle it
                Log.Debug("DispatchService: cascade to ConsolidationRun {RunId} cancelled (shutdown)", runId);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "DispatchService: failed to cascade failure to ConsolidationRun {RunId} (non-fatal)", runId);
            }
            return;
        }

        // Fallback: direct store write — skips _runningRuns cleanup, OnChange, workspace cleanup.
        // Only executes in test scenarios or misconfigured DI. Log at Warning for visibility.
        Log.Warning("DispatchService: IConsolidationService unavailable, using direct store fallback for ConsolidationRun {RunId}. " +
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
                Log.Information("DispatchService: cascaded failure to ConsolidationRun {RunId} (direct store)", runId);
            }
        }
        catch (OperationCanceledException)
        {
            Log.Debug("DispatchService: cascade to ConsolidationRun {RunId} cancelled during shutdown (fallback path)", runId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DispatchService: failed to cascade failure to ConsolidationRun {RunId} (non-fatal)", runId);
        }
    }

    // ── Static helpers (internal for testability) ────────────────────────

    /// <summary>
    /// Generates deterministic K8s Job name: caa-{workItemId first 8 hex chars}.
    /// </summary>
    internal static string GenerateJobName(Guid workItemId)
        => $"caa-{workItemId.ToString("N")[..8]}";

    /// <summary>
    /// Normalizes agent selector by sorting labels and joining with comma.
    /// Delegates to <see cref="JobTemplateProvider.NormalizeLabels"/>.
    /// </summary>
    internal static string NormalizeSelector(string agentSelector)
        => JobTemplateProvider.NormalizeLabels(agentSelector);

    /// <summary>
    /// Calculates available PVCs from the configured pool minus currently claimed.
    /// Exposed for property testing.
    /// </summary>
    internal static List<string> CalculateAvailablePvcs(
        IReadOnlyList<string> configuredPvcs,
        IEnumerable<string> claimedPvcs)
    {
        return configuredPvcs
            .Except(claimedPvcs, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Lightweight projection of pending work items (no Payload loaded).
    /// </summary>
    internal sealed record PendingWorkItemProjection
    {
        public required Guid Id { get; init; }
        public required string AgentSelector { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required int TimeoutSeconds { get; init; }
        public WorkItemTaskType TaskType { get; init; }
        public string? ProjectId { get; init; }
        public string? IssueIdentifier { get; init; }
        public string? IssueProviderConfigId { get; init; }
    }
}
