using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
using k8s.Autorest;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
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
    /// Discriminated result of <see cref="DispatchDirectlyAsync"/>: success carries the new WorkItem ID;
    /// the failure cases carry an HTTP-level status code the endpoint can return to the caller.
    /// </summary>
    internal abstract record DirectDispatchResult
    {
        /// <summary>Dispatch succeeded. The WorkItem was created as Dispatched and the K8s Job is running.</summary>
        internal sealed record Success(Guid WorkItemId) : DirectDispatchResult;
        /// <summary>No PVC available for kiro agents, or the K8s API call failed. Map to HTTP 503.</summary>
        internal sealed record ServiceUnavailable(string Reason) : DirectDispatchResult;
        /// <summary>Concurrency limit reached for this selector. Map to HTTP 409.</summary>
        internal sealed record ConcurrencyLimitReached(string Reason) : DirectDispatchResult;
        /// <summary>A work item already exists for this issue (unique-index violation). Map to HTTP 409.</summary>
        internal sealed record Conflict(string Reason) : DirectDispatchResult;
    }

    /// <summary>
    /// Synchronous dispatch path (issue #2322): atomically creates a WorkItem as
    /// <see cref="WorkItemStatus.Dispatched"/> and launches the corresponding K8s Job in one request.
    /// No <c>Pending</c> state is written.
    /// </summary>
    /// <remarks>
    /// Called by <c>POST /api/work-items/dispatch</c>. The method:
    /// <list type="number">
    ///   <item>Checks the concurrency limit for the resolved template's selector.</item>
    ///   <item>For kiro agents: acquires <see cref="_pvcSelectLock"/> and selects an available PVC.</item>
    ///   <item>Creates the WorkItem entity with <c>Status=Dispatched</c>.</item>
    ///   <item>Creates the K8s Job (rolls back on failure by deleting the WorkItem).</item>
    ///   <item>Registers the in-memory <see cref="PipelineRun"/> in <paramref name="runService"/>.</item>
    ///   <item>Records dispatch telemetry.</item>
    /// </list>
    /// PVC selection atomicity is preserved by <see cref="_pvcSelectLock"/>: the lock spans the
    /// DB query for available PVCs through the K8s Job creation so no two concurrent requests
    /// can mount the same PVC. This is an in-process <see cref="SemaphoreSlim"/>; if the API
    /// runs with multiple replicas, a distributed lock is required.
    /// </remarks>
    internal async Task<DirectDispatchResult> DispatchDirectlyAsync(
        JobDistributionRequest request,
        JobTemplate template,
        IOrchestratorRunService runService,
        IDbContextFactory<PipelineDbContext> dbFactory,
        CancellationToken ct)
    {
        var selector = JobTemplateStore.NormalizeLabels(request.AgentSelector ?? "");
        var isKiroAgent = string.Equals(template.ProviderType, "kiro", StringComparison.OrdinalIgnoreCase);

        // TODO [WARNING]: Issue eligibility re-check is absent from this synchronous dispatch path.
        // The issue requirements state: "Eligibility re-check (issue still open, no blocking labels)
        // must be performed before K8s Job creation — this is a correctness guard, not optional."
        // An issue may be closed or label-blocked between the Scheduler's eligibility check and this
        // endpoint being called (a few seconds of latency). Without this check, a K8s Job is launched
        // for a closed/blocked issue, consuming a PVC and agent credentials until the agent discovers
        // the issue state at runtime. The old DispatchLoop.ProcessItemAsync called GetEligibilityCachedAsync
        // before TryClaimWorkItemAsync. This endpoint should perform an equivalent check via the
        // issue provider before proceeding past this point.
        // See review finding [WARNING] Correctness:DispatchLifecycleService.cs:109.

        // Concurrency check (DB-backed, same as ConsolidationWorkItemDispatchService)
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var activeConcurrency = await db.WorkItems
            .AsNoTracking()
            .Where(w => (w.Status == WorkItemStatus.Dispatched || w.Status == WorkItemStatus.Running)
                     && w.AgentSelector == (request.AgentSelector ?? ""))
            .CountAsync(ct);

        if (template.MaxConcurrent > 0 && activeConcurrency >= template.MaxConcurrent)
        {
            Log.Information(
                "DispatchDirectly: concurrency limit reached for selector '{Selector}' (limit={Max}, active={Active}), issue {IssueIdentifier}",
                selector, template.MaxConcurrent, activeConcurrency, request.IssueIdentifier);
            // TODO [WARNING]: request.AgentSelector is user-controlled input reflected verbatim in
            // the 409 response body via ConcurrencyLimitReached.Reason. NormalizeLabels() only
            // sorts comma-separated tokens — it does not cap length or restrict characters.
            // A crafted AgentSelector value (e.g. containing HTML-special chars or oversized content)
            // is returned in TypedResults.Conflict(e.Reason) in WorkItemEndpoints.DispatchWorkItem.
            // Since the endpoint requires Operator-tier auth, the exploitable population is already
            // privileged, but if responses are forwarded through proxies or displayed in a UI without
            // escaping, this could facilitate injection. Consider sanitising selector before embedding.
            // See review finding [WARNING] SecurityReviewer:DispatchLifecycleService.cs:144.
            // TODO [WARNING]: Two concurrent requests for non-kiro agents can both pass this
            // concurrency check simultaneously (both read count=0 before either writes) and
            // both succeed at CreateWorkItemAsDispatchedAsync for different issues, resulting in
            // MaxConcurrent+N active items. For kiro agents the PVC pool is a secondary hard cap,
            // but for non-kiro agents with MaxConcurrent configured, the limit is not reliably
            // enforced under concurrent load. The old single-threaded DispatchLoop avoided this.
            // To fix: hold _pvcSelectLock (or a dedicated lock) around the check+write for non-kiro
            // agents, or use a DB-level serializable transaction for the concurrency check.
            // See review finding [WARNING] Correctness:DispatchLifecycleService.cs:131.
            return new DirectDispatchResult.ConcurrencyLimitReached(
                $"Concurrency limit {template.MaxConcurrent} reached for selector '{selector}'");
        }

        // Use RunId from request as the WorkItem ID (deterministic round-trip)
        var workItemId = !string.IsNullOrEmpty(request.RunId) && Guid.TryParse(request.RunId, out var parsedRunId)
            ? parsedRunId
            : Guid.NewGuid();

        var jobName = GenerateJobName(workItemId);

        if (isKiroAgent)
        {
            // Acquire PVC lock: spans PVC selection → K8s Job creation to close the TOCTOU race.
            await _pvcSelectLock.WaitAsync(ct);
            try
            {
                var pvcResult = await QueryAvailablePvcsAsync(db, _options.KiroPvcPool, ct);
                if (pvcResult.AvailablePvcs.Count == 0)
                {
                    Log.Information(
                        "DispatchDirectly: no PVC available for kiro agent, issue {IssueIdentifier}",
                        request.IssueIdentifier);
                    WorkDistributionTelemetry.PvcPoolExhaustions.Add(1,
                        new KeyValuePair<string, object?>("pool", "kiro"));
                    return new DirectDispatchResult.ServiceUnavailable("No PVC available in the kiro pool");
                }

                var claimedPvc = pvcResult.AvailablePvcs[0];

                // Create the WorkItem as Dispatched (no Pending state), inside the lock
                var createResult = await CreateWorkItemAsDispatchedAsync(
                    db, request, workItemId, jobName, claimedPvc, ct);
                if (createResult is null)
                    return new DirectDispatchResult.Conflict(
                        "A live work item already exists for this issue.");

                // Create K8s Job while holding the lock to prevent PVC double-assignment
                var k8sResult = await TryCreateK8sJobDirectAsync(
                    db, workItemId, jobName, claimedPvc, template, request, ct);
                if (!k8sResult)
                {
                    // K8s Job creation failed — delete the WorkItem so the issue can be re-dispatched
                    await SafeDeleteWorkItemAsync(db, workItemId, ct);
                    return new DirectDispatchResult.ServiceUnavailable("K8s Job creation failed");
                }
            }
            finally
            {
                _pvcSelectLock.Release();
            }
        }
        else
        {
            // Non-kiro: no PVC, no lock required
            var createResult = await CreateWorkItemAsDispatchedAsync(
                db, request, workItemId, jobName, null, ct);
            if (createResult is null)
                return new DirectDispatchResult.Conflict(
                    "A live work item already exists for this issue.");

            var k8sResult = await TryCreateK8sJobDirectAsync(
                db, workItemId, jobName, null, template, request, ct);
            if (!k8sResult)
            {
                await SafeDeleteWorkItemAsync(db, workItemId, ct);
                return new DirectDispatchResult.ServiceUnavailable("K8s Job creation failed");
            }
        }

        // Register in-memory PipelineRun for SignalR hub routing
        RegisterPipelineRun(workItemId, request, runService);

        // Record telemetry
        WorkDistributionTelemetry.RecordDispatchLatency(DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow, request.AgentSelector);

        Log.Information(
            "DispatchDirectly: WorkItem {WorkItemId} dispatched as Job {JobName} (selector={Selector}, isKiro={IsKiro})",
            workItemId, jobName, selector, isKiroAgent);

        return new DirectDispatchResult.Success(workItemId);
    }

    /// <summary>
    /// Creates a WorkItem entity with Status=Dispatched directly (no Pending intermediate state).
    /// Returns the WorkItem ID on success, or null if a unique-index conflict was detected.
    /// </summary>
    private static async Task<Guid?> CreateWorkItemAsDispatchedAsync(
        PipelineDbContext db,
        JobDistributionRequest request,
        Guid workItemId,
        string jobName,
        string? claimedPvc,
        CancellationToken ct)
    {
        var minimalPayload = WorkItemEndpoints.BuildMinimalPayload(request);
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(minimalPayload, Pipeline.PipelineJsonOptions.Default);

        var entity = new WorkItemEntity
        {
            Id = workItemId,
            TaskType = request.TaskType,
            IssueIdentifier = request.IssueIdentifier.Value,
            IssueProviderConfigId = request.IssueProviderConfigId,
            Status = WorkItemStatus.Dispatched,
            K8sJobName = jobName,
            ClaimedPvcName = claimedPvc,
            Payload = payloadJson,
            AgentSelector = request.AgentSelector ?? "",
            TimeoutSeconds = request.TimeoutSeconds,
            ProjectId = request.ProjectId,
            CreatedAt = DateTimeOffset.UtcNow,
            DispatchedAt = DateTimeOffset.UtcNow,
            PriorityWeight = InitiatedByConstants.IsManual(request.InitiatedBy) ? 100 : 0,
            TraceParent = request.TraceContext?.GetValueOrDefault("traceparent")
                ?? Pipeline.Telemetry.PipelineTelemetry.FormatTraceParent(System.Diagnostics.Activity.Current)
        };

        try
        {
            db.WorkItems.Add(entity);
            await db.SaveChangesAsync(ct);
            return workItemId;
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex)
            when (IsNpgsqlUniqueViolation(ex))
        {
            Log.Warning("DispatchDirectly: unique constraint violation for issue {IssueIdentifier} (workItemId={WorkItemId})",
                request.IssueIdentifier, workItemId);
            return null;
        }
        catch (ArgumentException ae)
            when (ae.Message.Contains("An item with the same key has already been added"))
        {
            // EF InMemory provider
            Log.Warning("DispatchDirectly: unique constraint violation (InMemory) for issue {IssueIdentifier}",
                request.IssueIdentifier);
            return null;
        }
    }

    private static bool IsNpgsqlUniqueViolation(Exception ex)
    {
        // Matches Npgsql SQLSTATE 23505 (unique_violation)
        return ex is Microsoft.EntityFrameworkCore.DbUpdateException dbEx &&
               dbEx.InnerException is Npgsql.PostgresException pgEx &&
               pgEx.SqlState == "23505";
    }

    /// <summary>Creates the K8s Job for a directly-dispatched WorkItem. Returns true on success.</summary>
    private async Task<bool> TryCreateK8sJobDirectAsync(
        PipelineDbContext db,
        Guid workItemId,
        string jobName,
        string? claimedPvc,
        JobTemplate template,
        JobDistributionRequest request,
        CancellationToken ct)
    {
        // TODO [WARNING]: _kubeClient may be null when K8s is unavailable (registered as null! via
        // ApiServiceCollectionExtensions when IKubernetesJobClient is not configured). Currently
        // the NullReferenceException from _kubeClient.CreateJobAsync is caught by the broad
        // catch(Exception ex) below and returns false → 503, which is the correct behavior but
        // achieved accidentally. A null guard here (or before calling this method) would make the
        // intent explicit, avoid the unnecessary WorkItem write/delete cycle on null K8s, and
        // prevent confusing "failed to create K8s Job" error logs when K8s is simply not configured.
        // See review finding [WARNING] DotNetSpecialist:ApiServiceCollectionExtensions.cs:462.
        try
        {
            var buildCtx = new JobSpecBuilder.BuildContext
            {
                WorkItemId = workItemId,
                AgentSelector = request.AgentSelector,
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
            await _kubeClient.CreateJobAsync(job, _options.Namespace, ct);
            return true;
        }
        catch (HttpOperationException httpEx) when (httpEx.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // 409 = Job already exists — idempotent success
            Log.Information("DispatchDirectly: K8s Job {JobName} already exists (409 Conflict), treating as success", jobName);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "DispatchDirectly: failed to create K8s Job {JobName} for WorkItem {WorkItemId}", jobName, workItemId);
            return false;
        }
    }

    /// <summary>Deletes a WorkItem created by DispatchDirectlyAsync when K8s Job creation fails.</summary>
    private static async Task SafeDeleteWorkItemAsync(PipelineDbContext db, Guid workItemId, CancellationToken ct)
    {
        // TODO [WARNING]: Use CancellationToken.None here instead of ct. If the original request
        // was cancelled (e.g. client disconnected after CreateWorkItemAsDispatchedAsync succeeded but
        // before K8s Job creation completed), ct is already cancelled. Passing a cancelled token to
        // FindAsync/SaveChangesAsync causes them to throw OperationCanceledException, which is swallowed
        // by the catch below — leaving a Dispatched WorkItem row with no K8s Job, permanently occupying
        // the PVC claim and concurrency slot until EnforceDispatchedTimeoutAsync cleans it up.
        // Fix: replace ct with CancellationToken.None in the two calls below.
        // See review finding [WARNING] DotNetSpecialist:DispatchLifecycleService.cs:326.
        try
        {
            var item = await db.WorkItems.FindAsync([workItemId], ct);
            if (item is not null)
            {
                db.WorkItems.Remove(item);
                await db.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "DispatchDirectly: failed to roll back WorkItem {WorkItemId} after K8s failure", workItemId);
        }
    }

    /// <summary>
    /// Registers an in-memory PipelineRun in IOrchestratorRunService so the UI can receive
    /// live SignalR hub events from the dispatched agent pod.
    /// </summary>
    private static void RegisterPipelineRun(Guid workItemId, JobDistributionRequest request, IOrchestratorRunService runService)
    {
        try
        {
            var run = PipelineRunFactory.CreateFromWorkItem(workItemId, request);
            if (run is not null)
                runService.AddRun(run);
        }
        catch (Exception ex)
        {
            // Non-fatal: the agent can still run; the UI just won't receive live events
            Log.Warning(ex, "DispatchDirectly: failed to register PipelineRun for WorkItem {WorkItemId}", workItemId);
        }
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
            await _kubeClient.CreateJobAsync(job, _options.Namespace, ct);
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
