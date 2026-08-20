using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using k8s.Models;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Fetches the available Kiro CLI model list in Kubernetes mode by dispatching a one-shot
/// k8s Job that runs the agent binary normally. The agent pod connects to the orchestrator
/// hub, receives a <c>RequestFetchModels</c> message, runs <c>kiro-cli --list-models</c>,
/// and reports the result back via <c>ReportFetchModelsResult</c>. The orchestrator reads
/// the response via <see cref="ModelFetchService"/> — no pod log reads or extra RBAC needed.
/// </summary>
public sealed class ModelFetchJobService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ModelFetchJobService>();

    /// <summary>Prefix distinguishing model-fetch Jobs from work-item Jobs (caa-&lt;workItemId&gt;).</summary>
    public const string JobNamePrefix = "caa-models-";

    /// <summary>Label value placed on model-fetch Jobs so ReconciliationService can exclude them.</summary>
    public const string JobTypeLabel = "model-fetch";

    /// <summary>
    /// True when at least one credential PVC is configured in the pool.
    /// Used by the UI to proactively disable the Fetch Models button and show a hint
    /// rather than letting the operator discover the requirement after clicking.
    /// </summary>
    public bool IsPvcPoolConfigured => _options.KiroPvcPool.Count > 0;

    private readonly IKubernetesJobClient _kubeClient;
    private readonly JobTemplateStore _templateStore;
    private readonly DispatchServiceOptions _options;
    private readonly IPipelineConfigStore _configStore;
    private readonly IModelFetchReceiver _modelFetchReceiver;
    private readonly int? _pollTimeoutSecondsOverride; // non-null only in tests
    private readonly int _pollIntervalMs;

    public ModelFetchJobService(
        ModelFetchJobDependencies deps)
    {
        _kubeClient = deps.KubeClient;
        _templateStore = deps.TemplateStore;
        _options = deps.Options;
        _configStore = deps.ConfigStore;
        _modelFetchReceiver = deps.ModelFetchReceiver;
        _pollTimeoutSecondsOverride = deps.PollTimeoutSecondsOverride;
        _pollIntervalMs = deps.PollIntervalMs;
        _ = deps.Logger; // consumed via static Log; parameter kept for test injection
    }

    /// <summary>
    /// Dispatches a one-shot k8s Job running the agent binary. The agent pod connects to
    /// the orchestrator hub and handles a <c>RequestFetchModels</c> request via the normal
    /// SignalR protocol. Results are returned through <see cref="ModelFetchService"/> —
    /// no pod log reads or <c>pods/log</c> RBAC required.
    /// </summary>
    public async Task<(IReadOnlyList<AgentModelInfo> Models, string? Error)> FetchModelsAsync(
        string providerType, CancellationToken ct, IProgress<string>? progress = null)
    {
        // Read timeout from config each call so UI changes take effect without restart.
        var config = await _configStore.LoadPipelineConfigAsync(ct);
        var pollTimeoutSeconds = _pollTimeoutSecondsOverride ?? config.ModelFetchTimeoutSeconds;

        // ── 1. Resolve job template ──────────────────────────────────────
        var template = _templateStore.GetAllTemplates()
            .FirstOrDefault(t => string.Equals(t.ProviderType, providerType, StringComparison.OrdinalIgnoreCase));

        if (template is null)
        {
            var msg = $"No job template found for provider type '{providerType}'. " +
                      "Ensure a matching entry exists in the job-templates ConfigMap.";
            Log.Warning("ModelFetchJobService: {Message}", msg);
            return ([], msg);
        }

        // ── 2. Claim PVC (required for kiro provider auth) ───────────────
        string? claimedPvc = null;
        if (string.Equals(providerType, "kiro", StringComparison.OrdinalIgnoreCase))
        {
            if (_options.KiroPvcPool.Count == 0)
            {
                const string msg = "No credential PVC available — the kiro PVC pool is empty. " +
                                   "Configure at least one PVC in WorkDistribution:KiroPvcPool.";
                Log.Warning("ModelFetchJobService: {Message}", msg);
                return ([], msg);
            }
            claimedPvc = await SelectAvailablePvcAsync(ct) ?? _options.KiroPvcPool[0];
        }

        // ── 3. Build job spec ────────────────────────────────────────────
        // The job runs the normal agent binary (no command override). The agent connects
        // to the hub, registers, receives RequestFetchModels, and reports results back.
        var jobName = $"{JobNamePrefix}{Guid.NewGuid().ToString("N")[..8]}";
        var job = BuildFetchModelsJob(template, jobName, claimedPvc, pollTimeoutSeconds);

        // ── 4. Create job ────────────────────────────────────────────────
        progress?.Report("Creating job…");
        try
        {
            await _kubeClient.CreateJobAsync(job, _options.Namespace, ct);
            Log.Information("ModelFetchJobService: created job {JobName} in namespace {Namespace}",
                jobName, _options.Namespace);
        }
        catch (OperationCanceledException)
        {
            return ([], "Fetch models was cancelled before the job could be created.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ModelFetchJobService: failed to create job {JobName}", jobName);
            return ([], $"Failed to create fetch-models job: {ex.Message}");
        }

        // ── 5. Wait for agent to connect and report results via SignalR ──
        // For model-fetch pods, AGENT_ID is still set from metadata.name (Downward API fieldRef),
        // so the pod name includes the random K8s suffix. This differs from work-item pods where
        // AGENT_ID is set to the static job name (Spec 041 auth fix). Model-fetch pods use the
        // chat/SignalR auth path (not the HMAC work-item path) so the suffix mismatch is acceptable.
        // ModelFetchService.WaitAndFetchAsync polls the registry until that agent appears,
        // then sends RequestFetchModels and awaits ReportFetchModelsResult — all over the
        // existing hub connection. No pod log reads, no pods/log RBAC required.
        progress?.Report("Waiting for agent to connect…");
        IReadOnlyList<AgentModelInfo> models = [];
        string? fetchError = null;

        try
        {
            (models, fetchError) = await _modelFetchReceiver.WaitAndFetchAsync(
                agentIdPrefix: jobName,
                timeoutSeconds: pollTimeoutSeconds,
                pollIntervalMs: _pollIntervalMs,
                ct: ct);

            if (fetchError is null)
                progress?.Report("Received results…");
        }
        catch (Exception ex)
        {
            fetchError = $"Unexpected error waiting for fetch-models agent: {ex.Message}";
            Log.Warning(ex, "ModelFetchJobService: unexpected error for job {JobName}", jobName);
        }

        // ── 6. Cleanup (best-effort) ─────────────────────────────────────
        try
        {
            await _kubeClient.DeleteJobAsync(jobName, _options.Namespace, CancellationToken.None);
            Log.Debug("ModelFetchJobService: deleted job {JobName}", jobName);
        }
        catch (Exception ex)
        {
            // Don't propagate — a cleanup failure must not hide a successful result.
            Log.Warning(ex, "ModelFetchJobService: cleanup failed for job {JobName} — will be GC'd by TTL",
                jobName);
        }

        if (fetchError is not null)
            Log.Warning("ModelFetchJobService: fetch failed — {Error}", fetchError);
        else
            Log.Information("ModelFetchJobService: fetched {Count} model(s) via job {JobName}",
                models.Count, jobName);

        return (models, fetchError);
    }

    private async Task<string?> SelectAvailablePvcAsync(CancellationToken ct)
    {
        try
        {
            var runningJobs = await _kubeClient.ListJobsAsync(
                _options.Namespace,
                "app.kubernetes.io/component=agent-job",
                ct);

            var claimedPvcs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var j in runningJobs.Items)
            {
                if (j.Status?.Active is null or 0) continue;
                foreach (var vol in j.Spec?.Template?.Spec?.Volumes ?? [])
                {
                    if (vol.PersistentVolumeClaim?.ClaimName is { } pvcName)
                        claimedPvcs.Add(pvcName);
                }
            }

            var available = _options.KiroPvcPool.FirstOrDefault(pvc => !claimedPvcs.Contains(pvc));
            if (available is null)
                Log.Warning("ModelFetchJobService: all {Count} PVCs are in use; " +
                            "using {Pvc} anyway — RWX volumes required for concurrent access",
                    _options.KiroPvcPool.Count, _options.KiroPvcPool[0]);

            return available;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ModelFetchJobService: failed to query running jobs for PVC selection; using KiroPvcPool[0]");
            return null;
        }
    }

    private V1Job BuildFetchModelsJob(JobTemplate template, string jobName, string? pvcName, int pollTimeoutSeconds)
    {
        // Build a full job spec from the template — the agent pod runs the standard agent
        // binary, connects to the hub, and handles RequestFetchModels normally.
        var job = JobSpecBuilder.Build(template, new JobSpecBuilder.BuildContext
        {
            WorkItemId = null,           // no work item — agent enters SignalR mode
            AgentSelector = string.Empty,
            TimeoutSeconds = pollTimeoutSeconds,
            JobName = jobName,
            ClaimedPvc = pvcName,
            OrchestratorUrl = _options.OrchestratorUrl,
            AgentApiKeySecretName = _options.AgentApiKeySecretName,
            AgentServiceAccountName = _options.AgentServiceAccountName,
            Namespace = _options.Namespace
        });

        // Override job metadata only — no command/args override, the agent binary runs as-is.
        job.Metadata.Labels = new Dictionary<string, string>
        {
            ["app.kubernetes.io/managed-by"] = "caa-orchestrator",
            ["app.kubernetes.io/component"] = "model-fetch",
            ["caa/job-type"] = JobTypeLabel
        };

        // One-shot diagnostic: no retries, short TTL, tighter deadline.
        job.Spec.BackoffLimit = 0;
        job.Spec.TtlSecondsAfterFinished = 300;
        job.Spec.ActiveDeadlineSeconds = pollTimeoutSeconds + 30;

        return job;
    }
}
