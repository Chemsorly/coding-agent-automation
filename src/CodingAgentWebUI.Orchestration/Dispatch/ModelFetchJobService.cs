using System.Text.Json;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Models;
using k8s.Models;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Fetches the available Kiro CLI model list in Kubernetes mode by dispatching a one-shot
/// k8s Job that runs <c>kiro-cli chat --list-models --format json</c>, reading stdout from
/// pod logs once the Job completes, then cleaning up.
/// <para>
/// This is the k8s-mode replacement for <c>ModelFetchService</c> (SignalR hub-based).
/// The Job needs the kiro-cli-data PVC mounted so the CLI can authenticate against
/// the Kiro service to retrieve subscription-specific model availability.
/// </para>
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

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly IKubernetesJobClient _kubeClient;
    private readonly JobTemplateStore _templateStore;
    private readonly DispatchServiceOptions _options;
    private readonly int _pollTimeoutSeconds;
    private readonly int _pollIntervalMs;

    public ModelFetchJobService(
        IKubernetesJobClient kubeClient,
        JobTemplateStore templateStore,
        DispatchServiceOptions options,
        int pollTimeoutSeconds = 60,
        int pollIntervalMs = 2000,
        ILogger? logger = null)
    {
        _kubeClient = kubeClient;
        _templateStore = templateStore;
        _options = options;
        _pollTimeoutSeconds = pollTimeoutSeconds;
        _pollIntervalMs = pollIntervalMs;
        _ = logger; // consumed via static Log; parameter kept for test injection
    }

    /// <summary>
    /// Dispatches a one-shot k8s Job to run <c>kiro-cli chat --list-models --format json</c>,
    /// waits for completion, parses the JSON output, and returns the model list.
    /// The Job is always deleted after the operation, whether successful or not.
    /// </summary>
    /// <param name="providerType">Provider type to select a matching job template (e.g. "kiro").</param>
    /// <param name="ct">Cancellation token. On cancellation, best-effort Job cleanup is attempted.</param>
    /// <param name="progress">
    /// Optional progress sink that receives phase labels ("Creating job…", "Waiting for pod…", etc.)
    /// for display in the UI during the operation.
    /// </param>
    /// <returns>
    /// Tuple of (models, error). On success, error is null. On failure, models is empty and error
    /// contains a human-readable message suitable for display in the Settings UI.
    /// </returns>
    public async Task<(IReadOnlyList<AgentModelInfo> Models, string? Error)> FetchModelsAsync(
        string providerType, CancellationToken ct, IProgress<string>? progress = null)
    {
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
        var isKiro = string.Equals(providerType, "kiro", StringComparison.OrdinalIgnoreCase);
        string? claimedPvc = null;

        if (isKiro)
        {
            if (_options.KiroPvcPool.Count == 0)
            {
                const string msg = "No credential PVC available — the kiro PVC pool is empty. " +
                                   "Configure at least one PVC in WorkDistribution:KiroPvcPool.";
                Log.Warning("ModelFetchJobService: {Message}", msg);
                return ([], msg);
            }

            // Select a PVC that is not already mounted by another running caa-* Job.
            // This avoids ReadWriteOnce volume conflicts when work-item jobs are running
            // concurrently and holding PVCs exclusively.
            claimedPvc = await SelectAvailablePvcAsync(ct) ?? _options.KiroPvcPool[0];
        }

        // ── 3. Build job spec ────────────────────────────────────────────
        var jobName = $"{JobNamePrefix}{Guid.NewGuid().ToString("N")[..8]}";
        var job = BuildFetchModelsJob(template, jobName, claimedPvc);

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

        // ── 5. Poll until complete ───────────────────────────────────────
        progress?.Report("Waiting for pod to start…");
        string? pollError = null;
        bool succeeded = false;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_pollTimeoutSeconds));
            var timeoutToken = timeoutCts.Token;

            while (!timeoutToken.IsCancellationRequested)
            {
                await Task.Delay(_pollIntervalMs, timeoutToken);

                V1Job status;
                try { status = await _kubeClient.ReadJobAsync(jobName, _options.Namespace, timeoutToken); }
                catch (OperationCanceledException) { break; }

                if (status.Status?.Active >= 1)
                    progress?.Report("Pod is running…");

                if (status.Status?.Succeeded >= 1)
                {
                    succeeded = true;
                    break;
                }

                if (status.Status?.Failed >= 1)
                {
                    pollError = $"Fetch models job {jobName} failed (exit code non-zero). " +
                                $"Run: kubectl logs -l job-name={jobName} -n {_options.Namespace}";
                    break;
                }
            }

            if (!succeeded && pollError is null)
            {
                // Distinguish user cancellation vs timeout
                if (ct.IsCancellationRequested)
                    pollError = "Fetch models was cancelled.";
                else
                    pollError = $"Fetch models job {jobName} timed out after {_pollTimeoutSeconds}s. " +
                                $"The agent pod may be slow to start or failing to schedule. " +
                                $"Run: kubectl describe job {jobName} -n {_options.Namespace}";
            }
        }
        catch (OperationCanceledException)
        {
            pollError = ct.IsCancellationRequested
                ? "Fetch models was cancelled."
                : $"Fetch models job {jobName} timed out after {_pollTimeoutSeconds}s. " +
                  $"Run: kubectl describe job {jobName} -n {_options.Namespace}";
        }

        // ── 6. Read pod logs (success path) — with backoff retry ─────────
        IReadOnlyList<AgentModelInfo> models = [];
        string? parseError = null;

        if (succeeded)
        {
            progress?.Report("Reading results…");
            try
            {
                var podList = await _kubeClient.ListPodsAsync(
                    _options.Namespace,
                    $"job-name={jobName}",
                    CancellationToken.None);

                var pod = podList.Items.FirstOrDefault();
                if (pod?.Metadata?.Name is null)
                {
                    parseError = "Fetch models job succeeded but no pod was found to read logs from.";
                }
                else
                {
                    // Kubelet log flushing is async with the Job status transition; retry with
                    // exponential backoff to avoid "no output" errors on fast completions.
                    (models, parseError) = await ReadLogsWithRetryAsync(pod.Metadata.Name, jobName);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ModelFetchJobService: failed to read logs for job {JobName}", jobName);
                parseError = $"Fetch models job completed but logs could not be retrieved: {ex.Message}";
            }
        }

        // ── 7. Cleanup (best-effort) ─────────────────────────────────────
        try
        {
            await _kubeClient.DeleteJobAsync(jobName, _options.Namespace, CancellationToken.None);
            Log.Debug("ModelFetchJobService: deleted job {JobName}", jobName);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ModelFetchJobService: cleanup failed for job {JobName} — will be GC'd by KubernetesJobCleanup",
                jobName);
            // Do NOT propagate — a cleanup failure must not hide a successful result.
        }

        var finalError = pollError ?? parseError;
        if (finalError is not null)
            Log.Warning("ModelFetchJobService: fetch failed — {Error}", finalError);
        else
            Log.Information("ModelFetchJobService: fetched {Count} model(s) via job {JobName}",
                models.Count, jobName);

        return (models, finalError);
    }

    // ── Private helpers ──────────────────────────────────────────────────

    /// <summary>
    /// Attempts to find a PVC from the pool that is not currently mounted by another running
    /// caa-* Job. Falls back to <c>null</c> (caller uses pool[0]) if the k8s query fails
    /// or all PVCs appear busy.
    /// </summary>
    private async Task<string?> SelectAvailablePvcAsync(CancellationToken ct)
    {
        try
        {
            // List running/pending work-item jobs to find claimed PVCs.
            // Model-fetch jobs (caa-models-*) are excluded by label.
            var runningJobs = await _kubeClient.ListJobsAsync(
                _options.Namespace,
                "app.kubernetes.io/component=agent-job",
                ct);

            var claimedPvcs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var j in runningJobs.Items)
            {
                // Only consider active (not completed/failed) jobs
                if (j.Status?.Active is null or 0) continue;

                foreach (var vol in j.Spec?.Template?.Spec?.Volumes ?? [])
                {
                    if (vol.PersistentVolumeClaim?.ClaimName is { } pvcName)
                        claimedPvcs.Add(pvcName);
                }
            }

            var available = _options.KiroPvcPool.FirstOrDefault(pvc => !claimedPvcs.Contains(pvc));
            if (available is null)
                Log.Warning("ModelFetchJobService: all {Count} PVCs are in use by running jobs; " +
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

    /// <summary>
    /// Reads pod logs with exponential backoff retries to handle kubelet log flush lag.
    /// Retries 3 times (delays: 1s, 2s, 4s) before returning empty-output error.
    /// </summary>
    private async Task<(IReadOnlyList<AgentModelInfo> Models, string? Error)> ReadLogsWithRetryAsync(
        string podName, string jobName)
    {
        var delays = new[] { 1000, 2000, 4000 };

        for (var attempt = 0; attempt <= delays.Length; attempt++)
        {
            if (attempt > 0)
            {
                Log.Debug("ModelFetchJobService: pod log retry {Attempt}/{Max} for {PodName}",
                    attempt, delays.Length, podName);
                await Task.Delay(delays[attempt - 1]);
            }

            var logs = await _kubeClient.ReadPodLogsAsync(podName, _options.Namespace, CancellationToken.None);
            var (models, error) = ParseModelList(logs);

            // If we got a non-empty result or it's not the "no output" transient error, return immediately
            if (models.Count > 0 || (error is not null && !error.Contains("no output", StringComparison.OrdinalIgnoreCase)))
                return (models, error);

            // "no output" on last attempt — surface it
            if (attempt == delays.Length)
            {
                Log.Warning("ModelFetchJobService: pod {PodName} returned empty logs after {Attempts} attempts",
                    podName, attempt + 1);
                return (models, error);
            }
        }

        return ([], "Fetch models pod completed but returned no output.");
    }

    private V1Job BuildFetchModelsJob(JobTemplate template, string jobName, string? pvcName)
    {
        var isKiro = string.Equals(template.ProviderType, "kiro", StringComparison.OrdinalIgnoreCase);

        // ── Env vars (minimal — no AGENT_ID needed, just orchestrator URL for connectivity) ──
        var envVars = new List<V1EnvVar>
        {
            new() { Name = AgentDefaults.EnvOrchestratorUrl, Value = _options.OrchestratorUrl },
        };

        var logLevel = Environment.GetEnvironmentVariable(AgentDefaults.EnvLogLevel);
        if (!string.IsNullOrEmpty(logLevel))
            envVars.Add(new V1EnvVar { Name = AgentDefaults.EnvLogLevel, Value = logLevel });

        // ── Volumes & mounts ─────────────────────────────────────────────
        var volumeMounts = new List<V1VolumeMount>();
        var volumes = new List<V1Volume>();

        if (isKiro && pvcName is not null)
        {
            volumeMounts.Add(new V1VolumeMount
            {
                Name = "kiro-cli-data",
                MountPath = "/home/ubuntu/.local/share/kiro-cli"
            });
            volumes.Add(new V1Volume
            {
                Name = "kiro-cli-data",
                PersistentVolumeClaim = new V1PersistentVolumeClaimVolumeSource { ClaimName = pvcName }
            });
        }

        // ── Container ────────────────────────────────────────────────────
        var container = new V1Container
        {
            Name = "agent",
            Image = template.Image,
            ImagePullPolicy = template.ImagePullPolicy,
            // Override the normal worker entrypoint with a direct CLI command.
            // The worker image's default entrypoint is the agent binary; we bypass it
            // by providing explicit command/args that invoke kiro-cli directly.
            Command = ["/bin/sh", "-c"],
            Args = [
                $"{AgentDefaults.KiroCliPath} chat --list-models --format json"
            ],
            Env = envVars,
            VolumeMounts = volumeMounts,
            SecurityContext = new V1SecurityContext
            {
                Capabilities = new V1Capabilities { Drop = ["ALL"] }
            }
        };

        if (template.Resources is not null)
        {
            // Use lighter resources for model fetch (CPU-light, short-lived)
            container.Resources = new V1ResourceRequirements
            {
                Requests = template.Resources.Requests?
                    .ToDictionary(kv => kv.Key, kv => new ResourceQuantity(kv.Value)),
                Limits = template.Resources.Limits?
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
                    ["app.kubernetes.io/component"] = "model-fetch",
                    ["caa/job-type"] = JobTypeLabel
                }
            },
            Spec = new V1JobSpec
            {
                Parallelism = 1,
                Completions = 1,
                BackoffLimit = 0,            // don't retry on failure — surface error immediately
                ActiveDeadlineSeconds = _pollTimeoutSeconds + 30,
                TtlSecondsAfterFinished = 300, // 5-minute safety TTL if cleanup fails
                Template = new V1PodTemplateSpec
                {
                    Spec = new V1PodSpec
                    {
                        ServiceAccountName = _options.AgentServiceAccountName,
                        RestartPolicy = "Never",
                        TerminationGracePeriodSeconds = 10,
                        SecurityContext = new V1PodSecurityContext
                        {
                            RunAsNonRoot = true,
                            SeccompProfile = new V1SeccompProfile { Type = "RuntimeDefault" }
                        },
                        Containers = [container],
                        Volumes = volumes,
                        NodeSelector = template.NodeSelector
                    }
                }
            }
        };
    }

    private static (IReadOnlyList<AgentModelInfo> Models, string? Error) ParseModelList(string logs)
    {
        if (string.IsNullOrWhiteSpace(logs))
            return ([], "Fetch models pod completed but returned no output.");

        try
        {
            using var doc = JsonDocument.Parse(logs, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (!doc.RootElement.TryGetProperty("models", out var modelsArray))
                return ([], "Fetch models output is missing the 'models' array.");

            var result = new List<AgentModelInfo>();
            foreach (var m in modelsArray.EnumerateArray())
            {
                var modelId = m.TryGetProperty("model_id", out var id) ? id.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(modelId)) continue;

                result.Add(new AgentModelInfo
                {
                    ModelId = modelId,
                    Description = m.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    RateMultiplier = m.TryGetProperty("rate_multiplier", out var r) ? r.GetDouble() : 1.0
                });
            }

            return result.Count == 0
                ? ([], "Fetch models returned an empty model list.")
                : (result, null);
        }
        catch (JsonException ex)
        {
            return ([], $"Fetch models output could not be parsed as JSON: {ex.Message}");
        }
    }
}
