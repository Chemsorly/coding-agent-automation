using System.Text.Json;
using System.Threading.RateLimiting;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Orchestration.Telemetry;
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
/// resolves container image from AgentSelector, creates K8s Jobs, updates to Dispatched.
/// Runs under leader election (same Lease as PipelineLoopService).
/// Rate-limited: default 10 Jobs/s. Skips items whose selector group is at concurrency limit.
/// </summary>
public sealed class DispatchService : BackgroundService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DispatchService>();

    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly LeaderElectionService _leaderElection;
    private readonly IKubernetes _kubeClient;
    private readonly WorkItemTransitionService _transitionService;
    private readonly DispatchServiceOptions _options;
    private readonly TokenBucketRateLimiter _rateLimiter;

    public DispatchService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        LeaderElectionService leaderElection,
        IKubernetes kubeClient,
        WorkItemTransitionService transitionService,
        IConfiguration configuration)
    {
        _dbFactory = dbFactory;
        _leaderElection = leaderElection;
        _kubeClient = kubeClient;
        _transitionService = transitionService;
        _options = new DispatchServiceOptions();
        configuration.GetSection("WorkDistribution:Dispatch").Bind(_options);
        configuration.GetSection("WorkDistribution:ImageMapping").Bind(_options.ImageMapping);

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

        while (!stoppingToken.IsCancellationRequested && !_leaderElection.IsLeader)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }

        Log.Information("DispatchService: leader acquired, entering poll loop");

        while (!stoppingToken.IsCancellationRequested && _leaderElection.IsLeader)
        {
            try
            {
                await PollAndDispatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "DispatchService: unhandled error in poll cycle");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Log.Information("DispatchService: exiting (lost leadership or stopping)");
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
                ProjectId = w.ProjectId
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

            // Check concurrency limit for selector group
            if (_options.MaxConcurrentPods.TryGetValue(item.AgentSelector, out var maxConcurrent) && maxConcurrent > 0)
            {
                var current = concurrencyBySelector.GetValueOrDefault(item.AgentSelector, 0);
                if (current >= maxConcurrent)
                {
                    Log.Debug("DispatchService: selector {Selector} at concurrency limit ({Current}/{Max}), skipping {WorkItemId}",
                        item.AgentSelector, current, maxConcurrent, item.Id);
                    continue;
                }
            }

            // Determine if this is a kiro agent (needs PVC) or opencode agent (bypasses PVC)
            var isKiroAgent = IsKiroAgent(item.AgentSelector);

            if (isKiroAgent && availablePvcs.Count == 0)
            {
                // No PVC available — skip, leave Pending for next poll cycle (NOT failed)
                Log.Information("DispatchService: no PVC available for kiro agent, skipping WorkItem {WorkItemId}", item.Id);
                continue;
            }

            await DispatchSingleItemAsync(db, item, isKiroAgent, availablePvcs, concurrencyBySelector, ct);
        }

        WorkDistributionTelemetry.DispatcherPollCount.Add(1);
    }

    private async Task DispatchSingleItemAsync(
        PipelineDbContext db,
        PendingWorkItemProjection item,
        bool isKiroAgent,
        List<string> availablePvcs,
        Dictionary<string, int> concurrencyBySelector,
        CancellationToken ct)
    {
        // Resolve container image
        var image = ResolveImage(item.AgentSelector);
        if (image is null)
        {
            Log.Error("DispatchService: no image mapping for selector '{Selector}', failing WorkItem {WorkItemId}",
                item.AgentSelector, item.Id);
            await FailWorkItem(item.Id, $"No image mapping for selector: {item.AgentSelector}", ct);
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

        // Create K8s Job
        try
        {
            var job = BuildJobSpec(item, jobName, image, claimedPvc, projectSecrets);
            await _kubeClient.BatchV1.CreateNamespacedJobAsync(job, _options.Namespace, cancellationToken: ct);
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
        }
        catch (DbUpdateConcurrencyException)
        {
            Log.Warning("DispatchService: concurrency conflict updating WorkItem {WorkItemId} to Dispatched", item.Id);
            // Job exists in K8s — ReconciliationService will reconcile
        }
    }

    private V1Job BuildJobSpec(
        PendingWorkItemProjection item,
        string jobName,
        string image,
        string? claimedPvc,
        Dictionary<string, string>? projectSecrets)
    {
        var isKiroAgent = IsKiroAgent(item.AgentSelector);
        var isOpencodeAgent = IsOpencodeAgent(item.AgentSelector);

        var envVars = new List<V1EnvVar>
        {
            new() { Name = "ORCHESTRATOR_URL", Value = _options.OrchestratorUrl },
            new() { Name = "AGENT_API_KEY_FILE", Value = "/var/run/secrets/agent-api-key/agent-api-key" }
        };

        // Propagate OTel env vars from orchestrator's own environment
        var otelEndpoint = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        if (!string.IsNullOrEmpty(otelEndpoint))
            envVars.Add(new V1EnvVar { Name = "OTEL_EXPORTER_OTLP_ENDPOINT", Value = otelEndpoint });

        var otelHeaders = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_HEADERS");
        if (!string.IsNullOrEmpty(otelHeaders))
            envVars.Add(new V1EnvVar { Name = "OTEL_EXPORTER_OTLP_HEADERS", Value = otelHeaders });

        var volumeMounts = new List<V1VolumeMount>
        {
            new()
            {
                Name = "agent-api-key",
                MountPath = "/var/run/secrets/agent-api-key",
                ReadOnlyProperty = true
            }
        };

        var volumes = new List<V1Volume>
        {
            new()
            {
                Name = "agent-api-key",
                Secret = new V1SecretVolumeSource
                {
                    SecretName = _options.AgentApiKeySecretName,
                    Items = [new V1KeyToPath { Key = "agent-api-key", Path = "agent-api-key" }]
                }
            }
        };

        // PVC mount for kiro agents
        if (isKiroAgent && claimedPvc is not null)
        {
            volumeMounts.Add(new V1VolumeMount
            {
                Name = "kiro-cli-data",
                MountPath = "/home/ubuntu/.local/share/kiro-cli"
            });
            volumes.Add(new V1Volume
            {
                Name = "kiro-cli-data",
                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource
                {
                    ClaimName = claimedPvc
                }
            });
        }

        // Opencode config secret mount
        if (isOpencodeAgent && !string.IsNullOrEmpty(_options.OpencodeConfigSecretName))
        {
            volumeMounts.Add(new V1VolumeMount
            {
                Name = "opencode-config",
                MountPath = "/home/ubuntu/.config/opencode",
                ReadOnlyProperty = true
            });
            volumes.Add(new V1Volume
            {
                Name = "opencode-config",
                Secret = new V1SecretVolumeSource
                {
                    SecretName = _options.OpencodeConfigSecretName
                }
            });
        }

        // Per-job project-secrets volume (optional)
        if (projectSecrets is not null && projectSecrets.Count > 0)
        {
            var secretName = $"caa-secrets-{item.Id.ToString("N")[..8]}";
            volumeMounts.Add(new V1VolumeMount
            {
                Name = "project-secrets",
                MountPath = "/var/run/secrets/project-secrets",
                ReadOnlyProperty = true
            });
            volumes.Add(new V1Volume
            {
                Name = "project-secrets",
                Secret = new V1SecretVolumeSource
                {
                    SecretName = secretName,
                    Optional = true
                }
            });
        }

        var container = new V1Container
        {
            Name = "agent",
            Image = image,
            Args = [$"--work-item-id={item.Id}"],
            Env = envVars,
            VolumeMounts = volumeMounts,
            SecurityContext = new V1SecurityContext
            {
                Capabilities = new V1Capabilities { Drop = ["ALL"] }
            }
        };

        // Apply resource requests/limits from config
        if (_options.JobResources is not null)
        {
            container.Resources = new V1ResourceRequirements
            {
                Requests = _options.JobResources.Requests?
                    .ToDictionary(kv => kv.Key, kv => new ResourceQuantity(kv.Value)),
                Limits = _options.JobResources.Limits?
                    .ToDictionary(kv => kv.Key, kv => new ResourceQuantity(kv.Value))
            };
        }

        return new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = jobName,
                NamespaceProperty = _options.Namespace,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "caa-orchestrator",
                    ["app.kubernetes.io/component"] = "agent-job",
                    ["caa/work-item-id"] = item.Id.ToString(),
                    ["caa/agent-selector"] = item.AgentSelector
                }
            },
            Spec = new V1JobSpec
            {
                Parallelism = 1,
                Completions = 1,
                BackoffLimit = 2,
                ActiveDeadlineSeconds = item.TimeoutSeconds + 60,
                TtlSecondsAfterFinished = 3600,
                Template = new V1PodTemplateSpec
                {
                    Spec = new V1PodSpec
                    {
                        ServiceAccountName = _options.AgentServiceAccountName,
                        RestartPolicy = "Never",
                        TerminationGracePeriodSeconds = 30,
                        SecurityContext = new V1PodSecurityContext
                        {
                            RunAsNonRoot = true,
                            SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" }
                        },
                        Containers = [container],
                        Volumes = volumes
                    }
                }
            }
        };
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

        await _kubeClient.CoreV1.CreateNamespacedSecretAsync(secret, _options.Namespace, cancellationToken: ct);
    }

    private async Task<string?> GetJobUidAsync(string jobName, CancellationToken ct)
    {
        try
        {
            var job = await _kubeClient.BatchV1.ReadNamespacedJobAsync(jobName, _options.Namespace, cancellationToken: ct);
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

        var project = await db.Projects
            .AsNoTracking()
            .Where(p => p.Id == projGuid)
            .Select(p => p.Settings)
            .FirstOrDefaultAsync(ct);

        if (project is null)
            return null;

        // Read Secrets from the Settings JSONB — stored under a "Secrets" property
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
    /// Resolves container image from AgentSelector using configured image mapping.
    /// AgentSelector is already sorted comma-joined labels.
    /// </summary>
    internal string? ResolveImage(string agentSelector)
    {
        if (_options.ImageMapping.TryGetValue(agentSelector, out var image))
            return image;

        // Try normalizing: sort and re-join (in case input wasn't pre-sorted)
        var normalized = NormalizeSelector(agentSelector);
        if (normalized != agentSelector && _options.ImageMapping.TryGetValue(normalized, out image))
            return image;

        return null;
    }

    /// <summary>
    /// Normalizes agent selector by sorting labels and joining with comma.
    /// </summary>
    internal static string NormalizeSelector(string agentSelector)
    {
        var labels = agentSelector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Array.Sort(labels, StringComparer.Ordinal);
        return string.Join(",", labels);
    }

    /// <summary>
    /// Determines if the selector indicates a kiro agent (needs PVC pool).
    /// </summary>
    internal static bool IsKiroAgent(string agentSelector)
        => agentSelector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(l => string.Equals(l, "kiro", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Determines if the selector indicates an opencode agent (needs config secret mount).
    /// </summary>
    internal static bool IsOpencodeAgent(string agentSelector)
        => agentSelector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(l => string.Equals(l, "opencode", StringComparison.OrdinalIgnoreCase));

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
    }
}
