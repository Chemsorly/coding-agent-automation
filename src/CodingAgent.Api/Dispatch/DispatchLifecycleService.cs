using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Pipeline.Models;
using k8s.Autorest;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgent.Api.Dispatch;

/// <summary>
/// Shared K8s Job dispatch lifecycle extracted from DispatchService.
/// Handles: PVC selection, WorkItem load, pre-write, K8s Job creation, secret creation,
/// race detection, status transition to Dispatched, and metric recording.
/// Used by DispatchService for regular (non-consolidation) items.
/// </summary>
internal sealed class DispatchLifecycleService : IDisposable
{
    private readonly Serilog.ILogger _log;

    private readonly SemaphoreSlim _pvcSelectLock = new(1, 1);

    private readonly IKubernetesJobClient _kubeClient;
    private readonly WorkItemTransitionService _transitionService;
    private readonly DispatchServiceOptions _options;

    public DispatchLifecycleService(
        IKubernetesJobClient kubeClient,
        WorkItemTransitionService transitionService,
        DispatchServiceOptions options,
        Serilog.ILogger? logger = null)
    {
        _kubeClient = kubeClient;
        _transitionService = transitionService;
        _options = options;
        _log = logger ?? Serilog.Log.ForContext<DispatchLifecycleService>();
    }

    /// <summary>
    /// Returns the configured Kiro PVC pool. Used by <c>POST /api/work-items/dispatch</c> to
    /// check PVC availability before creating the WorkItem.
    /// </summary>
    public IReadOnlyList<string> GetPvcPool() => _options.KiroPvcPool;

    /// <summary>
    /// Queries the database for claimed PVCs, excludes inflight claims, and returns available PVCs
    /// from the given pool. Used by DispatchService.
    /// </summary>
    /// <param name="db">Database context for querying claimed PVCs.</param>
    /// <param name="pvcPool">Configured PVC pool to check availability against.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A result containing the list of available PVCs and the count of claimed PVCs (for telemetry).
    /// </returns>
    public static async Task<PvcAvailabilityResult> QueryAvailablePvcsAsync(
        PipelineDbContext db, IReadOnlyList<string> pvcPool, CancellationToken ct)
    {
        var claimedPvcs = await db.WorkItems
            .Where(w => w.ClaimedPvcName != null &&
                        (w.Status == WorkItemStatus.Dispatched ||
                         w.Status == WorkItemStatus.Running))
            .Select(w => w.ClaimedPvcName!)
            .ToListAsync(ct);

        var availablePvcs = pvcPool
            .Except(claimedPvcs, StringComparer.Ordinal)
            .ToList();

        return new PvcAvailabilityResult(availablePvcs, claimedPvcs.Count);
    }


