using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Pipeline;
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

        // Only active (non-terminal) jobs count toward the limit (issue #2176).
        var activeConcurrency = await DispatchLoopHelpers.BuildConcurrencyMapAsync(
            _k8sClient, _options.Namespace, nameof(ConsolidationDispatchLoop), ct);

        foreach (var item in pending)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessItemAsync(item, activeConcurrency, ct);
        }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

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

        // PVC assignment for kiro agents — the critical section must span SelectAvailablePvcAsync
        // through CreateJobAsync to close the cross-loop TOCTOU race: without holding the lock
        // until the K8s Job exists, a concurrent DispatchLoop or ConsolidationDispatchLoop cycle
        // can observe the same free PVC (no Job yet) and mount it in a second pod (issue #2176).
        // WaitAsync is called inside the try so an OperationCanceledException thrown before
        // acquisition does not trigger Release() — see PvcSelectLock XML doc for the invariant.
        var isKiroAgent = string.Equals(template.ProviderType, "kiro", StringComparison.OrdinalIgnoreCase);
        string? pvcName = null;

        ConsolidationWorkItemClaimResponse? claimed;

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
                        "ConsolidationDispatchLoop: no PVC available for kiro agent {Id}, holding item in Pending until next cycle",
                        item.Id);
                    // Do NOT call SafeRequeueAsync — the item is already Pending and must remain there.
                    // Calling RequeueAsync increments RetryCount on every starvation cycle, corrupting the
                    // field (issue #2129). Simply return; the next dispatch cycle will retry.
                    return;
                }

                // Claim and create Job inside the lock so no concurrent loop can observe the same
                // free PVC between SelectAvailablePvcAsync and CreateJobAsync.
                //
                // TODO [WARNING]: ClaimAsync is called while holding _pvcSelectLock. A slow or
                // timed-out HTTP round-trip to the orchestrator API serializes ALL kiro dispatch items
                // (both ConsolidationDispatchLoop and DispatchLoop) for the full latency of this call.
                // Under sustained orchestrator API latency every kiro item in the cycle queues behind
                // this single HTTP call, degrading throughput. To fix, perform ClaimAsync before
                // acquiring the lock; if Job creation then fails, issue a compensating unclaim call
                // to release the item. Alternatively, narrow the critical section to
                // SelectAvailablePvcAsync + CreateJobAsync only (release and re-acquire around ClaimAsync).

                // Claim (API does payload enrichment + token vending server-side)
                try
                {
                    claimed = await _consolidationClient.ClaimAsync(
                        item.Id,
                        new ClaimWorkItemRequest
                        {
                            AssignedAgentId = jobName,
                            K8sJobName = jobName,
                            DispatchedAt = DateTimeOffset.UtcNow
                        },
                        ct);
                }
                catch (WorkItemNotFoundException ex)
                {
                    Log.Warning(ex, "ConsolidationDispatchLoop: WorkItem {Id} not found during claim (404) — skipping", item.Id);
                    return;
                }

                if (claimed is null)
                {
                    Log.Debug("ConsolidationDispatchLoop: WorkItem {Id} already claimed by another instance (409), skipping", item.Id);
                    return;
                }

                // Build K8s Job spec — pass ProjectSecrets so JobSpecBuilder creates the volume mount
                var buildContextKiro = new JobSpecBuilder.BuildContext
                {
                    WorkItemId = item.Id,
                    AgentSelector = selector,
                    TimeoutSeconds = item.TimeoutSeconds > 0
                        ? item.TimeoutSeconds
                        : (int)PipelineConstants.DefaultAgentTimeout.TotalSeconds,
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

                var jobKiro = JobSpecBuilder.Build(template, buildContextKiro);

                try
                {
                    await _k8sClient.CreateJobAsync(jobKiro, _options.Namespace, ct);
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
            // Non-kiro agents do not use a PVC — claim and create Job without acquiring the lock.

            // Claim (API does payload enrichment + token vending server-side)
            try
            {
                claimed = await _consolidationClient.ClaimAsync(
                    item.Id,
                    new ClaimWorkItemRequest
                    {
                        AssignedAgentId = jobName,
                        K8sJobName = jobName,
                        DispatchedAt = DateTimeOffset.UtcNow
                    },
                    ct);
            }
            catch (WorkItemNotFoundException ex)
            {
                Log.Warning(ex, "ConsolidationDispatchLoop: WorkItem {Id} not found during claim (404) — skipping", item.Id);
                return;
            }

            if (claimed is null)
            {
                Log.Debug("ConsolidationDispatchLoop: WorkItem {Id} already claimed by another instance (409), skipping", item.Id);
                return;
            }

            // Build K8s Job spec — pass ProjectSecrets so JobSpecBuilder creates the volume mount
            var buildContext = new JobSpecBuilder.BuildContext
            {
                WorkItemId = item.Id,
                AgentSelector = selector,
                TimeoutSeconds = item.TimeoutSeconds > 0
                    ? item.TimeoutSeconds
                    : (int)PipelineConstants.DefaultAgentTimeout.TotalSeconds,
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
    /// Delegates to <see cref="JobNameFactory.ForConsolidation"/> — the canonical definition of this format.
    /// Uses "caa-cons-" prefix to distinguish consolidation Jobs from regular agent Jobs.
    /// Format stays under the K8s 63-char label limit.
    /// </summary>
    internal static string GenerateJobName(Guid workItemId) =>
        JobNameFactory.ForConsolidation(workItemId);
}
