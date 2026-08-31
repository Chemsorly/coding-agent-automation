using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using k8s.Models; // TODO: This using directive is unused — V1Job is no longer referenced directly in this file after the refactor (it is only used inside TryCreateK8sJobAsync's implementation). Remove once confirmed safe.
using Serilog;

namespace CodingAgentWebUI.JobController.Dispatch;

/// <summary>
/// Core poll-claim-create logic for the Job Controller dispatch cycle.
/// Called once per poll cycle by <see cref="DispatchService"/>.
/// Stateless aside from the startup-validation flag.
/// </summary>
public sealed class DispatchLoop
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<DispatchLoop>();

    private readonly IPipelineApiWorkItemClient _workItemClient;
    private readonly IPipelineApiConfigClient _configClient;
    private readonly IKubernetesJobClient _k8sClient;
    private readonly JobTemplateStore _templateStore;
    private readonly DispatchServiceOptions _options;
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

        // Refresh concurrency map from live K8s Jobs each cycle.
        // Only active (non-terminal) jobs count toward the limit (issue #2176).
        var activeConcurrency = await DispatchLoopHelpers.BuildConcurrencyMapAsync(
            _k8sClient, _options.Namespace, nameof(DispatchLoop), ct);

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

        // PVC assignment for kiro agents — the critical section must span SelectAvailablePvcAsync
        // through CreateJobAsync to close the cross-loop TOCTOU race: without holding the lock
        // until the K8s Job exists, a concurrent DispatchLoop or ConsolidationDispatchLoop cycle
        // can observe the same free PVC (no Job yet) and mount it in a second pod (issue #2176).
        // WaitAsync is called inside the try so an OperationCanceledException thrown before
        // acquisition does not trigger Release() — see PvcSelectLock XML doc for the invariant.
        var isKiroAgent = string.Equals(template.ProviderType, "kiro", StringComparison.OrdinalIgnoreCase);
        string? pvcName = null;
        WorkItemClaimResponse? claimed;
        bool created;

        if (isKiroAgent)
        {
            var acquired = false;
            try
            {
                await _pvcSelectLock.WaitAsync(ct);
                acquired = true;

                pvcName = await DispatchLoopHelpers.SelectAvailablePvcAsync(
                    _k8sClient, _options.Namespace, _options.KiroPvcPool, ct);

                if (pvcName is null)
                {
                    Log.Information(
                        "DispatchLoop: no PVC available for kiro agent {Id}, holding item in Pending until next cycle",
                        item.Id);
                    // Do NOT call SafeRequeueAsync — the item is already Pending and must remain there.
                    // Calling RequeueAsync increments RetryCount on every starvation cycle, corrupting the
                    // field (issue #2129). Simply return; the next dispatch cycle will retry.
                    return;
                }

                // Claim and create Job inside the lock so no concurrent loop can observe the same
                // free PVC between SelectAvailablePvcAsync and CreateJobAsync.
                claimed = await TryClaimWorkItemAsync(item.Id, jobName, ct);
                if (claimed is null) return;

                // Build and create K8s Job
                // Do NOT set DerivedKeySecretName — work-item pods use the master key file mount.
                // Key derivation happens inside the agent at runtime: HubConnectionManager and
                // WorkItemHttpClient both call HMAC(AGENT_API_KEY, AGENT_ID) internally.
                // Setting DerivedKeySecretName would cause double-derivation: the pod would
                // receive an already-derived key and the agent would derive it again.
                var buildContextKiro = new JobSpecBuilder.BuildContext
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

                created = await TryCreateK8sJobAsync(item.Id, jobName, template, buildContextKiro, ct);
                if (!created) return;
            }
            finally
            {
                if (acquired) _pvcSelectLock.Release();
            }
        }
        else
        {
            // Non-kiro agents do not use a PVC — claim and create Job without acquiring the lock.
            claimed = await TryClaimWorkItemAsync(item.Id, jobName, ct);
            if (claimed is null) return;

            // Build and create K8s Job
            // Do NOT set DerivedKeySecretName — work-item pods use the master key file mount.
            // Key derivation happens inside the agent at runtime: HubConnectionManager and
            // WorkItemHttpClient both call HMAC(AGENT_API_KEY, AGENT_ID) internally.
            // Setting DerivedKeySecretName would cause double-derivation: the pod would
            // receive an already-derived key and the agent would derive it again.
            var buildContext = new JobSpecBuilder.BuildContext
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

            created = await TryCreateK8sJobAsync(item.Id, jobName, template, buildContext, ct);
            if (!created) return;
        }

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