    /// <summary>
    /// Shared dispatch lifecycle for WorkItems.
    /// Handles: PVC selection, WorkItem load, pre-write, K8s Job creation, secret creation,
    /// race detection, status transition to Dispatched, and metric recording.
    /// Variant-specific behavior is injected via delegates.
    /// </summary>
    /// <param name="prepareVariant">
    /// Called after WorkItem is loaded. Returns (shouldContinue, projectSecrets).
    /// May mutate workItem entity fields (e.g., Payload). Return (false, null) to abort.
    /// Must handle its own error logging and FailWorkItem calls before returning false.
    /// </param>
    /// <param name="onDispatchSuccess">
    /// Called inside the final try block after successful Dispatched save.
    /// For regular: resets StartedAt + swaps label. For consolidation: transitions run to Running.
    /// </param>
    public async Task ExecuteDispatchLifecycleAsync(
        DispatchLifecycleContext ctx,
        Func<WorkItemEntity, Task<(bool shouldContinue, Dictionary<string, string>? projectSecrets)>> prepareVariant,
        Func<WorkItemEntity, Task>? onDispatchSuccess,
        CancellationToken ct,
        Func<Guid, string, Task>? onFailure = null)
    {
        var db = ctx.Db;
        var item = ctx.Item;
        var template = ctx.Template;
        var isKiroAgent = ctx.IsKiroAgent;
        var availablePvcs = ctx.AvailablePvcs;
        var concurrencyBySelector = ctx.ConcurrencyBySelector;
        var logPrefix = ctx.LogPrefix;

        // Generate deterministic job name
        var jobName = GenerateJobName(item.Id);

        // Select a PVC for kiro agents directly from the available pool (RWO makes label patching unnecessary).
        var claimedPvc = isKiroAgent ? await SelectPvcAsync(availablePvcs, item.Id, logPrefix, ct) : null;
        if (isKiroAgent && claimedPvc is null)
            return;

        // Load full WorkItem and run variant-specific preparation.
        WorkItemEntity? workItem;
        (bool shouldProceed, Dictionary<string, string>? projectSecrets) prepareResult;
        try
        {
            workItem = await db.WorkItems.FindAsync([item.Id], ct);
            if (workItem is null || workItem.Status != ctx.ExpectedInitialStatus)
            {
                // Item was modified by another process
                ReleaseClaimedPvc(claimedPvc, availablePvcs);
                return;
            }

            // Variant-specific preparation (may mutate workItem, load secrets, or signal abort)
            prepareResult = await prepareVariant(workItem);
        }
        catch
        {
            ReleaseClaimedPvc(claimedPvc, availablePvcs);
            throw;
        }
        var (shouldProceed, projectSecrets) = prepareResult;
        if (!shouldProceed)
        {
            ReleaseClaimedPvc(claimedPvc, availablePvcs);
            return;
        }

        // Pre-write K8sJobName to WorkItem BEFORE K8s API call.
        // ClaimedPvcName is NOT written here — it is set atomically with Status=Dispatched
        // in FinalizeDispatchAsync (see below) so that a Pending WorkItem never holds a
        // non-null ClaimedPvcName in the database. Pre-writing ClaimedPvcName would pin the
        // item to a specific PVC for its entire queue lifetime, causing starvation if that
        // PVC stays busy. EF change tracking also persists any entity mutations from
        // prepareVariant (e.g., Payload).
        workItem.K8sJobName = jobName;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _log.Warning(ex, "DispatchLifecycleService: concurrency conflict pre-writing {LogPrefix}K8sJobName for {WorkItemId}", logPrefix, item.Id);
            ReleaseClaimedPvc(claimedPvc, availablePvcs);
            return;
        }
        catch
        {
            ReleaseClaimedPvc(claimedPvc, availablePvcs);
            throw;
        }

        // Create K8s Job via JobSpecBuilder
        if (!await CreateK8sJobAsync(db, new K8sJobCreationContext(item, workItem, template, jobName, claimedPvc, availablePvcs, projectSecrets, logPrefix, onFailure), ct))
            return;

        // Create per-job K8s Secret if project has secrets
        await CreateJobSecretIfNeededAsync(jobName, item.Id, projectSecrets, logPrefix, ct);

        // Update to Dispatched — clear change tracker first to get fresh state
        // (avoids stale entity if another service modified the item during K8s API call)
        var (shouldContinue, reloadedWorkItem) = await HandleOrphanedJobIfRaceDetectedAsync(db, item.Id, jobName, claimedPvc, availablePvcs, logPrefix, ctx.ExpectedInitialStatus, ct);
        if (!shouldContinue)
            return;

        workItem = reloadedWorkItem!;
        workItem.Status = WorkItemStatus.Dispatched;
        workItem.DispatchedAt = DateTimeOffset.UtcNow;
        // Set ClaimedPvcName on the reloaded (actively tracked) entity so it is persisted
        // atomically with Status=Dispatched. Must target this reloaded reference — the
        // earlier workItem reference was detached when HandleOrphanedJobIfRaceDetectedAsync
        // called db.ChangeTracker.Clear() before re-fetching from the DB.
        workItem.ClaimedPvcName = claimedPvc;

