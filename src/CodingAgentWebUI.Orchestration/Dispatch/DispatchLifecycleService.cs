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
using static CodingAgentWebUI.Orchestration.Dispatch.DispatchService;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Shared K8s Job dispatch lifecycle extracted from DispatchService.
/// Handles: PVC claim, WorkItem load, pre-write, K8s Job creation, secret creation,
/// race detection, status transition to Dispatched, and metric recording.
/// Used by both DispatchService (regular items) and ConsolidationDispatchHandler (consolidation items).
/// </summary>
internal sealed class DispatchLifecycleService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DispatchLifecycleService>();

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
    /// Shared dispatch lifecycle for WorkItems.
    /// Handles: PVC claim, WorkItem load, pre-write, K8s Job creation, secret creation,
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
        PipelineDbContext db,
        PendingWorkItemProjection item,
        JobTemplate template,
        bool isKiroAgent,
        List<string> availablePvcs,
        Dictionary<string, int> concurrencyBySelector,
        string logPrefix,
        Func<WorkItemEntity, Task<(bool shouldContinue, Dictionary<string, string>? projectSecrets)>> prepareVariant,
        Func<WorkItemEntity, Task>? onDispatchSuccess,
        CancellationToken ct,
        Func<Guid, string, Task>? onFailure = null)
    {
        // Generate deterministic job name
        var jobName = GenerateJobName(item.Id);

        // Claim PVC for kiro agents
        string? claimedPvc = null;
        if (isKiroAgent)
        {
            claimedPvc = availablePvcs[0];
            availablePvcs.RemoveAt(0);
        }

        // Load full WorkItem
        var workItem = await db.WorkItems.FindAsync([item.Id], ct);
        if (workItem is null || workItem.Status != WorkItemStatus.Pending)
        {
            // Item was modified by another process
            if (claimedPvc is not null) availablePvcs.Add(claimedPvc);
            return;
        }

        // Variant-specific preparation (may mutate workItem, load secrets, or signal abort)
        var (shouldProceed, projectSecrets) = await prepareVariant(workItem);
        if (!shouldProceed)
        {
            if (claimedPvc is not null) availablePvcs.Add(claimedPvc);
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
            if (claimedPvc is not null) availablePvcs.Add(claimedPvc);
            return;
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

        try
        {
            await db.SaveChangesAsync(ct);

            // Record dispatch latency / pending duration metric
            var latency = (workItem.DispatchedAt.Value - (workItem.OriginalEnqueuedAt ?? workItem.CreatedAt)).TotalSeconds;
            WorkDistributionTelemetry.DispatchLatency.Record(latency,
                new KeyValuePair<string, object?>("agent_selector", item.AgentSelector));
            WorkDistributionTelemetry.PendingDuration.Record(latency,
                new KeyValuePair<string, object?>("agent_selector", item.AgentSelector));

            // Track concurrency
            concurrencyBySelector[item.AgentSelector ?? ""] =
                concurrencyBySelector.GetValueOrDefault(item.AgentSelector ?? "", 0) + 1;

            Log.Information(
                "DispatchLifecycleService: {LogPrefix}WorkItem {WorkItemId} dispatched as Job {JobName} (selector={Selector}, pvc={Pvc})",
                logPrefix, item.Id, jobName, item.AgentSelector, claimedPvc ?? "none");

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
            ct);

        Log.Warning("DispatchLifecycleService: WorkItem {WorkItemId} failed: {Error}", workItemId, errorMessage);
    }

    /// <summary>
    /// Loads project secrets from the project's Settings JSON.
    /// </summary>
    public async Task<Dictionary<string, string>?> LoadProjectSecretsAsync(
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
