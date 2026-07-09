using System.Text.Json;
using System.Threading.RateLimiting;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Orchestration.Telemetry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
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
    private readonly LeaderElectionService _leaderElection;
    private readonly IKubernetesJobClient _kubeClient;
    private readonly WorkItemTransitionService _transitionService;
    private readonly DispatchServiceOptions _options;
    private readonly JobTemplateProvider _templateProvider;
    private readonly ILabelSwapper? _labelSwapper;
    private readonly TokenBucketRateLimiter _rateLimiter;

    public DispatchService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        LeaderElectionService leaderElection,
        IKubernetesJobClient kubeClient,
        WorkItemTransitionService transitionService,
        IConfiguration configuration,
        ILabelSwapper? labelSwapper = null)
    {
        _dbFactory = dbFactory;
        _leaderElection = leaderElection;
        _kubeClient = kubeClient;
        _transitionService = transitionService;
        _labelSwapper = labelSwapper;
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
        LeaderElectionService leaderElection,
        IKubernetesJobClient kubeClient,
        WorkItemTransitionService transitionService,
        IConfiguration configuration,
        JobTemplateProvider templateProvider,
        ILabelSwapper? labelSwapper = null)
    {
        _dbFactory = dbFactory;
        _leaderElection = leaderElection;
        _kubeClient = kubeClient;
        _transitionService = transitionService;
        _labelSwapper = labelSwapper;
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
                IssueProviderConfigId = w.IssueProviderConfigId
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
            if (template is null)
            {
                await FailWorkItem(item.Id, $"No job template for selector: {item.AgentSelector}", ct);
                continue;
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
            }
            await FailWorkItem(item.Id, $"K8s Job creation failed: {ex.Message}", ct);
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
            if (_labelSwapper is not null &&
                !string.IsNullOrEmpty(item.IssueIdentifier) &&
                !string.IsNullOrEmpty(item.IssueProviderConfigId))
            {
                try
                {
                    await _labelSwapper.SwapLabelAsync(
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

    private async Task FailWorkItem(Guid workItemId, string errorMessage, CancellationToken ct)
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
        public string? ProjectId { get; init; }
        public string? IssueIdentifier { get; init; }
        public string? IssueProviderConfigId { get; init; }
    }
}
