using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Pipeline.Models;
using k8s.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.JobController.Dispatch;

/// <summary>
/// Core poll-claim-create logic for consolidation WorkItem dispatch.
/// Called once per poll cycle by <see cref="ConsolidationDispatchService"/>.
///
/// Stateless — no EF, no direct DB access. All domain operations (payload enrichment,
/// token vending, run status transitions) are delegated to the API via
/// <see cref="IPipelineApiConsolidationWorkItemClient"/>. K8s Job creation runs here
/// because the JC has the required K8s RBAC.
///
/// Pattern mirrors <see cref="DispatchLoop"/> for regular WorkItems.
/// </summary>
public sealed class ConsolidationDispatchLoop
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ConsolidationDispatchLoop>();

    private readonly IPipelineApiConsolidationWorkItemClient _consolidationClient;
    private readonly IKubernetesJobClient _k8sClient;
    private readonly JobTemplateStore _templateStore;
    private readonly DispatchServiceOptions _options;

    /// <summary>
    /// Shared process-wide lock that guards the check-available-PVC → create-K8s-Job critical
    /// section across BOTH <see cref="ConsolidationDispatchLoop"/> and <see cref="DispatchLoop"/>.
    /// A single <see cref="PvcSelectLock"/> singleton is injected by DI so that the two loops
    /// cannot race each other and select the same free PVC concurrently (cross-loop TOCTOU).
    /// </summary>
    private readonly PvcSelectLock _pvcSelectLock;

    public ConsolidationDispatchLoop(
        IPipelineApiConsolidationWorkItemClient consolidationClient,
        IKubernetesJobClient k8sClient,
        JobTemplateStore templateStore,
        DispatchServiceOptions options,
        PvcSelectLock pvcSelectLock)
    {
        ArgumentNullException.ThrowIfNull(consolidationClient);
        ArgumentNullException.ThrowIfNull(k8sClient);
        ArgumentNullException.ThrowIfNull(templateStore);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pvcSelectLock);
        _consolidationClient = consolidationClient;
        _k8sClient = k8sClient;
        _templateStore = templateStore;
        _options = options;
        _pvcSelectLock = pvcSelectLock;
    }

    /// <summary>
    /// Runs one full consolidation dispatch cycle:
    /// 1. Fetch pending consolidation WorkItems from API
    /// 2. Refresh concurrency map from live K8s Jobs
    /// 3. For each item: check concurrency → claim (with server-side enrichment) → create Job
    /// 4. On success: transition ConsolidationRun → Running
    /// 5. On K8s failure: requeue WorkItem + transition ConsolidationRun → Failed
    /// </summary>
    public async Task RunOneCycleAsync(CancellationToken ct)
    {
        IReadOnlyList<PendingWorkItemDto> pending;
        try
        {
            pending = await _consolidationClient.GetPendingAsync(maxResults: 50, ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ConsolidationDispatchLoop: failed to fetch pending consolidation items; skipping cycle");
            return;
        }

        if (pending.Count == 0)
            return;

        Log.Debug("ConsolidationDispatchLoop: {Count} pending consolidation item(s) found, building concurrency map", pending.Count);

        var activeConcurrency = await BuildConcurrencyMapAsync(ct);

        foreach (var item in pending)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessItemAsync(item, activeConcurrency, ct);
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

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

                var normalizedSelector = selectorLabel.Replace('.', ',');
                var key = JobTemplateStore.NormalizeLabels(normalizedSelector);
                map[key] = (map.TryGetValue(key, out var cnt) ? cnt : 0) + 1;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ConsolidationDispatchLoop: failed to build concurrency map; proceeding with empty map");
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
                "ConsolidationDispatchLoop: no JobTemplate for selector '{Selector}' (WorkItem {Id}). " +
                "Verify WorkDistribution:JobTemplatesPath and the ConfigMap mount.",
                selector, item.Id);
            return;
        }

        // Concurrency limit check — same logic as DispatchLoop
        if (template.MaxConcurrent > 0 &&
            (activeConcurrency.TryGetValue(selector, out var active) ? active : 0) >= template.MaxConcurrent)
        {
            Log.Information(
                "ConsolidationDispatchLoop: concurrency limit reached for selector '{Selector}' (limit={Max}, active={Active}), holding WorkItem {Id}",
                selector, template.MaxConcurrent, active, item.Id);
            return;
        }

        var jobName = GenerateJobName(item.Id);

        var isKiroAgent = string.Equals(template.ProviderType, "kiro", StringComparison.OrdinalIgnoreCase);

        string? pvcName = null;
        ConsolidationWorkItemClaimResponse? claimed;

        if (isKiroAgent)
        {
            // PVC assignment for kiro agents: query live K8s Jobs under a shared process-wide
            // semaphore to prevent TOCTOU races between ConsolidationDispatchLoop and DispatchLoop.
            // The lock wraps both the availability check AND the K8s Job creation so no other
            // dispatch loop can sneak in between "PVC available" and "Job created".
            //
            // NOTE: _pvcSelectLock.WaitAsync(ct) throws OperationCanceledException if ct is
            // cancelled before the semaphore is acquired. In that case the finally block must NOT
            // call Release() — tracking `acquired` guards against corrupting the semaphore count
            // above its maximum (1) on graceful shutdown.
            var acquired = false;
            try
            {
                await _pvcSelectLock.WaitAsync(ct);
                acquired = true;

                pvcName = await SelectAvailablePvcAsync(ct);
                if (pvcName is null)
                {
                    Log.Information(
                        "ConsolidationDispatchLoop: no PVC available for kiro agent {Id}, holding item in Pending until next cycle",
                        item.Id);
                    // Do NOT call SafeRequeueAsync — the item is already Pending and must remain there.
                    // Calling RequeueAsync increments RetryCount on every starvation cycle, corrupting
                    // the field (issue #2129). Simply return; the next dispatch cycle will retry.
                    // NOTE: _reconciliationTrigger.RequestImmediateCycle() is no longer called on PVC
                    // starvation. The old TryClaimPvcForKiroAgent path called it unconditionally to
                    // unblock stalled items immediately after a Job completes. Without it, the next
                    // dispatch opportunity is the natural 30 s reconciliation poll, introducing latency
                    // under load. Re-add the call here if this latency is unacceptable.
                    return;
                }

                // NOTE: TryClaimAsync (ClaimAsync HTTP call) is made while holding _pvcSelectLock.
                // A slow or timing-out ClaimAsync call serializes ALL kiro consolidation items for
                // the duration of the network round-trip. Consider narrowing the critical section to
                // SelectAvailablePvcAsync + CreateJobAsync only, releasing and re-acquiring the
                // semaphore around the ClaimAsync call.
                // Claim (API does payload enrichment + token vending server-side)
                claimed = await TryClaimAsync(item.Id, jobName, ct);
                if (claimed is null) return;

                // Build K8s Job spec — pass ProjectSecrets so JobSpecBuilder creates the volume mount
                var buildContext = BuildJobContext(item, selector, jobName, pvcName, claimed);

                var job = JobSpecBuilder.Build(template, buildContext);
                try
                {
                    await _k8sClient.CreateJobAsync(job, _options.Namespace, ct);
                    Log.Information("ConsolidationDispatchLoop: K8s Job {JobName} created for consolidation WorkItem {Id}", jobName, item.Id);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "ConsolidationDispatchLoop: K8s Job creation failed for WorkItem {Id}, requeuing", item.Id);
                    await SafeRequeueAsync(item.Id, claimed.RunId, $"K8s Job creation failed: {ex.Message}", ct);
                    return;
                }
            }
            finally
            {
                if (acquired) _pvcSelectLock.Release();
            }
        }
        else
        {
            // Non-kiro agents: no PVC required — claim and create without the PVC select lock.
            claimed = await TryClaimAsync(item.Id, jobName, ct);
            if (claimed is null) return;

            var buildContext = BuildJobContext(item, selector, jobName, pvcName: null, claimed);
            var job = JobSpecBuilder.Build(template, buildContext);
            try
            {
                await _k8sClient.CreateJobAsync(job, _options.Namespace, ct);
                Log.Information("ConsolidationDispatchLoop: K8s Job {JobName} created for consolidation WorkItem {Id}", jobName, item.Id);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ConsolidationDispatchLoop: K8s Job creation failed for WorkItem {Id}, requeuing", item.Id);
                await SafeRequeueAsync(item.Id, claimed.RunId, $"K8s Job creation failed: {ex.Message}", ct);
                return;
            }
        }

        // Create project-secrets K8s Secret (owner-referenced to Job) if secrets were returned
        if (claimed.ProjectSecrets is { Count: > 0 })
            await SafeCreateProjectSecretsAsync(jobName, item.Id, claimed.ProjectSecrets, ct);

        activeConcurrency[selector] = (activeConcurrency.TryGetValue(selector, out var curr) ? curr : 0) + 1;

        // Transition ConsolidationRun Queued → Running (best-effort; non-fatal if it fails)
        if (!string.IsNullOrEmpty(claimed.RunId))
            await SafeTransitionRunAsync(claimed.RunId, ConsolidationRunStatus.Running, null, ct);
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
        PendingWorkItemDto item,
        string selector,
        string jobName,
        string? pvcName,
        ConsolidationWorkItemClaimResponse claimed) =>
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
            ProjectSecrets = claimed.ProjectSecrets,
            TraceParent = item.TraceParent
        };

    /// <summary>
    /// Claims the consolidation work item. Returns <c>null</c> on 409 (already claimed) or 404 (deleted).
    /// </summary>
    private async Task<ConsolidationWorkItemClaimResponse?> TryClaimAsync(
        Guid workItemId, string jobName, CancellationToken ct)
    {
        try
        {
            var claimed = await _consolidationClient.ClaimAsync(
                workItemId,
                new ClaimWorkItemRequest
                {
                    AssignedAgentId = jobName,
                    K8sJobName = jobName,
                    DispatchedAt = DateTimeOffset.UtcNow
                },
                ct);

            if (claimed is null)
                Log.Debug("ConsolidationDispatchLoop: WorkItem {Id} already claimed by another instance (409), skipping", workItemId);

            return claimed;
        }
        catch (WorkItemNotFoundException ex)
        {
            Log.Warning(ex, "ConsolidationDispatchLoop: WorkItem {Id} not found during claim (404) — skipping", workItemId);
            return null;
        }
    }

    // ── Project-secrets K8s Secret ─────────────────────────────────────────────

    private async Task SafeCreateProjectSecretsAsync(
        string jobName, Guid workItemId, Dictionary<string, string> secrets, CancellationToken ct)
    {
        try
        {
            var secretName = $"caa-secrets-{workItemId.ToString("N")[..8]}";

            // Read Job to get UID for owner-reference
            string? jobUid = null;
            try
            {
                var existingJob = await _k8sClient.ReadJobAsync(jobName, _options.Namespace, ct);
                jobUid = existingJob?.Metadata?.Uid;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "ConsolidationDispatchLoop: could not read Job UID for {JobName} — project-secrets Secret will have no owner reference", jobName);
            }

            var secret = new V1Secret
            {
                Metadata = new V1ObjectMeta
                {
                    Name = secretName,
                    NamespaceProperty = _options.Namespace,
                    OwnerReferences = jobUid is not null
                        ?
                        [
                            new V1OwnerReference
                            {
                                ApiVersion = "batch/v1",
                                Kind = "Job",
                                Name = jobName,
                                Uid = jobUid
                            }
                        ]
                        : null
                },
                StringData = secrets
            };

            await _k8sClient.CreateSecretAsync(secret, _options.Namespace, ct);
            Log.Debug("ConsolidationDispatchLoop: created project-secrets Secret {SecretName} for Job {JobName}", secretName, jobName);
        }
        catch (k8s.Autorest.HttpOperationException httpEx)
            when (httpEx.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Already exists — idempotent
        }
        catch (Exception ex)
        {
            // Non-fatal: job can still run without project secrets in degraded mode
            Log.Warning(ex, "ConsolidationDispatchLoop: failed to create project-secrets Secret for Job {JobName} — continuing without it", jobName);
        }
    }

    // ── Run status transitions ─────────────────────────────────────────────────

    private async Task SafeTransitionRunAsync(
        string runId, ConsolidationRunStatus status, string? summary, CancellationToken ct)
    {
        try
        {
            await _consolidationClient.TransitionRunAsync(runId, status, summary, ct);
            Log.Information("ConsolidationDispatchLoop: ConsolidationRun {RunId} → {Status}", runId, status);
        }
        catch (OperationCanceledException oce)
        {
            Log.Debug(oce, "ConsolidationDispatchLoop: run transition for {RunId} cancelled (shutdown)", runId);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ConsolidationDispatchLoop: failed to transition ConsolidationRun {RunId} → {Status} (non-fatal)", runId, status);
        }
    }

    // ── Requeue + cascade failure ──────────────────────────────────────────────

    private async Task SafeRequeueAsync(Guid workItemId, string runId, string errorMessage, CancellationToken ct)
    {
        try
        {
            await _consolidationClient.RequeueAsync(workItemId, ct);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ConsolidationDispatchLoop: failed to requeue consolidation WorkItem {Id}", workItemId);
        }

        if (!string.IsNullOrEmpty(runId))
            await SafeTransitionRunAsync(runId, ConsolidationRunStatus.Failed, $"WorkItem dispatch failed: {errorMessage}", ct);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a deterministic K8s Job name from a WorkItem ID.
    /// Uses "caa-cons-" prefix to distinguish consolidation Jobs from regular agent Jobs.
    /// Format stays under the K8s 63-char label limit.
    /// </summary>
    internal static string GenerateJobName(Guid workItemId) =>
        $"caa-cons-{workItemId:N}"[..21]; // "caa-cons-" + 12 hex chars = 21 chars
}