        await FinalizeDispatchAsync(db, workItem, item, logPrefix, concurrencyBySelector, onDispatchSuccess, _log, ct);
    }

    /// <summary>
    /// Returns the claimed PVC to the available pool.
    /// A no-op when <paramref name="claimedPvc"/> is null (non-kiro agents never claim a PVC).
    /// </summary>
    private static void ReleaseClaimedPvc(string? claimedPvc, List<string> availablePvcs)
    {
        if (claimedPvc is not null)
            availablePvcs.Add(claimedPvc);
    }

    /// <summary>
    /// Selects a PVC from the available pool under <see cref="_pvcSelectLock"/> for a kiro agent.
    /// Returns the claimed PVC name, or null if the pool is empty (caller should return early).
    /// </summary>
    private async Task<string?> SelectPvcAsync(
        List<string> availablePvcs, Guid workItemId, string logPrefix, CancellationToken ct)
    {
        await _pvcSelectLock.WaitAsync(ct);
        try
        {
            var claimedPvc = availablePvcs.FirstOrDefault();
            if (claimedPvc is null)
            {
                _log.Information("DispatchLifecycleService: {LogPrefix}no PVC available for WorkItem {WorkItemId}, skipping",
                    logPrefix, workItemId);
                return null;
            }
            availablePvcs.Remove(claimedPvc);
            return claimedPvc;
        }
        finally
        {
            _pvcSelectLock.Release();
        }
    }

    /// <summary>
    /// Saves the Dispatched status transition, records metrics, updates concurrency tracking,
    /// and invokes the variant-specific post-dispatch success callback.
    /// </summary>
    private static async Task FinalizeDispatchAsync(
        PipelineDbContext db,
        WorkItemEntity workItem,
        PendingWorkItemProjection item,
        string logPrefix,
        Dictionary<string, int> concurrencyBySelector,
        Func<WorkItemEntity, Task>? onDispatchSuccess,
        Serilog.ILogger log,
        CancellationToken ct)
    {
        var jobName = workItem.K8sJobName!;
        try
        {
            await db.SaveChangesAsync(ct);

            // Record dispatch latency / pending duration metric
            WorkDistributionTelemetry.RecordDispatchLatency(workItem.DispatchedAt!.Value, workItem.OriginalEnqueuedAt, workItem.CreatedAt, item.AgentSelector);

            // Track concurrency
            concurrencyBySelector[item.AgentSelector ?? ""] =
                concurrencyBySelector.GetValueOrDefault(item.AgentSelector ?? "", 0) + 1;

            log.Information(
                "DispatchLifecycleService: {LogPrefix}WorkItem {WorkItemId} dispatched as Job {JobName} (selector={Selector}, pvc={Pvc})",
                logPrefix, item.Id, jobName, item.AgentSelector, workItem.ClaimedPvcName ?? "none");

            // Variant-specific post-dispatch success action
            if (onDispatchSuccess is not null)
                await onDispatchSuccess(workItem);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            log.Warning(ex, "DispatchLifecycleService: concurrency conflict updating {LogPrefix}WorkItem {WorkItemId} to Dispatched", logPrefix, item.Id);
            // Job exists in K8s — ReconciliationService will reconcile
        }
    }

    /// <summary>
    /// Fails a work item with the given error message. Transitions to Failed with InfrastructureFailure reason.
    /// </summary>
    public async Task FailWorkItemAsync(Guid workItemId, string errorMessage, CancellationToken ct)
    {
        await _transitionService.TransitionAsync(
            workItemId,
            WorkItemStatus.Failed,
            WorkItemMutationFactory.Failed(
                errorMessage: errorMessage,
                failureReason: FailureReason.InfrastructureFailure),
            ct: ct);

        _log.Warning("DispatchLifecycleService: WorkItem {WorkItemId} failed: {Error}", workItemId, errorMessage);
    }

    /// <summary>
    /// Loads project secrets from the project's Settings JSON.
    /// </summary>
    public static async Task<Dictionary<string, string>?> LoadProjectSecretsAsync(
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

    // ── K8s Job Creation ────────────────────────────────────────────────

    /// <summary>
    /// Groups the parameters of <see cref="CreateK8sJobAsync"/> related to the job being created,
    /// reducing its parameter count (S107).
    /// </summary>
    private sealed record K8sJobCreationContext(
        PendingWorkItemProjection Item,
        WorkItemEntity WorkItem,
        JobTemplate Template,
        string JobName,
        string? ClaimedPvc,
        List<string> AvailablePvcs,
        Dictionary<string, string>? ProjectSecrets,
        string LogPrefix,
        Func<Guid, string, Task>? OnFailure);

    /// <summary>
    /// Creates a K8s Job via JobSpecBuilder. Handles 409 Conflict (idempotent) and general failures
    /// (releases PVC, fails WorkItem). Returns true if job creation succeeded (or 409), false if the
    /// caller should return early due to an error.
    /// </summary>
    private async Task<bool> CreateK8sJobAsync(
        PipelineDbContext db,
        K8sJobCreationContext ctx,
        CancellationToken ct)
    {
        // Per-job credential name: caa-key-{jobName} (auto-GC'd via OwnerReference on Job).
        // Contains HMAC-SHA256(masterKey, jobName) so the pod can authenticate without holding
        // the master key. Null when master key is not configured (e.g. tests without API key env).
        string? perJobKeySecretName = null;
        if (!string.IsNullOrEmpty(_options.AgentApiKeyValue))
        {
            perJobKeySecretName = $"caa-key-{ctx.JobName}";
        }

        try
        {
            var buildCtx = new JobSpecBuilder.BuildContext
            {
                WorkItemId = ctx.Item.Id,
                AgentSelector = ctx.Item.AgentSelector,
                TimeoutSeconds = ctx.Item.TimeoutSeconds,
                JobName = ctx.JobName,
                ClaimedPvc = ctx.ClaimedPvc,
                OrchestratorUrl = _options.OrchestratorUrl,
                AgentApiKeySecretName = _options.AgentApiKeySecretName,
                AgentServiceAccountName = _options.AgentServiceAccountName,
                Namespace = _options.Namespace,
                OpencodeConfigSecretName = _options.OpencodeConfigSecretName,
                ProjectSecrets = ctx.ProjectSecrets,
                TraceParent = ctx.WorkItem.TraceParent,
                // Spec 043 Req 8a: vend per-job derived key via dedicated K8s Secret.
                // The pod receives HMAC-SHA256(masterKey, jobName) as AGENT_API_KEY — it cannot
                // compute credentials for any other job since it never sees the master key.
                // Null when master key is unavailable (degrades to legacy master-key-mount path).
                DerivedKeySecretName = perJobKeySecretName
            };
            var job = JobSpecBuilder.Build(ctx.Template, buildCtx);
            await _kubeClient.CreateJobAsync(job, _options.Namespace, ct);
        }
        catch (HttpOperationException httpEx) when (httpEx.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // 409 Conflict = Job already exists = success (idempotent)
            _log.Information(httpEx, "DispatchLifecycleService: K8s Job {JobName} already exists (409 Conflict), treating as success", ctx.JobName);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "DispatchLifecycleService: failed to create K8s Job {JobName} for {LogPrefix}WorkItem {WorkItemId}", ctx.JobName, ctx.LogPrefix, ctx.Item.Id);
            if (ctx.ClaimedPvc is not null)
            {
                ctx.WorkItem.ClaimedPvcName = null;
                ctx.AvailablePvcs.Add(ctx.ClaimedPvc);
                await db.SaveChangesAsync(ct);
            }
            try
            {
                await FailWorkItemAsync(ctx.Item.Id, $"K8s Job creation failed: {ex.Message}", ct);
            }
            catch (Exception failEx) when (failEx is not OperationCanceledException)
            {
                _log.Warning(failEx,
                    "DispatchLifecycleService: FailWorkItemAsync threw for WorkItem {WorkItemId} after K8s Job creation failure — item remains Pending for reconciliation",
                    ctx.Item.Id);
            }
            if (ctx.OnFailure is not null)
                await ctx.OnFailure(ctx.Item.Id, $"K8s Job creation failed: {ex.Message}");
            return false;
        }

        // Create the per-job API key Secret immediately after the Job so we can attach an
        // OwnerReference. The Job must exist before the Secret to enable auto-GC on Job deletion.
        // TODO(#3034): Race window — the Job is scheduled before the Secret exists. If the
        // kubelet picks up the pod in this gap, the container fails with CreateContainerConfigError
        // because AGENT_API_KEY is sourced via SecretKeyRef (not Optional=true). The container
        // restart policy will not automatically retry env-var resolution after the Secret is created.
        // Consider creating the Secret first (without OwnerReference), then creating the Job, then
        // patching the OwnerReference — or mark the SecretKeyRef as Optional=true and let the
        // agent's startup guard reject a missing key explicitly.
        if (perJobKeySecretName is not null)
        {
            await CreatePerJobKeySecretAsync(ctx.JobName, perJobKeySecretName, ct);
        }

        return true;
    }

    /// <summary>
    /// Derives the per-job API key as HMAC-SHA256(masterKey, jobName), creates a K8s Secret
    /// named <paramref name="secretName"/> containing it, and attaches an OwnerReference to
    /// the K8s Job so the Secret is auto-deleted when the Job is garbage-collected.
    /// Non-fatal: a failure is logged as a warning and the dispatch continues, but since the Job
    /// spec already references the per-job Secret via SecretKeyRef (without Optional=true), a
    /// missing Secret causes the pod to enter CreateContainerConfigError on start. There is no
    /// fallback to the master-key-mount path for work-item pods dispatched with DerivedKeySecretName.
    /// TODO(#3034): treat Secret-creation failure as a hard dispatch failure (return false from
    /// CreateK8sJobAsync, release PVC, and call FailWorkItemAsync) so the item is re-queued
    /// rather than silently blocked in CreateContainerConfigError.
    /// </summary>
    private async Task CreatePerJobKeySecretAsync(string jobName, string secretName, CancellationToken ct)
    {
        try
        {
            // Pre-compute HMAC-SHA256(masterKey, jobName) — this is the value the pod will use
            // as its bearer token. The server-side AgentApiKeyAuthHandler validates by computing
            // the same value from the master key + agentId query param.
            var derivedKey = DeriveJobKey(_options.AgentApiKeyValue, jobName);

            var jobUid = await GetJobUidAsync(jobName, ct);

            List<V1OwnerReference>? ownerReferences = null;
            if (!string.IsNullOrEmpty(jobUid))
            {
                ownerReferences =
                [
                    new V1OwnerReference
                    {
                        ApiVersion = "batch/v1",
                        Kind = "Job",
                        Name = jobName,
                        Uid = jobUid
                        // TODO(#3034): set BlockOwnerDeletion = true here to ensure the K8s GC
                        // finalizer prevents Job deletion from completing before the Secret is
                        // garbage-collected, preventing per-job Secrets from accumulating in the
                        // namespace if the GC reconciler skips the finalizer check.
                    }
                ];
            }
            else
            {
                _log.Warning("DispatchLifecycleService: creating per-job key Secret {SecretName} for Job {JobName} without OwnerReference — secret will not be auto-GC'd",
                    secretName, jobName);
            }

            var secret = new V1Secret
            {
                Metadata = new V1ObjectMeta
                {
                    Name = secretName,
                    NamespaceProperty = _options.Namespace,
                    OwnerReferences = ownerReferences
                },
                StringData = new Dictionary<string, string>
                {
                    ["agent-api-key"] = derivedKey
                }
            };

            await _kubeClient.CreateSecretAsync(secret, _options.Namespace, ct);
            _log.Debug("DispatchLifecycleService: created per-job key Secret {SecretName} for Job {JobName}", secretName, jobName);
        }
        catch (HttpOperationException httpEx) when (httpEx.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Secret already exists — idempotent (e.g. retry after 409 Job conflict)
        }
        catch (Exception ex)
        {
            // TODO(#3034): see XML doc above — this should fail the dispatch rather than continue.
            // The pod will NOT fall back to the master-key path; it will fail to start with
            // CreateContainerConfigError because the Job spec references the per-job Secret via
            // SecretKeyRef and the Secret does not exist.
            _log.Warning(ex, "DispatchLifecycleService: failed to create per-job key Secret {SecretName} for Job {JobName} — pod will fail to start (no master-key fallback for work-item pods)",
                secretName, jobName);
        }
    }

    /// <summary>
    /// Derives the per-job API key using HMAC-SHA256(masterKey, jobName).
    /// Matches the derivation used by <c>AgentApiKeyAuthHandler</c> server-side validation.
    /// </summary>
    internal static string DeriveJobKey(string masterKey, string jobName)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(masterKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(jobName));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Creates a per-job K8s Secret if the project has secrets. Handles 409 Conflict (idempotent)
    /// and treats all other failures as non-fatal warnings.
    /// </summary>
    private async Task CreateJobSecretIfNeededAsync(
        string jobName,
        Guid workItemId,
        Dictionary<string, string>? projectSecrets,
        string logPrefix,
        CancellationToken ct)
    {
        if (projectSecrets is null || projectSecrets.Count == 0)
            return;

        try
        {
            await CreateJobSecretAsync(jobName, workItemId, projectSecrets, ct);
        }
        catch (HttpOperationException httpEx) when (httpEx.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Secret already exists — idempotent
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "DispatchLifecycleService: failed to create project-secrets K8s Secret for {LogPrefix}Job {JobName}", logPrefix, jobName);
            // Non-fatal: job can still run without project secrets in degraded mode
        }
    }

    /// <summary>
    /// Clears the change tracker, re-fetches the WorkItem, and checks for race conditions.
    /// If the WorkItem is no longer in <paramref name="expectedStatus"/>, releases the PVC and deletes the orphaned K8s Job.
    /// Returns (true, reloadedWorkItem) if the caller should continue, or (false, null) if the caller
    /// should return early due to a detected race condition.
    /// </summary>
    private async Task<(bool shouldContinue, WorkItemEntity? reloadedWorkItem)> HandleOrphanedJobIfRaceDetectedAsync(
        PipelineDbContext db,
        Guid workItemId,
        string jobName,
        string? claimedPvc,
        List<string> availablePvcs,
        string logPrefix,
        WorkItemStatus expectedStatus,
        CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var workItem = await db.WorkItems.FindAsync([workItemId], ct);
        if (workItem is null || workItem.Status != expectedStatus)
        {
            // Race condition: another process transitioned the work item while we were creating the K8s Job.
            if (claimedPvc is not null)
                availablePvcs.Add(claimedPvc);

            try
            {
                await _kubeClient.DeleteJobAsync(jobName, _options.Namespace, CancellationToken.None);
                _log.Information("DispatchLifecycleService: deleted orphaned K8s Job {JobName} — {LogPrefix}WorkItem {WorkItemId} no longer in expected status {ExpectedStatus}", jobName, logPrefix, workItemId, expectedStatus);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "DispatchLifecycleService: failed to delete orphaned K8s Job {JobName} for {LogPrefix}WorkItem {WorkItemId}", jobName, logPrefix, workItemId);
            }

            return (false, null);
        }

        return (true, workItem);
    }

    /// <summary>
    /// Test-only override for retry delays in <see cref="GetJobUidAsync"/>.
    /// Mirrors <c>ResiliencePipelineFactory.TestRetryDelayOverride</c>.
    /// Set to <see cref="TimeSpan.Zero"/> in tests to prevent real wall-clock delays.
    /// Must be reset to <see langword="null"/> in test teardown.
    /// </summary>
    // TODO [WARNING]: TestRetryDelayOverride is a static mutable field on a production type.
    // xUnit runs test classes in parallel by default; a concurrent test class that also
    // exercises GetJobUidAsync could observe an unexpected TimeSpan.Zero or null depending
    // on set/reset ordering. Also, this field is reachable in production builds — consider
    // gating with #if DEBUG or injecting delay values via constructor/options to eliminate
    // the production-visible backdoor. See: SecurityReviewer / DotNetSpecialist warnings.
    internal static TimeSpan? TestRetryDelayOverride { get; set; }

    private static TimeSpan ResolveDelay(TimeSpan configured)
        => TestRetryDelayOverride ?? configured;

    private async Task CreateJobSecretAsync(
        string jobName, Guid workItemId, Dictionary<string, string> secrets, CancellationToken ct)
    {
        var secretName = $"caa-secrets-{workItemId.ToString("N")[..8]}";

        var jobUid = await GetJobUidAsync(jobName, ct);

        List<V1OwnerReference>? ownerReferences = null;
        if (!string.IsNullOrEmpty(jobUid))
        {
            ownerReferences =
            [
                new V1OwnerReference
                {
                    ApiVersion = "batch/v1",
                    Kind = "Job",
                    Name = jobName,
                    Uid = jobUid
                }
            ];
        }
        else
        {
            Log.Warning("DispatchLifecycleService: creating K8s Secret {SecretName} for Job {JobName} without OwnerReference — secret will not be auto-GC'd",
                secretName, jobName);
        }

        var secret = new V1Secret
        {
            Metadata = new V1ObjectMeta
            {
                Name = secretName,
                NamespaceProperty = _options.Namespace,
                OwnerReferences = ownerReferences
            },
            StringData = secrets
        };

        await _kubeClient.CreateSecretAsync(secret, _options.Namespace, ct);
    }

    /// <summary>
    /// Attempts to read the UID of the specified K8s Job, retrying up to 3 times on exceptions
    /// (200 ms then 400 ms backoff). Returns <see langword="null"/> if the UID cannot be obtained
    /// after all retries, after logging a warning.
    /// Only retries on exceptions — a null or empty UID from a successful response is returned as-is
    /// without retrying.
    /// </summary>
    private async Task<string?> GetJobUidAsync(string jobName, CancellationToken ct)
    {
        // retryDelays[i] = delay to apply BEFORE attempt i+1 (i.e. after attempt i fails).
        // 3 total attempts: attempt 0, delay 200ms, attempt 1, delay 400ms, attempt 2.
        TimeSpan[] retryDelays =
        [
            ResolveDelay(TimeSpan.FromMilliseconds(200)),
            ResolveDelay(TimeSpan.FromMilliseconds(400))
        ];
        Exception? lastException = null;

        for (int attempt = 0; attempt <= retryDelays.Length; attempt++)
        {
            try
            {
                var job = await _kubeClient.ReadJobAsync(jobName, _options.Namespace, ct);
                return job.Metadata?.Uid;
            }
            catch (Exception ex)
            {
                // TODO [WARNING]: OperationCanceledException (from ReadJobAsync or Task.Delay) is
                // caught here and treated as a retryable failure. If ct is cancelled mid-retry,
                // Task.Delay throws OperationCanceledException which is caught, stored as
                // lastException, and may trigger another Task.Delay on the now-cancelled token —
                // delaying cooperative shutdown by up to one extra delay period (max 400ms).
                // Fix: add `catch (OperationCanceledException) { throw; }` before this block,
                // or use `when (!ct.IsCancellationRequested)` as the catch predicate.
                // See: Correctness / DotNetSpecialist / SecurityReviewer [WARNING] findings.
                lastException = ex;
                if (attempt < retryDelays.Length)
                    await Task.Delay(retryDelays[attempt], ct);
            }
        }

        Log.Warning(lastException,
            "DispatchLifecycleService: could not obtain UID for K8s Job {JobName} after {Attempts} attempts — secret will be created without OwnerReference",
            jobName, retryDelays.Length + 1);
        return null;
    }

    /// <summary>
    /// Generates a deterministic K8s Job name from a work item ID.
    /// Delegates to <see cref="JobNameFactory.ForBrain"/> — the canonical definition of this format.
    /// Format: <c>caa-{first-8-chars-of-guid-no-dashes}</c>.
    /// Previously a static method on <c>DispatchService</c>; moved here (arch-audit 2026-08-22).
    /// </summary>
    /// <remarks>
    /// This wrapper must remain <c>internal static</c> — it is called directly by
    /// <c>DispatchServiceJobNamingPropertyTests</c> via <c>InternalsVisibleTo</c>.
    /// </remarks>
    internal static string GenerateJobName(Guid workItemId)
        => JobNameFactory.ForBrain(workItemId);

    public void Dispose()
    {
        _pvcSelectLock.Dispose();
    }
}
