using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Pipeline.Models;
using k8s.Models;
using Serilog;

namespace CodingAgentWebUI.JobController.Dispatch;

/// <summary>
/// Core poll-claim-create logic for the Job Controller dispatch cycle.
/// Called once per poll cycle by <see cref="DispatchService"/>.
/// Stateless aside from the in-memory PVC pool and startup-validation flag.
/// </summary>
public sealed class DispatchLoop
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<DispatchLoop>();

    private readonly IPipelineApiWorkItemClient _workItemClient;
    private readonly IPipelineApiConfigClient _configClient;
    private readonly IKubernetesJobClient _k8sClient;
    private readonly JobTemplateStore _templateStore;
    private readonly PvcPool _pvcPool;
    private readonly DispatchServiceOptions _options;

    private bool _startupValidationDone;

    public DispatchLoop(
        IPipelineApiWorkItemClient workItemClient,
        IPipelineApiConfigClient configClient,
        IKubernetesJobClient k8sClient,
        JobTemplateStore templateStore,
        PvcPool pvcPool,
        DispatchServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(workItemClient);
        ArgumentNullException.ThrowIfNull(configClient);
        ArgumentNullException.ThrowIfNull(k8sClient);
        ArgumentNullException.ThrowIfNull(templateStore);
        ArgumentNullException.ThrowIfNull(pvcPool);
        ArgumentNullException.ThrowIfNull(options);
        _workItemClient = workItemClient;
        _configClient = configClient;
        _k8sClient = k8sClient;
        _templateStore = templateStore;
        _pvcPool = pvcPool;
        _options = options;
    }

    /// <summary>
    /// Runs one full dispatch cycle:
    /// 1. Startup validation (once)
    /// 2. Fetch pending work items
    /// 3. Refresh concurrency map from live K8s Jobs
    /// 4. For each item: check concurrency → claim → create Job → label-swap
    /// </summary>
    public async Task RunOneCycleAsync(CancellationToken ct)
    {
        await RunStartupValidationAsync(ct);

        var pending = await _workItemClient.GetPendingAsync(maxResults: 50, ct);
        if (pending.Count == 0)
            return;

        // Refresh concurrency map from live K8s Jobs each cycle
        var activeConcurrency = await BuildConcurrencyMapAsync(ct);

        foreach (var item in pending)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessItemAsync(item, activeConcurrency, ct);
        }
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    private async Task RunStartupValidationAsync(CancellationToken ct)
    {
        if (_startupValidationDone) return;
        _startupValidationDone = true;

        try
        {
            var profiles = await _configClient.GetAgentProfilesAsync(ct);
            foreach (var profile in profiles)
            {
                var selector = JobTemplateStore.NormalizeLabels(string.Join(",", profile.MatchLabels));
                if (_templateStore.Resolve(selector) is null)
                {
                    Log.Warning("AgentProfile '{Profile}' has no matching JobTemplate for selector '{Selector}'",
                        profile.DisplayName, selector);
                }
            }
            Log.Information("Dispatch startup validation complete. Profiles checked: {Count}", profiles.Count);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Dispatch startup validation failed; continuing without it");
        }
    }

    private async Task<Dictionary<string, int>> BuildConcurrencyMapAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            var jobs = await _k8sClient.ListJobsAsync(
                _options.Namespace,
                "app.kubernetes.io/managed-by=caa-orchestrator",
                ct);

            foreach (var job in jobs.Items)
            {
                var labels = job.Metadata?.Labels;
                var selectorLabel = labels is not null && labels.TryGetValue("caa/agent-selector", out var lv) ? lv : "";
                if (string.IsNullOrEmpty(selectorLabel)) continue;

                // Label stores dots (e.g. "dotnet10.kiro"); convert back to comma-separated form
                var normalizedSelector = selectorLabel.Replace('.', ',');
                var key = JobTemplateStore.NormalizeLabels(normalizedSelector);
                map[key] = (map.TryGetValue(key, out var cnt) ? cnt : 0) + 1;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to build concurrency map from live Jobs; proceeding with empty map");
        }
        return map;
    }

    private async Task ProcessItemAsync(
        PendingWorkItemDto item,
        Dictionary<string, int> activeConcurrency,
        CancellationToken ct)
    {
        var selector = JobTemplateStore.NormalizeLabels(item.AgentSelector);
        var template = _templateStore.Resolve(selector);
        if (template is null)
        {
            Log.Warning("No JobTemplate for selector '{Selector}', skipping {Id}", selector, item.Id);
            return;
        }

        // Concurrency limit check
        if (template.MaxConcurrent > 0 &&
            (activeConcurrency.TryGetValue(selector, out var active) ? active : 0) >= template.MaxConcurrent)
        {
            Log.Debug("Concurrency limit reached for selector '{Selector}' ({Max}), skipping {Id}",
                selector, template.MaxConcurrent, item.Id);
            return;
        }

        // Claim the item (atomic — 409 if already claimed)
        var claimed = await _workItemClient.ClaimAsync(
            item.Id,
            new ClaimWorkItemRequest { DispatchedAt = DateTimeOffset.UtcNow },
            ct);

        if (claimed is null)
        {
            Log.Debug("WorkItem {Id} already claimed by another instance (409), skipping", item.Id);
            return;
        }

        // PVC assignment for kiro agents
        string? pvcName = null;
        var isKiroAgent = string.Equals(template.ProviderType, "kiro", StringComparison.OrdinalIgnoreCase);
        if (isKiroAgent)
        {
            pvcName = _pvcPool.TryClaim(item.Id);
            if (pvcName is null)
            {
                Log.Warning("No available PVC for kiro agent {Id}, requeuing", item.Id);
                await SafeRequeueAsync(item.Id, ct);
                return;
            }
        }

        // Derive per-agent key (Spec 043 Req 8a.1).
        // The agent ID for a work-item pod equals the K8s Job name (set as AGENT_ID via
        // metadata.name field ref in the pod spec). We must derive using the Job name so the
        // key matches what AgentApiKeyAuthHandler re-derives when the agent authenticates.
        var jobName = GenerateJobName(item.Id);
        var derivedKey = DeriveAgentKey(_options.AgentMasterApiKey, jobName);
        var derivedSecretName = GenerateDerivedKeySecretName(item.Id);

        // Build and create K8s Job
        var buildContext = new JobSpecBuilder.BuildContext
        {
            WorkItemId = item.Id,
            AgentSelector = selector,
            TimeoutSeconds = _options.ChatSessionMaxDurationSeconds,
            JobName = jobName,
            ClaimedPvc = pvcName,
            OrchestratorUrl = _options.OrchestratorUrl,
            AgentApiKeySecretName = _options.AgentApiKeySecretName,
            AgentServiceAccountName = _options.AgentServiceAccountName,
            Namespace = _options.Namespace,
            OpencodeConfigSecretName = _options.OpencodeConfigSecretName,
            DerivedKeySecretName = derivedSecretName
        };

        var job = JobSpecBuilder.Build(template, buildContext);

        try
        {
            await _k8sClient.CreateJobAsync(job, _options.Namespace, ct);
            Log.Information("K8s Job {JobName} created for WorkItem {Id}", jobName, item.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "K8s Job creation failed for WorkItem {Id}, requeuing", item.Id);
            if (pvcName is not null) _pvcPool.Release(pvcName);
            await SafeRequeueAsync(item.Id, ct);
            return;
        }

        // Create per-Job derived-key Secret with ownerReference to the Job (Spec 043 Req 8a.3).
        // ownerReference ensures the Secret is garbage-collected with the Job.
        // We must first read the Job to obtain its UID.
        try
        {
            V1Job createdJob;
            try
            {
                createdJob = await _k8sClient.ReadJobAsync(jobName, _options.Namespace, ct);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to read Job {JobName} after creation to get UID; derived-key Secret not created", jobName);
                // Non-fatal from dispatch perspective — agent pod will fail to start without the Secret,
                // but ReconciliationService will detect the failed pod and mark the WorkItem as Failed.
                activeConcurrency[selector] = (activeConcurrency.TryGetValue(selector, out var curr2) ? curr2 : 0) + 1;
                goto LabelSwap;
            }

            var secret = new V1Secret
            {
                Metadata = new V1ObjectMeta
                {
                    Name = derivedSecretName,
                    NamespaceProperty = _options.Namespace,
                    OwnerReferences =
                    [
                        new V1OwnerReference
                        {
                            ApiVersion = "batch/v1",
                            Kind = "Job",
                            Name = createdJob.Metadata.Name,
                            Uid = createdJob.Metadata.Uid,
                            BlockOwnerDeletion = true
                        }
                    ]
                },
                StringData = new Dictionary<string, string>
                {
                    ["agent-api-key"] = derivedKey
                }
            };

            await _k8sClient.CreateSecretAsync(secret, _options.Namespace, ct);
            Log.Debug("Derived-key Secret {SecretName} created for WorkItem {Id}", derivedSecretName, item.Id);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to create derived-key Secret {SecretName} for WorkItem {Id}", derivedSecretName, item.Id);
            // Agent pod will fail to start; ReconciliationService handles cleanup.
        }

        LabelSwap:
        activeConcurrency[selector] = (activeConcurrency.TryGetValue(selector, out var curr) ? curr : 0) + 1;

        // Label swap — non-fatal; job is already running
        try
        {
            await _workItemClient.PostLabelSwapAsync(item.Id, "agent:in-progress", ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Label swap failed for WorkItem {Id} after Job creation — swallowing", item.Id);
        }
    }

    private async Task SafeRequeueAsync(Guid workItemId, CancellationToken ct)
    {
        try
        {
            await _workItemClient.RequeueAsync(workItemId, ct);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to requeue WorkItem {Id}", workItemId);
        }
    }

    /// <summary>
    /// Derives a per-agent API key via HMAC-SHA256(masterKey, agentId).
    /// Returns lowercase hex — matches the format validated by AgentApiKeyAuthHandler
    /// which calls Convert.ToHexString(hash).ToLowerInvariant() on the re-derived key.
    /// </summary>
    /// <param name="masterKey">Master HMAC key (from AGENT_API_KEY / AgentMasterApiKey).</param>
    /// <param name="agentId">
    /// The K8s Job name (e.g. "caa-agent-7f3a9b2e1c4") — NOT the work-item GUID.
    /// Pods read this as AGENT_ID from the metadata.name field ref; the auth handler
    /// receives it via the ?agentId= query parameter.
    /// </param>
    internal static string DeriveAgentKey(string masterKey, string agentId)
    {
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(masterKey);
        var dataBytes = System.Text.Encoding.UTF8.GetBytes(agentId);
        var hash = System.Security.Cryptography.HMACSHA256.HashData(keyBytes, dataBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Generates a K8s Secret name for the derived agent key.
    /// Format: caa-agent-key-{N} truncated to 32 chars to stay under K8s 63-char metadata name limit.
    /// </summary>
    internal static string GenerateDerivedKeySecretName(Guid workItemId)
        => $"caa-agent-key-{workItemId:N}"[..32];

    /// <summary>
    /// Generates a deterministic K8s Job name from a WorkItem ID.
    /// Format: caa-agent-{first-8-chars-of-guid} — short enough to stay under K8s 63-char limit.
    /// </summary>
    internal static string GenerateJobName(Guid workItemId) =>
        $"caa-agent-{workItemId:N}"[..21]; // "caa-agent-" + 11 hex chars
}
