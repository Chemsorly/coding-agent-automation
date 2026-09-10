using CodingAgent.Api.Client;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.LeaderElection;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CodingAgent.Api.Dispatch;

/// <summary>
/// BackgroundService that polls for non-consolidation Pending WorkItems
/// (Implementation, Review, Decomposition) ordered by <c>PriorityWeight DESC, CreatedAt ASC</c>
/// and dispatches them as K8s Jobs when capacity is available.
///
/// <para>
/// Flow: Scheduler enqueues an issue as a <c>Pending</c> WorkItem (visible in the UI queue) via
/// <c>KubernetesWorkDistributor.DistributeAsync → POST /api/work-items</c>. This service picks up
/// the item, checks concurrency and PVC availability, and calls
/// <c>DispatchLifecycleService.ExecuteDispatchLifecycleAsync</c> to create the K8s Job and
/// transition the WorkItem to <c>Dispatched</c>. Because <c>PriorityWeight</c> is applied at
/// poll time, operators can re-order the queue by updating it via
/// <c>POST /api/work-items/{id}/priority</c> before the item is claimed.
/// </para>
/// <para>
/// The label is already <c>agent:in-progress</c> when items reach this service — it was swapped
/// by <c>DistributeAndFinalizeAsync</c> at enqueue time. No additional label swap is performed here.
/// </para>
/// <para>
/// <b>Multi-replica note:</b> all API replicas run this service simultaneously (via
/// <see cref="AlwaysLeaderService"/>). The CAS <c>TransitionIfAsync(Pending → Dispatched)</c>
/// in <see cref="DispatchLifecycleService"/> prevents the same item from being double-claimed.
/// However, <c>maxConcurrent</c> per selector is not atomically enforced across replicas:
/// two replicas can both read a concurrency snapshot showing capacity available and each
/// dispatch a different item for the same selector. This is an accepted limitation for
/// single-replica deployments; see <see cref="AlwaysLeaderService"/> for details.
/// </para>
/// </summary>
internal sealed class WorkItemDispatchService : LeaderElectedPollingService
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<WorkItemDispatchService>();

    private readonly DispatchLifecycleService _lifecycle;
    private readonly DispatchServiceOptions _options;
    private readonly WorkItemTransitionService _transitionService;
    private readonly DispatchStateBuilder _stateBuilder;

    protected override string ServiceName => "WorkItemDispatchService";
    protected override int PollIntervalSeconds => _options.PollIntervalSeconds;

    public WorkItemDispatchService(WorkItemDispatchServiceDependencies deps)
        : this(deps ?? throw new ArgumentNullException(nameof(deps)),
               DispatchServiceOptionsFactory.Create(deps.Configuration))
    { }

    /// <summary>
    /// Internal constructor accepting a pre-built <see cref="DispatchServiceOptions"/>.
    /// Used directly by tests to avoid requiring <see cref="IConfiguration"/>.
    /// </summary>
    internal WorkItemDispatchService(WorkItemDispatchServiceDependencies deps, DispatchServiceOptions options)
        : base((deps ?? throw new ArgumentNullException(nameof(deps))).LeaderElection,
               (options ?? throw new ArgumentNullException(nameof(options))).RateLimitPerSecond)
    {
        _lifecycle = deps.Lifecycle;
        _transitionService = deps.TransitionService;
        _options = options;
        ArgumentNullException.ThrowIfNull(deps.StateBuilder, nameof(deps.StateBuilder));
        _stateBuilder = deps.StateBuilder;
    }

    /// <inheritdoc/>
    protected override Task OnPollCycleAsync(CancellationToken ct) => PollAndDispatchAsync(ct);

    internal async Task PollAndDispatchAsync(CancellationToken ct)
    {
        // Poll all non-consolidation Pending WorkItems, ordered by PriorityWeight DESC, CreatedAt ASC.
        // recordTelemetry:false — only the Job Controller is the authoritative emitter for
        // workdistribution.dispatcher_last_poll_epoch_seconds and credential pool metrics.
        var state = await _stateBuilder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: false,
            ct);
        if (state is null)
            return;

        await using (state.Db)
        {
            var rateLimiter = RateLimiter ?? throw new InvalidOperationException(
                $"{ServiceName} requires a rate limiter but RateLimiter is null. " +
                "Ensure the constructor passes rateLimitPerSecond to the base class.");
            await foreach (var candidate in _stateBuilder.GetEligibleCandidatesAsync(
                state, LeaderElection, rateLimiter,
                ServiceName,
                async (item, msg, token) =>
                    await FailWorkItemAsync(item.Id, msg, token),
                ct))
            {
                await DispatchItemAsync(
                    state.Db, candidate.Item, candidate.Template,
                    candidate.IsKiroAgent, state.AvailablePvcs, state.ConcurrencyBySelector, ct);
            }
        }
    }

    private async Task DispatchItemAsync(
        PipelineDbContext db,
        PendingWorkItemProjection item,
        JobTemplate template,
        bool isKiroAgent,
        List<string> availablePvcs,
        Dictionary<string, int> concurrencyBySelector,
        CancellationToken ct)
    {
        await _lifecycle.ExecuteDispatchLifecycleAsync(
            new DispatchLifecycleContext(db, item, template, isKiroAgent, availablePvcs, concurrencyBySelector, "workitem-dispatch ")
            {
                // Default ExpectedInitialStatus = Pending — items were created as Pending by the Scheduler.
            },
            prepareVariant: async workItem =>
            {
                // Load project secrets if a project is configured.
                Dictionary<string, string>? projectSecrets = null;
                if (workItem.ProjectId.HasValue)
                    projectSecrets = await DispatchLifecycleService.LoadProjectSecretsAsync(
                        db, workItem.ProjectId.Value.ToString(), ct);
                return (shouldContinue: true, projectSecrets);
            },
            onDispatchSuccess: _ =>
            {
                // Label is already agent:in-progress (swapped at enqueue time by
                // DistributeAndFinalizeAsync). No label swap needed here.
                Log.Information(
                    "WorkItemDispatchService: K8s Job created for WorkItem {WorkItemId} (issue {IssueIdentifier})",
                    item.Id, item.IssueIdentifier);
                return Task.CompletedTask;
            },
            ct,
            onFailure: async (workItemId, errorMessage) =>
            {
                Log.Warning(
                    "WorkItemDispatchService: dispatch failed for WorkItem {WorkItemId} (issue {IssueIdentifier}): {Error}",
                    workItemId, item.IssueIdentifier, errorMessage);
            });
    }

    private async Task FailWorkItemAsync(Guid workItemId, string errorMessage, CancellationToken ct)
    {
        Log.Error("WorkItemDispatchService: failing WorkItem {WorkItemId}: {Error}", workItemId, errorMessage);
        await _lifecycle.FailWorkItemAsync(workItemId, errorMessage, ct);
    }
}
