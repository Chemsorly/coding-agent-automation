using System.Diagnostics;
using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.Pipeline.Models;
using k8s.Autorest;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Serilog;

namespace CodingAgentWebUI.Api.Dispatch;

/// <summary>
/// Shared K8s Job dispatch lifecycle extracted from DispatchService.
/// Handles: PVC selection, WorkItem load, pre-write, K8s Job creation, secret creation,
/// race detection, status transition to Dispatched, and metric recording.
/// Used by both DispatchService (regular items) and ConsolidationWorkItemDispatchService (consolidation items).
/// </summary>
internal sealed class DispatchLifecycleService : IDisposable
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<DispatchLifecycleService>();

    private readonly SemaphoreSlim _pvcSelectLock = new(1, 1);

    private readonly IKubernetesJobClient? _kubeClient;
    private readonly WorkItemTransitionService _transitionService;
    private readonly DispatchServiceOptions _options;
    private readonly JobTemplateStore? _templateProvider;

    public DispatchLifecycleService(
        IKubernetesJobClient? kubeClient,
        WorkItemTransitionService transitionService,
        DispatchServiceOptions options,
        JobTemplateStore? templateProvider = null)
    {
        _kubeClient = kubeClient;
        _transitionService = transitionService;
        _options = options;
        _templateProvider = templateProvider;
    }

    /// <summary>
    /// Queries the database for claimed PVCs, excludes inflight claims, and returns available PVCs
    /// from the given pool. Used by both DispatchService and ConsolidationWorkItemDispatchService.
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
        catch (DbUpdateConcurrencyException ex)
        {
            Log.Warning(ex, "DispatchLifecycleService: concurrency conflict pre-writing {LogPrefix}K8sJobName for {WorkItemId}", logPrefix, item.Id);
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
    private static async Task FinalizeDispatchAsync(
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
        catch (DbUpdateConcurrencyException ex)
        {
            Log.Warning(ex, "DispatchLifecycleService: concurrency conflict updating {LogPrefix}WorkItem {WorkItemId} to Dispatched", logPrefix, item.Id);
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
        try
        {
            // Work-item pods receive the master key via file mount (DerivedKeySecretName=null).
            // Key derivation for hub auth and HTTP calls happens inside the agent at runtime
            // using AGENT_ID (= job name) as the agentId: HMAC(masterKey, jobName).
            // Do NOT set DerivedKeySecretName here — it would make the pod receive the
            // already-derived key, causing HubConnectionManager to double-derive and fail auth.
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
                ProjectSecrets = ctx.ProjectSecrets
            };
            var job = JobSpecBuilder.Build(ctx.Template, buildCtx);
            await _kubeClient!.CreateJobAsync(job, _options.Namespace, ct);
        }
        catch (HttpOperationException httpEx) when (httpEx.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // 409 Conflict = Job already exists = success (idempotent)
            Log.Information(httpEx, "DispatchLifecycleService: K8s Job {JobName} already exists (409 Conflict), treating as success", ctx.JobName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DispatchLifecycleService: failed to create K8s Job {JobName} for {LogPrefix}WorkItem {WorkItemId}", ctx.JobName, ctx.LogPrefix, ctx.Item.Id);
            if (ctx.ClaimedPvc is not null)
            {
                ctx.WorkItem.ClaimedPvcName = null;
                ctx.AvailablePvcs.Add(ctx.ClaimedPvc);
                await db.SaveChangesAsync(ct);
            }
            await FailWorkItemAsync(ctx.Item.Id, $"K8s Job creation failed: {ex.Message}", ct);
            if (ctx.OnFailure is not null)
                await ctx.OnFailure(ctx.Item.Id, $"K8s Job creation failed: {ex.Message}");
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
                await _kubeClient!.DeleteJobAsync(jobName, _options.Namespace, CancellationToken.None);
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

        await _kubeClient!.CreateSecretAsync(secret, _options.Namespace, ct);
    }

    private async Task<string?> GetJobUidAsync(string jobName, CancellationToken ct)
    {
        try
        {
            var job = await _kubeClient!.ReadJobAsync(jobName, _options.Namespace, ct);
            return job.Metadata?.Uid;
        }
        catch
        {
            return null;
        }
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

    /// <summary>
    /// Synchronous dispatch path: atomically creates a <see cref="WorkItemEntity"/> with
    /// <c>Status=Dispatched</c> and a matching K8s Job in a single request.
    /// Used by <c>POST /api/work-items/dispatch</c> — replaces the two-step
    /// <c>POST /api/work-items</c> (→ Pending) + <c>DispatchLoop</c> path.
    ///
    /// For kiro agents, <see cref="_pvcSelectLock"/> is held from <see cref="QueryAvailablePvcsAsync"/>
    /// through <c>SaveChangesAsync</c> so that the DB row (with <c>ClaimedPvcName</c> set) is
    /// persisted before any concurrent caller can read the available-PVC set. This closes the
    /// TOCTOU race: two concurrent callers will each read the PVC pool in sequence under the lock,
    /// so the second caller will see the first caller's claimed PVC as unavailable and return 503.
    ///
    /// Returns the new <see cref="WorkItemEntity.Id"/> on success.
    /// Returns <see langword="null"/> with <paramref name="statusCode"/> set to:
    /// <list type="bullet">
    ///   <item>503 — no PVC available for kiro agent, or K8s Job creation failed</item>
    ///   <item>409 — at concurrency limit</item>
    /// </list>
    /// </summary>
    public async Task<(Guid? WorkItemId, int? statusCode)> ExecuteSynchronousDispatchAsync(
        JobDistributionRequest request,
        Guid workItemId,
        string payloadJson,
        IDbContextFactory<PipelineDbContext> dbFactory,
        IOrchestratorRunService runService,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(payloadJson);
        // TODO [WARNING]: Missing null/empty guards for required parameters dbFactory and runService.
        // If either is null (misconfigured DI), a NullReferenceException is thrown at first use
        // rather than a clear ArgumentNullException at method entry. Add:
        //   ArgumentNullException.ThrowIfNull(dbFactory);
        //   ArgumentNullException.ThrowIfNull(runService);
        // Also missing input validation on request.IssueIdentifier.Value — a POST body with an
        // empty/null IssueIdentifier persists an empty-string identity, breaking dedup queries.
        // Validate IssueIdentifier, IssueProviderConfigId, and AgentSelector and return 400.
        // See review finding [WARNING] line 529/591.

        var db = await dbFactory.CreateDbContextAsync(ct);
        try
        {
            // ── Concurrency check ──────────────────────────────────────────
            // TODO [WARNING]: This CountAsync runs outside _pvcSelectLock and races with concurrent
            // dispatch calls: two callers for the same selector at MaxConcurrent-1 can both read
            // activeCounts == MaxConcurrent-1, both pass the guard, and both create WorkItems —
            // exceeding the limit by one. For non-kiro selectors (no PVC backstop) this is the only
            // limiter. Fix: enforce the limit inside a transaction / advisory lock, or via a
            // conditional INSERT that counts atomically. See review finding [WARNING] line 544.
            var selector = request.AgentSelector ?? "";
            var activeCounts = await db.WorkItems
                .Where(w => (w.Status == WorkItemStatus.Dispatched || w.Status == WorkItemStatus.Running)
                         && w.AgentSelector == selector)
                .CountAsync(ct);

            var template = _templateProvider?.Resolve(selector);
            if (template is not null && template.MaxConcurrent > 0 && activeCounts >= template.MaxConcurrent)
            {
                Log.Information(
                    "DispatchLifecycleService: concurrency limit reached for selector '{Selector}' " +
                    "(limit={Max}, active={Active}), returning 409",
                    selector, template.MaxConcurrent, activeCounts);
                await db.DisposeAsync();
                return (null, 409);
            }

            // ── PVC selection for kiro agents ──────────────────────────────
            // TOCTOU fix: the lock must span QueryAvailablePvcsAsync + SaveChangesAsync so that the
            // DB row reflecting ClaimedPvcName is written before any concurrent caller reads
            // QueryAvailablePvcsAsync. Without this, two concurrent callers both read the same
            // free-PVC set before either has written a WorkItem row, both select the same PVC,
            // and both proceed to create K8s Jobs mounting the same RWO PVC.
            // TODO [WARNING]: The issue requirements state "Eligibility re-check (issue still open,
            // no blocking labels) must be performed before K8s Job creation — this is a correctness
            // guard, not optional." This path currently omits that check. A dispatch request arriving
            // moments after the issue is closed or labeled agent:cancelled will create a Dispatched
            // WorkItem and a K8s Job for a now-ineligible issue. The old DispatchLoop called
            // GetEligibilityCachedAsync before claiming. Add an eligibility check here before PVC
            // selection. See review finding [WARNING] line 601.
            var isKiroAgent = template is not null &&
                string.Equals(template.ProviderType, "kiro", StringComparison.OrdinalIgnoreCase);

            if (isKiroAgent)
                await _pvcSelectLock.WaitAsync(ct);
            bool pvcLockHeld = isKiroAgent;

            string? claimedPvc = null;
            try
            {
                if (isKiroAgent)
                {
                    // Query and select inside the lock so no concurrent caller observes the same
                    // free PVC between our read and our write.
                    var pvcResult = await QueryAvailablePvcsAsync(db, _options.KiroPvcPool, ct);
                    claimedPvc = pvcResult.AvailablePvcs.FirstOrDefault();
                    if (claimedPvc is null)
                    {
                        Log.Information(
                            "DispatchLifecycleService: [sync] no PVC available for WorkItem {WorkItemId}, returning 503",
                            workItemId);
                        _pvcSelectLock.Release();
                        pvcLockHeld = false;
                        await db.DisposeAsync();
                        WorkDistributionTelemetry.PvcPoolExhaustions.Add(1,
                            new KeyValuePair<string, object?>("pool", "kiro"));
                        return (null, 503);
                    }
                }

                var jobName = GenerateJobName(workItemId);

                // ── Create WorkItem as Dispatched (while lock is held for kiro agents) ────
                var entity = new WorkItemEntity
                {
                    Id = workItemId,
                    TaskType = request.TaskType,
                    IssueIdentifier = request.IssueIdentifier.Value,
                    IssueProviderConfigId = request.IssueProviderConfigId,
                    Status = WorkItemStatus.Dispatched,
                    Payload = payloadJson,
                    AgentSelector = selector,
                    TimeoutSeconds = request.TimeoutSeconds,
                    ProjectId = request.ProjectId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    PriorityWeight = InitiatedByConstants.IsManual(request.InitiatedBy) ? 100 : 0,
                    TraceParent = request.TraceContext?.GetValueOrDefault("traceparent")
                        ?? System.Diagnostics.Activity.Current?.Id,
                    K8sJobName = jobName,
                    ClaimedPvcName = claimedPvc,
                    DispatchedAt = DateTimeOffset.UtcNow
                };

                db.WorkItems.Add(entity);
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (Exception ex) when (IsUniqueViolation(ex))
                {
                    // Idempotent: a row with this workItemId already exists (retry).
                    // For the dispatch path, treat as a successful prior dispatch.
                    // TODO [WARNING]: This shortcut assumes the prior call fully completed (WorkItem +
                    // K8s Job both created). If the prior call died between SaveChangesAsync and
                    // CreateJobAsync, the WorkItem row exists but no K8s Job was ever created. Returning
                    // (workItemId, null) / HTTP 200 here means the caller treats dispatch as successful
                    // while no pod will ever run — the item remains Dispatched until the timeout fires.
                    // Also skips PipelineRunFactory.CreateFromWorkItem, so the UI gets no live events.
                    // Fix: verify/create the K8s Job idempotently on the retry path. See WARNING line 610.
                    var exists = await db.WorkItems.AnyAsync(w => w.Id == workItemId, ct);
                    if (exists)
                    {
                        if (pvcLockHeld) { _pvcSelectLock.Release(); pvcLockHeld = false; }
                        await db.DisposeAsync();
                        return (workItemId, null);
                    }
                    // Partial unique index conflict: issue already has a live WorkItem.
                    if (pvcLockHeld) { _pvcSelectLock.Release(); pvcLockHeld = false; }
                    await db.DisposeAsync();
                    return (null, 409);
                }

                // WorkItem row is now persisted with ClaimedPvcName — safe to release lock.
                // Subsequent callers to QueryAvailablePvcsAsync will see this row and exclude
                // the PVC we just claimed.
                if (pvcLockHeld) { _pvcSelectLock.Release(); pvcLockHeld = false; }

                // ── Create K8s Job ─────────────────────────────────────────
                // TODO [WARNING]: When template is null (no JobTemplate for the selector) or _kubeClient
                // is null (K8s unavailable in test/dev), this block is skipped and the method returns
                // (workItemId, null) — HTTP 200 — with a Dispatched WorkItem but no running K8s Job.
                // This is a phantom dispatch: the item sits Dispatched until EnforceDispatchedTimeoutAsync
                // marks it Failed. A missing template should return 503 or 409 (misconfiguration) rather
                // than a false-positive success. See review finding [WARNING] line 619.
                if (template is not null && _kubeClient is not null)
                {
                    var buildCtx = new JobSpecBuilder.BuildContext
                    {
                        WorkItemId = workItemId,
                        AgentSelector = selector,
                        TimeoutSeconds = request.TimeoutSeconds > 0
                            ? request.TimeoutSeconds
                            : (int)PipelineConstants.DefaultAgentTimeout.TotalSeconds,
                        JobName = jobName,
                        ClaimedPvc = claimedPvc,
                        OrchestratorUrl = _options.OrchestratorUrl,
                        AgentApiKeySecretName = _options.AgentApiKeySecretName,
                        AgentServiceAccountName = _options.AgentServiceAccountName,
                        Namespace = _options.Namespace,
                        OpencodeConfigSecretName = _options.OpencodeConfigSecretName
                    };
                    var job = JobSpecBuilder.Build(template, buildCtx);
                    try
                    {
                        await _kubeClient.CreateJobAsync(job, _options.Namespace, ct);
                    }
                    catch (k8s.Autorest.HttpOperationException httpEx)
                        when (httpEx.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
                    {
                        // 409 Conflict = Job already exists = success (idempotent)
                        Log.Information(
                            "DispatchLifecycleService: [sync] K8s Job {JobName} already exists (409 Conflict), treating as success",
                            jobName);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex,
                            "DispatchLifecycleService: [sync] failed to create K8s Job {JobName} for WorkItem {WorkItemId}",
                            jobName, workItemId);
                        // Fail the WorkItem so ReconciliationLoop can clean it up
                        await FailWorkItemAsync(workItemId, $"K8s Job creation failed: {ex.Message}", CancellationToken.None);
                        await db.DisposeAsync();
                        return (null, 503);
                    }
                }

                // Materialise in-memory PipelineRun so UI receives live events
                var run = PipelineRunFactory.CreateFromWorkItem(workItemId, request);
                if (run is not null)
                    runService.AddRun(run);

                WorkDistributionTelemetry.RecordDispatchLatency(
                    entity.DispatchedAt!.Value, null, entity.CreatedAt, selector);

                Log.Information(
                    "DispatchLifecycleService: [sync] WorkItem {WorkItemId} dispatched as Job {JobName} (selector={Selector})",
                    workItemId, jobName, selector);

                await db.DisposeAsync();
                return (workItemId, null);
            }
            catch
            {
                // Release the PVC lock if still held (e.g., exception thrown during SaveChangesAsync
                // before we could release it explicitly).
                if (pvcLockHeld) { _pvcSelectLock.Release(); pvcLockHeld = false; }
                if (claimedPvc is not null)
                {
                    Log.Warning(
                        "DispatchLifecycleService: [sync] unexpected error — PVC {Pvc} was claimed but WorkItem {WorkItemId} was not persisted",
                        claimedPvc, workItemId);
                }
                // TODO [WARNING]: db.DisposeAsync() is called here AND in the outer catch below,
                // resulting in a double-dispose on the exception path. EF Core DbContext.DisposeAsync
                // is currently idempotent but this is fragile and relies on implementation detail.
                // Prefer a single disposal scope (e.g., await using var db = ...) to eliminate the
                // double-dispose risk. See review finding [WARNING] line 722.
                await db.DisposeAsync();
                throw;
            }
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Postgres/EF InMemory unique-constraint violation detection (mirrored from WorkItemEndpoints).
    /// </summary>
    private static bool IsUniqueViolation(Exception ex)
    {
        if (ex is DbUpdateException { InnerException: Npgsql.PostgresException pg })
            return pg.SqlState == "23505";
        var message = ex.Message ?? "";
        var innerMessage = ex.InnerException?.Message ?? "";
        return message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unique constraint", StringComparison.OrdinalIgnoreCase)
            || innerMessage.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
            || innerMessage.Contains("unique constraint", StringComparison.OrdinalIgnoreCase)
            || message.Contains("An item with the same key has already been added", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _pvcSelectLock.Dispose();
    }
}
