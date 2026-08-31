using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using Serilog;

namespace CodingAgentWebUI.JobController.Dispatch;

/// <summary>
/// Core poll-claim-create logic for the Job Controller dispatch cycle.
/// Called once per poll cycle by <see cref="DispatchService"/>.
/// Stateless aside from the PVC select lock and startup-validation flag.
/// </summary>
public sealed class DispatchLoop
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<DispatchLoop>();

    private readonly IPipelineApiWorkItemClient _workItemClient;
    private readonly IPipelineApiConfigClient _configClient;
    private readonly IKubernetesJobClient _k8sClient;
    private readonly JobTemplateStore _templateStore;
    private readonly DispatchServiceOptions _options;

    /// <summary>
    /// Shared process-wide lock that guards the check-available-PVC → create-K8s-Job critical
    /// section across BOTH <see cref="DispatchLoop"/> and <see cref="ConsolidationDispatchLoop"/>.
    /// A single <see cref="PvcSelectLock"/> singleton is injected by DI so that the two loops
    /// cannot race each other and select the same free PVC concurrently (cross-loop TOCTOU).
    /// </summary>
    private readonly PvcSelectLock _pvcSelectLock;

    private bool _startupValidationDone;

    public DispatchLoop(
        IPipelineApiWorkItemClient workItemClient,
        IPipelineApiConfigClient configClient,
        IKubernetesJobClient k8sClient,
        JobTemplateStore templateStore,
        DispatchServiceOptions options,
        PvcSelectLock pvcSelectLock)
    {
        ArgumentNullException.ThrowIfNull(workItemClient);
        ArgumentNullException.ThrowIfNull(configClient);
        ArgumentNullException.ThrowIfNull(k8sClient);
        ArgumentNullException.ThrowIfNull(templateStore);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pvcSelectLock);
        _workItemClient = workItemClient;
        _configClient = configClient;
        _k8sClient = k8sClient;
        _templateStore = templateStore;
        _options = options;
        _pvcSelectLock = pvcSelectLock;
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

        IReadOnlyList<PendingWorkItemDto> pending;
        try
        {
            pending = await _workItemClient.GetPendingAsync(maxResults: 50, ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to fetch pending work items from Pipeline API; skipping cycle");
            return;
        }

        if (pending.Count == 0)
        {
            Log.Debug("DispatchLoop: 0 pending work items — skipping cycle");
            return;
        }

        Log.Debug("DispatchLoop: {Count} pending work item(s) found, building concurrency map", pending.Count);

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
            Log.Error(
                "No JobTemplate for selector '{Selector}' (WorkItem {Id}). " +
                "The Job Controller cannot dispatch this item until a matching template is added. " +
                "Verify WorkDistribution:JobTemplatesPath and the ConfigMap mount.",
                selector, item.Id);
            return;
        }

        // Concurrency limit check
        var currentConcurrency = activeConcurrency.TryGetValue(selector, out var active) ? active : 0;
        if (template.MaxConcurrent > 0 && currentConcurrency >= template.MaxConcurrent)
        {
            Log.Information(
                "DispatchLoop: concurrency limit reached for selector '{Selector}' (limit={Max}, active={Active}), holding WorkItem {Id}",
                selector, template.MaxConcurrent, currentConcurrency, item.Id);
            return;
        }

        // The K8s Job name is deterministic from the WorkItem id and doubles as the pod's
        // AGENT_ID (metadata.name field ref). Compute it before claiming so the claim records
        // which agent identity owns this item — the API binds the agent's derived key to the
        // WorkItem through AssignedAgentId when serving /assignment and /status.
        var jobName = GenerateJobName(item.Id);

        var isKiroAgent = string.Equals(template.ProviderType, "kiro", StringComparison.OrdinalIgnoreCase);
        var dispatched = isKiroAgent
            ? await DispatchKiroAgentAsync(item, selector, jobName, template, ct)
            : await DispatchNonKiroAgentAsync(item, selector, jobName, template, ct);

        if (!dispatched) return;

        activeConcurrency[selector] = currentConcurrency + 1;

        // Label swap — non-fatal; job is already running
        try
        {
            await _workItemClient.PostLabelSwapAsync(item.Id, "agent:in-progress", ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Label swap failed for WorkItem {Id} after Job creation — swallowing", item.Id);
        }

        // Record dispatch metrics after successful Job creation
        WorkDistributionTelemetry.RecordDispatchLatency(
            dispatchedAt: DateTimeOffset.UtcNow,
            originalEnqueuedAt: null,
            createdAt: item.CreatedAt,
            agentSelector: item.AgentSelector);
        WorkDistributionTelemetry.DispatcherPollCount.Add(1);
    }

    /// <summary>
    /// Dispatches a kiro-type work item: selects a PVC under the shared lock, claims the item,
    /// and creates the K8s Job — all within the PVC select lock to prevent TOCTOU races.
    /// Returns <c>true</c> on success, <c>false</c> to skip this item.
    /// </summary>
    private async Task<bool> DispatchKiroAgentAsync(
        PendingWorkItemDto item, string selector, string jobName, JobTemplate template, CancellationToken ct)
    {
        // TODO [WARNING] (DotNetSpecialist): _pvcSelectLock.WaitAsync(ct) throws
        // OperationCanceledException if ct is cancelled before the semaphore is acquired.
        // In that case the finally block must NOT call Release() — tracking `acquired` guards
        // against corrupting the semaphore count above its maximum (1) on graceful shutdown.
        var acquired = false;
        try
        {
            await _pvcSelectLock.WaitAsync(ct);
            acquired = true;

            var pvcName = await SelectAvailablePvcAsync(ct);
            if (pvcName is null)
            {
                Log.Information(
                    "DispatchLoop: no PVC available for kiro agent {Id}, holding item in Pending until next cycle",
                    item.Id);
                // Do NOT call SafeRequeueAsync — the item is already Pending and must remain there.
                // Calling RequeueAsync increments RetryCount on every starvation cycle, corrupting
                // the field (issue #2129). Simply return; the next dispatch cycle will retry.
                // TODO [WARNING]: _reconciliationTrigger.RequestImmediateCycle() is no longer called
                // on PVC starvation. Re-add the call here if PVC starvation latency is unacceptable.
                return false;
            }

            // TODO [WARNING]: TryClaimWorkItemAsync (ClaimAsync HTTP call) is made while holding
            // _pvcSelectLock. A slow or timing-out ClaimAsync call serializes ALL kiro dispatch items
            // for the duration of the network round-trip (N × latency per cycle under load).
            // Accepted trade-off: throughput may degrade under sustained network latency.
            var claimed = await TryClaimWorkItemAsync(item.Id, jobName, ct);
            if (claimed is null) return false;

            // Build and create K8s Job inside the lock so no other cycle can claim the same PVC
            // between our availability check and the actual Job creation.
            var buildContext = BuildJobContext(item, selector, jobName, pvcName);
            return await TryCreateK8sJobAsync(item.Id, jobName, template, buildContext, ct);
        }
        finally
        {
            if (acquired) _pvcSelectLock.Release();
        }
    }

    /// <summary>
    /// Dispatches a non-kiro work item (no PVC required): claims the item and creates the K8s Job
    /// without acquiring the PVC select lock.
    /// Returns <c>true</c> on success, <c>false</c> to skip this item.
    /// </summary>
    private async Task<bool> DispatchNonKiroAgentAsync(
        PendingWorkItemDto item, string selector, string jobName, JobTemplate template, CancellationToken ct)
    {
        var claimed = await TryClaimWorkItemAsync(item.Id, jobName, ct);
        if (claimed is null) return false;

        var buildContext = BuildJobContext(item, selector, jobName, pvcName: null);
        return await TryCreateK8sJobAsync(item.Id, jobName, template, buildContext, ct);
    }

    /// <summary>
    /// Queries live K8s Jobs to find the first PVC name from the configured pool that is
    /// not already mounted by a running Job. Returns <c>null</c> if all configured PVCs
    /// are claimed or the pool is empty.
    /// Must be called under <see cref="_pvcSelectLock"/>.
    /// </summary>
    private async Task<string?> SelectAvailablePvcAsync(CancellationToken ct)
    {
        if (_options.KiroPvcPool.Count == 0) return null;

        var jobs = await _k8sClient.ListJobsAsync(
            _options.Namespace,
            "app.kubernetes.io/managed-by=caa-orchestrator",
            ct);

        var claimedNames = jobs.Items
            .SelectMany(j => j.Spec?.Template?.Spec?.Volumes ?? [])
            .Where(v => v.PersistentVolumeClaim?.ClaimName is not null)
            .Select(v => v.PersistentVolumeClaim!.ClaimName!)
            .ToHashSet(StringComparer.Ordinal);

        return _options.KiroPvcPool.FirstOrDefault(p => !claimedNames.Contains(p));
    }

    private JobSpecBuilder.BuildContext BuildJobContext(
        PendingWorkItemDto item, string selector, string jobName, string? pvcName) =>
        new()
        {
            WorkItemId = item.Id,
            AgentSelector = selector,
            TimeoutSeconds = Math.Max(item.TimeoutSeconds, _options.AgentJobTimeoutSeconds),
            JobName = jobName,
            ClaimedPvc = pvcName,
            OrchestratorUrl = _options.OrchestratorUrl,
            AgentApiKeySecretName = _options.AgentApiKeySecretName,
            AgentServiceAccountName = _options.AgentServiceAccountName,
            Namespace = _options.Namespace,
            OpencodeConfigSecretName = _options.OpencodeConfigSecretName,
            TraceParent = item.TraceParent
        };

    /// <summary>
    /// Atomically claims a work item. Returns the claim response on success, null to signal the
    /// caller should skip this item (contention or deletion).
    /// </summary>
    private async Task<WorkItemClaimResponse?> TryClaimWorkItemAsync(
        Guid workItemId, string jobName, CancellationToken ct)
    {
        try
        {
            var claimed = await _workItemClient.ClaimAsync(
                workItemId,
                new ClaimWorkItemRequest { AssignedAgentId = jobName, K8sJobName = jobName, DispatchedAt = DateTimeOffset.UtcNow },
                ct);

            if (claimed is null)
            {
                Log.Debug("WorkItem {Id} already claimed by another instance (409), skipping", workItemId);
            }
            return claimed;
        }
        catch (WorkItemNotFoundException)
        {
            // Item was in the pending list but no longer exists — data race (deleted between
            // GetPendingAsync and ClaimAsync) or a bug in the pending query. Skip with a
            // warning so it is distinguishable from normal 409 contention in the logs.
            Log.Warning("WorkItem {Id} not found during claim (404) — item may have been deleted between poll and claim, skipping", workItemId);
            return null;
        }
    }

    /// <summary>
    /// Creates the K8s Job for a claimed work item. Returns true on success, false on failure
    /// (in which case the work item is re-queued).
    /// </summary>
    private async Task<bool> TryCreateK8sJobAsync(
        Guid workItemId, string jobName, JobTemplate template,
        JobSpecBuilder.BuildContext buildContext, CancellationToken ct)
    {
        var job = JobSpecBuilder.Build(template, buildContext);
        try
        {
            await _k8sClient.CreateJobAsync(job, _options.Namespace, ct);
            Log.Information(
                "K8s Job {JobName} created for WorkItem {Id} (OrchestratorUrl={OrchestratorUrl})",
                jobName, workItemId, _options.OrchestratorUrl);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "K8s Job creation failed for WorkItem {Id}, requeuing", workItemId);
            await SafeRequeueAsync(workItemId, ct);
            return false;
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
    /// Generates a deterministic K8s Job name from a WorkItem ID.
    /// Format: caa-agent-{first-11-chars-of-guid-no-dashes} — short enough to stay under K8s 63-char limit.
    /// </summary>
    internal static string GenerateJobName(Guid workItemId) =>
        $"caa-agent-{workItemId:N}"[..21]; // "caa-agent-" (10) + 11 hex chars = 21 total
}
