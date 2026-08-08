using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Telemetry;
using CodingAgentWebUI.Pipeline.Models;
using k8s.Autorest;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Shared K8s Job dispatch lifecycle extracted from DispatchService.
/// Handles: PVC selection, WorkItem load, pre-write, K8s Job creation, secret creation,
/// race detection, status transition to Dispatched, and metric recording.
/// Used by both DispatchService (regular items) and ConsolidationDispatchHandler (consolidation items).
/// </summary>
internal sealed class DispatchLifecycleService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DispatchLifecycleService>();

    private readonly SemaphoreSlim _pvcSelectLock = new(1, 1);

    private readonly IKubernetesJobClient _kubeClient;
    private readonly WorkItemTransitionService _transitionService;
    private readonly DispatchServiceOptions _options;

    public DispatchLifecycleService(
        IKubernetesJobClient kubeClient,
        WorkItemTransitionService transitionService,
        DispatchServiceOptions options)
    {
        _kubeClient = kubeClient;
        _transitionService = transitionService;
        _options = options;
    }

    /// <summary>
    /// Queries the database for claimed PVCs, excludes inflight claims, and returns available PVCs
    /// from the given pool. Used by both DispatchService and ConsolidationDispatchHandler.
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
                        (w.Status == WorkItemStatus.Pending ||
                         w.Status == WorkItemStatus.Dispatched ||
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
        var jobName = DispatchService.GenerateJobName(item.Id);

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
            if (workItem is null || workItem.Status != WorkItemStatus.Pending)
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

        // Pre-write K8sJobName (and ClaimedPvcName) to WorkItem BEFORE K8s API call.
        // EF change tracking also persists any entity mutations from prepareVariant (e.g., Payload).
        workItem.K8sJobName = jobName;
        if (claimedPvc is not null)
            workItem.ClaimedPvcName = claimedPvc;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            Log.Warning("DispatchLifecycleService: concurrency conflict pre-writing {LogPrefix}K8sJobName for {WorkItemId}", logPrefix, item.Id);
            ReleaseClaimedPvc(claimedPvc, availablePvcs);
            return;
        }
        catch
        {
            ReleaseClaimedPvc(claimedPvc, availablePvcs);
            throw;
        }

        // Create K8s Job via JobSpecBuilder
        if (!await CreateK8sJobAsync(db, item, workItem, template, jobName, claimedPvc, availablePvcs, projectSecrets, logPrefix, onFailure, ct))
            return;

        // Create per-job K8s Secret if project has secrets
        await CreateJobSecretIfNeededAsync(jobName, item.Id, projectSecrets, logPrefix, ct);

        // Update to Dispatched — clear change tracker first to get fresh state
        // (avoids stale entity if another service modified the item during K8s API call)
        var (shouldContinue, reloadedWorkItem) = await HandleOrphanedJobIfRaceDetectedAsync(db, item.Id, jobName, claimedPvc, availablePvcs, logPrefix, ct);
        if (!shouldContinue)
            return;

        workItem = reloadedWorkItem!;
        workItem.Status = WorkItemStatus.Dispatched;
        workItem.DispatchedAt = DateTimeOffset.UtcNow;

        await FinalizeDispatchAsync(db, workItem, item, logPrefix, concurrencyBySelector, onDispatchSuccess, ct);
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
                Log.Information("DispatchLifecycleService: {LogPrefix}no PVC available for WorkItem {WorkItemId}, skipping",
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
    private async Task FinalizeDispatchAsync(
        PipelineDbContext db,
        WorkItemEntity workItem,
        PendingWorkItemProjection item,
        string logPrefix,
        Dictionary<string, int> concurrencyBySelector,
        Func<WorkItemEntity, Task>? onDispatchSuccess,
        CancellationToken ct)
    {
        var jobName = workItem.K8sJobName!;
        try
        {
            await db.SaveChangesAsync(ct);

            // Record dispatch latency / pending duration metric
            WorkDistributionTelemetry.RecordDispatchLatency(workItem.DispatchedAt!.Value, workItem.OriginalEnqueuedAt, workItem.CreatedAt, item.AgentSelector);

            // Track concurrency
            // TODO: Use effectiveSelector (from eligibility checker's profile fallback resolution) instead of item.AgentSelector.
            concurrencyBySelector[item.AgentSelector ?? ""] =
                concurrencyBySelector.GetValueOrDefault(item.AgentSelector ?? "", 0) + 1;

            Log.Information(
                "DispatchLifecycleService: {LogPrefix}WorkItem {WorkItemId} dispatched as Job {JobName} (selector={Selector}, pvc={Pvc})",
                logPrefix, item.Id, jobName, item.AgentSelector, workItem.ClaimedPvcName ?? "none");

            // Variant-specific post-dispatch success action
            if (onDispatchSuccess is not null)
                await onDispatchSuccess(workItem);
        }
        catch (DbUpdateConcurrencyException)
        {
            Log.Warning("DispatchLifecycleService: concurrency conflict updating {LogPrefix}WorkItem {WorkItemId} to Dispatched", logPrefix, item.Id);
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
            item =>
            {
                item.ErrorMessage = errorMessage;
                item.FailureReason = FailureReason.InfrastructureFailure;
                item.CompletedAt = DateTimeOffset.UtcNow;
            },
            ct: ct);

        Log.Warning("DispatchLifecycleService: WorkItem {WorkItemId} failed: {Error}", workItemId, errorMessage);
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
    /// Creates a K8s Job via JobSpecBuilder. Handles 409 Conflict (idempotent) and general failures
    /// (releases PVC, fails WorkItem). Returns true if job creation succeeded (or 409), false if the
    /// caller should return early due to an error.
    /// </summary>
    private async Task<bool> CreateK8sJobAsync(
        PipelineDbContext db,
        PendingWorkItemProjection item,
        WorkItemEntity workItem,
        JobTemplate template,
        string jobName,
        string? claimedPvc,
        List<string> availablePvcs,
        Dictionary<string, string>? projectSecrets,
        string logPrefix,
        Func<Guid, string, Task>? onFailure,
        CancellationToken ct)
    {
        try
        {
            var buildCtx = new JobSpecBuilder.BuildContext
            {
                WorkItemId = item.Id,
                AgentSelector = item.AgentSelector,
                TimeoutSeconds = item.TimeoutSeconds,
                JobName = jobName,
                ClaimedPvc = claimedPvc,
                OrchestratorUrl = _options.OrchestratorUrl,
                AgentApiKeySecretName = _options.AgentApiKeySecretName,
                AgentServiceAccountName = _options.AgentServiceAccountName,
                Namespace = _options.Namespace,
                OpencodeConfigSecretName = _options.OpencodeConfigSecretName,
                ProjectSecrets = projectSecrets
            };
            var job = JobSpecBuilder.Build(template, buildCtx);
            await _kubeClient.CreateJobAsync(job, _options.Namespace, ct);
        }
        catch (HttpOperationException httpEx) when (httpEx.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // 409 Conflict = Job already exists = success (idempotent)
            Log.Information("DispatchLifecycleService: K8s Job {JobName} already exists (409 Conflict), treating as success", jobName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DispatchLifecycleService: failed to create K8s Job {JobName} for {LogPrefix}WorkItem {WorkItemId}", jobName, logPrefix, item.Id);
            if (claimedPvc is not null)
            {
                workItem.ClaimedPvcName = null;
                availablePvcs.Add(claimedPvc);
                await db.SaveChangesAsync(ct);
            }
            await FailWorkItemAsync(item.Id, $"K8s Job creation failed: {ex.Message}", ct);
            if (onFailure is not null)
                await onFailure(item.Id, $"K8s Job creation failed: {ex.Message}");
            return false;
        }

        return true;
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
            Log.Warning(ex, "DispatchLifecycleService: failed to create project-secrets K8s Secret for {LogPrefix}Job {JobName}", logPrefix, jobName);
            // Non-fatal: job can still run without project secrets in degraded mode
        }
    }

    /// <summary>
    /// Clears the change tracker, re-fetches the WorkItem, and checks for race conditions.
    /// If the WorkItem is no longer Pending, releases the PVC and deletes the orphaned K8s Job.
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
        CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var workItem = await db.WorkItems.FindAsync([workItemId], ct);
        if (workItem is null || workItem.Status != WorkItemStatus.Pending)
        {
            // Race condition: another process transitioned the work item while we were creating the K8s Job.
            if (claimedPvc is not null)
                availablePvcs.Add(claimedPvc);

            try
            {
                await _kubeClient.DeleteJobAsync(jobName, _options.Namespace, CancellationToken.None);
                Log.Information("DispatchLifecycleService: deleted orphaned K8s Job {JobName} — {LogPrefix}WorkItem {WorkItemId} no longer Pending", jobName, logPrefix, workItemId);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "DispatchLifecycleService: failed to delete orphaned K8s Job {JobName} for {LogPrefix}WorkItem {WorkItemId}", jobName, logPrefix, workItemId);
            }

            return (false, null);
        }

        return (true, workItem);
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

        await _kubeClient.CreateSecretAsync(secret, _options.Namespace, ct);
    }

    private async Task<string?> GetJobUidAsync(string jobName, CancellationToken ct)
    {
        try
        {
            var job = await _kubeClient.ReadJobAsync(jobName, _options.Namespace, ct);
            return job.Metadata?.Uid;
        }
        catch
        {
            return null;
        }
    }
}
