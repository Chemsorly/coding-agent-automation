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

        // Generate per-job HMAC key and create the K8s Secret BEFORE the Job.
        // The pod cannot start if the SecretKeyRef target does not exist yet —
        // creating the Secret first ensures it is present before the pod is scheduled.
        // The Secret always contains "agent-api-key" (the per-job derived key), plus any
        // project secrets when present.
        var agentKey = DeriveAgentKey(jobName);
        var secretDataForJob = BuildJobSecretData(agentKey, projectSecrets);
        var derivedKeySecretName = await CreateJobSecretBeforeJobAsync(jobName, item.Id, secretDataForJob, logPrefix, ct);
        if (derivedKeySecretName is null)
        {
            // Secret creation failed — the job cannot be started safely without AGENT_API_KEY.
            // Error has already been logged; fail the work item and abort.
            ReleaseClaimedPvc(claimedPvc, availablePvcs);
            try
            {
                await FailWorkItemAsync(item.Id, "Failed to create per-job agent-key Secret — dispatch aborted", ct);
            }
            catch (Exception failEx) when (failEx is not OperationCanceledException)
            {
                _log.Warning(failEx,
                    "DispatchLifecycleService: FailWorkItemAsync threw after Secret creation failure for WorkItem {WorkItemId} — item remains Pending for reconciliation",
                    item.Id);
            }
            return;
        }

        // Create K8s Job via JobSpecBuilder, referencing the per-job Secret for AGENT_API_KEY
        if (!await CreateK8sJobAsync(db, new K8sJobCreationContext(item, workItem, template, jobName, claimedPvc, availablePvcs, derivedKeySecretName, projectSecrets, logPrefix, onFailure), ct))
        {
            // TODO [WARNING]: The per-job Secret (derivedKeySecretName) was already created above.
            // If CreateK8sJobAsync fails, the Secret is left orphaned with no cleanup — it contains
            // a valid credential for a Job that was never started. On a retry the 409-Conflict path
            // will re-use the existing Secret (idempotent), so functionality is unaffected, but a
            // permanent failure leaves a stale credential in the namespace indefinitely.
            // Fix: attempt DeleteSecretAsync(derivedKeySecretName, ...) here on non-retriable errors.
            // See: DotNetSpecialist / SecurityReviewer [WARNING] findings.
            return;
        }

        // Patch the per-job Secret with an OwnerReference to the now-existing Job so that
        // Kubernetes GC deletes the Secret when the Job is deleted (TtlSecondsAfterFinished).
        // This is a best-effort non-fatal operation — dispatch succeeds even if the patch fails
        // (the Secret was already created and the pod is starting), but without the OwnerReference
        // the Secret will not be auto-GC'd and will accumulate until manual cleanup.
        await PatchSecretOwnerReferenceAfterJobAsync(jobName, item.Id, logPrefix, ct);

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
        string? DerivedKeySecretName,
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
        try
        {
            // Work-item pods: DerivedKeySecretName is set — AGENT_API_KEY is vended from the per-job
            // Secret (HMAC(master, jobName) generated server-side by DispatchLifecycleService).
            // Non-work-item pods (consolidation, model-fetch): DerivedKeySecretName is null — those
            // agents receive the raw master key via AGENT_API_KEY_FILE, and HubConnectionManagerFactory
            // derives HMAC(master, agentId) locally. Do NOT set DerivedKeySecretName for these pods.
            // TODO [WARNING]: Non-work-item pods are not yet migrated to per-job secrets. If they ever
            // are, ensure HubConnectionManagerFactory is updated to pass isWorkItemMode=true for those
            // pods too, and that AGENT_API_KEY_FILE is no longer set in their environment.
            // See: DotNetSpecialist / Correctness [WARNING] — non-work-item derivation invariant.
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
                // ProjectSecrets drives the project-secrets volume mount in JobSpecBuilder.
                // The actual secret data (including agent-api-key) is stored in DerivedKeySecretName;
                // JobSpecBuilder uses the same naming convention (caa-secrets-{workItemId[..8]}) for both.
                ProjectSecrets = ctx.ProjectSecrets,
                DerivedKeySecretName = ctx.DerivedKeySecretName,
                // Propagate the W3C traceparent captured at WorkItem creation so the agent pod's
                // spans attach to the upstream dispatch trace rather than starting a new root trace.
                // ctx.WorkItem is the full WorkItemEntity (loaded via FindAsync) — it carries the
                // TraceParent column. ctx.Item is PendingWorkItemProjection which does not have it.
                TraceParent = ctx.WorkItem.TraceParent
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
                // TODO: db.SaveChangesAsync(ct) uses the cancellation token. If ct is already cancelled
                // at this point, OperationCanceledException propagates out of this catch block uncaught
                // (the new inner guard below only wraps FailWorkItemAsync). This is pre-existing behaviour
                // on the PVC-cleanup path and is handled by the outer endpoint catch, but it creates an
                // asymmetry: cancellation from this save propagates, while cancellation from FailWorkItemAsync
                // (below) is intentionally allowed to propagate by the 'when (failEx is not OperationCanceledException)'
                // guard. Consider wrapping this SaveChangesAsync in its own cancellation-aware guard if
                // the asymmetry becomes a problem.
                await db.SaveChangesAsync(ct);
            }
            try
            {
                await FailWorkItemAsync(ctx.Item.Id, $"K8s Job creation failed: {ex.Message}", ct);
            }
            catch (Exception failEx) when (failEx is not OperationCanceledException)
            {
                // FailWorkItemAsync itself threw (e.g. NpgsqlException, InvalidOperationException from
                // a faulted DB factory). The item remains in Pending state and will be recovered by the
                // reconciliation loop. Log a Warning with the WorkItem ID so the failure is visible.
                _log.Warning(failEx,
                    "DispatchLifecycleService: FailWorkItemAsync threw for WorkItem {WorkItemId} after K8s Job creation failure — item remains Pending for reconciliation",
                    ctx.Item.Id);
            }
            // TODO: OnFailure is invoked unconditionally here, including when FailWorkItemAsync threw
            // (item is left in Pending state for reconciliation). If OnFailure itself throws a
            // non-cancellation exception in that branch, the exception propagates out of
            // CreateK8sJobAsync unguarded — the same class of bug this fix was intended to eliminate.
            // Currently both callers in WorkItemDispatchEndpoints.cs pass onFailure: null, so this is
            // not reachable in production today. If a non-null OnFailure caller is added in the future,
            // consider wrapping this call in a try/catch or suppressing it when FailWorkItemAsync threw.
            if (ctx.OnFailure is not null)
                await ctx.OnFailure(ctx.Item.Id, $"K8s Job creation failed: {ex.Message}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Derives the per-job agent API key by computing <c>HMAC-SHA256(masterKey, jobName)</c>.
    /// The master key is read from <c>AGENT_API_KEY</c> — the same env var used by
    /// <c>AgentApiKeyAuthHandler</c> — so the derived value is guaranteed to pass server-side
    /// validation without the pod ever receiving the master key.
    /// Returns the lowercase hex string (64 chars), matching <c>AgentApiKeyAuthHandler</c>'s
    /// derivation format.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>AGENT_API_KEY</c> is not set or empty in the process environment.
    /// Failing fast here prevents dispatching work-item pods with a weakly-derived key
    /// that any observer knowing the job-naming scheme could reproduce.
    /// </exception>
    // TODO [WARNING]: DeriveAgentKey reads AGENT_API_KEY from Environment.GetEnvironmentVariable
    // directly, making it process-global and non-thread-safe under parallel test execution (tests
    // must use Environment.SetEnvironmentVariable with try/finally). Consider accepting the master
    // key as a parameter and resolving the env var at the call site (ExecuteDispatchLifecycleAsync),
    // e.g. from IOptions<T> or a startup-validated config field. See: DotNetSpecialist [WARNING].
    internal static string DeriveAgentKey(string jobName)
    {
        var masterKey = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentApiKey);
        if (string.IsNullOrEmpty(masterKey))
            throw new InvalidOperationException(
                $"Cannot derive per-job agent key: environment variable '{AgentDefaults.EnvAgentApiKey}' is not set or empty. " +
                "Ensure the Helm Secret is correctly mounted on the API/dispatcher container before dispatching work-item pods.");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(masterKey));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(jobName));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Builds the complete secret data dictionary for the per-job K8s Secret.
    /// Always includes <c>"agent-api-key"</c> (the pre-vended HMAC value for this job).
    /// Merges in any project secrets when present.
    /// </summary>
    private static Dictionary<string, string> BuildJobSecretData(string agentKey, Dictionary<string, string>? projectSecrets)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["agent-api-key"] = agentKey
        };
        if (projectSecrets is not null)
        {
            foreach (var kv in projectSecrets)
                data[kv.Key] = kv.Value;
        }
        // TODO [WARNING]: If a project operator configures a project secret with the key "agent-api-key",
        // it silently overwrites the pre-vended HMAC credential, causing the pod to receive an
        // operator-controlled value. The pod will fail to authenticate (denial-of-service against the pod,
        // not a credential escalation). Fix: add the agent-api-key entry AFTER the loop so it always wins,
        // or explicitly skip any project secret whose key equals "agent-api-key".
        // See: SecurityReviewer [WARNING] — BuildJobSecretData collision.
        return data;
    }

    /// <summary>
    /// After the K8s Job is successfully created, patches the per-job Secret's
    /// <c>ownerReferences</c> to point to the Job. This enables Kubernetes garbage collection
    /// to automatically delete the Secret when the Job is deleted (e.g., via
    /// <c>ttlSecondsAfterFinished</c>), preventing credential accumulation.
    /// Reads the Job UID via <see cref="GetJobUidAsync"/> (with retry). Non-fatal: if the UID
    /// cannot be obtained or the patch fails, logs a warning and continues — dispatch has already
    /// succeeded and the pod is starting; the only consequence is the Secret will not be auto-GC'd.
    /// </summary>
    private async Task PatchSecretOwnerReferenceAfterJobAsync(
        string jobName, Guid workItemId, string logPrefix, CancellationToken ct)
    {
        var secretName = $"caa-secrets-{workItemId.ToString("N")[..8]}";
        try
        {
            var jobUid = await GetJobUidAsync(jobName, ct);
            if (string.IsNullOrEmpty(jobUid))
            {
                _log.Warning(
                    "DispatchLifecycleService: could not obtain UID for K8s Job {JobName} — per-job Secret {SecretName} will not have an OwnerReference and will not be auto-GC'd on Job deletion",
                    jobName, secretName);
                return;
            }

            var ownerRef = new V1OwnerReference
            {
                ApiVersion = "batch/v1",
                Kind = "Job",
                Name = jobName,
                Uid = jobUid,
                BlockOwnerDeletion = true
            };
            await _kubeClient.PatchSecretOwnerReferenceAsync(secretName, _options.Namespace, ownerRef, ct);
            _log.Debug(
                "DispatchLifecycleService: patched OwnerReference on per-job Secret {SecretName} → Job {JobName} (uid={JobUid}) for {LogPrefix}WorkItem {WorkItemId}",
                secretName, jobName, jobUid, logPrefix, workItemId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Non-fatal: the pod is already starting; the Secret simply won't be auto-GC'd.
            _log.Warning(ex,
                "DispatchLifecycleService: failed to patch OwnerReference on per-job Secret {SecretName} for {LogPrefix}Job {JobName} (WorkItem {WorkItemId}) — Secret will not be auto-GC'd on Job deletion",
                secretName, logPrefix, jobName, workItemId);
        }
    }

    /// <summary>
    /// Creates the per-job K8s Secret containing the pre-vended agent API key (and any project
    /// secrets) BEFORE the K8s Job is submitted. The Secret must pre-exist so that Kubernetes
    /// can resolve the <c>SecretKeyRef</c> when the pod starts.
    /// No <c>OwnerReference</c> is set at creation time — the Job does not exist yet.
    /// After the Job is created, <see cref="PatchSecretOwnerReferenceAfterJobAsync"/> patches the
    /// OwnerReference so that Kubernetes GC deletes the Secret when the Job is deleted.
    /// Handles 409 Conflict idempotently. On other failures logs an error and returns null
    /// (caller should abort dispatch).
    /// Returns the Secret name on success, or null if creation failed.
    /// </summary>
    private async Task<string?> CreateJobSecretBeforeJobAsync(
        string jobName,
        Guid workItemId,
        Dictionary<string, string> secretData,
        string logPrefix,
        CancellationToken ct)
    {
        var secretName = $"caa-secrets-{workItemId.ToString("N")[..8]}";
        try
        {
            var secret = new V1Secret
            {
                Metadata = new V1ObjectMeta
                {
                    Name = secretName,
                    NamespaceProperty = _options.Namespace
                    // OwnerReference is not set here — Job does not exist yet.
                    // PatchSecretOwnerReferenceAfterJobAsync sets it after the Job is created.
                },
                StringData = secretData
            };
            await _kubeClient.CreateSecretAsync(secret, _options.Namespace, ct);
            return secretName;
        }
        catch (HttpOperationException httpEx) when (httpEx.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // 409 Conflict = Secret already exists (idempotent retry)
            // TODO [WARNING]: On the 409 path, we assume the pre-existing Secret contains a valid
            // agent-api-key matching HMAC(currentMasterKey, jobName). This is correct when the key
            // is deterministic and the master key has not changed since the original Secret was created.
            // If the master key was rotated between the original create and this retry, the pod will
            // receive a stale token that AgentApiKeyAuthHandler rejects, causing an infinite reconnect
            // loop. Fix: delete-and-recreate or PATCH the Secret on 409 to overwrite with the freshly-
            // derived value. See: DotNetSpecialist / SecurityReviewer [WARNING] findings.
            _log.Information("DispatchLifecycleService: per-job Secret {SecretName} for {LogPrefix}Job {JobName} already exists (409), treating as success", secretName, logPrefix, jobName);
            return secretName;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "DispatchLifecycleService: failed to create per-job agent-key Secret {SecretName} for {LogPrefix}Job {JobName} (WorkItem {WorkItemId}) — aborting dispatch", secretName, logPrefix, jobName, workItemId);
            return null;
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

    // TODO [WARNING]: CreateJobSecretAsync and GetJobUidAsync below are now dead code — the only
    // caller (CreateJobSecretIfNeededAsync) was removed in issue #3034 and replaced by the
    // CreateJobSecretBeforeJobAsync + PatchSecretOwnerReferenceAfterJobAsync pair.
    // GetJobUidAsync is still called by PatchSecretOwnerReferenceAfterJobAsync (the retry logic
    // is reused). CreateJobSecretAsync is unreachable from any production path.
    // Consider removing CreateJobSecretAsync once the new approach is confirmed stable in prod.
    // See: DotNetSpecialist / Correctness [WARNING] findings.
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
                return job?.Metadata?.Uid;
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
