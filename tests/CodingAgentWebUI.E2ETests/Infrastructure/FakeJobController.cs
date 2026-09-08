using System.Collections.Concurrent;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.E2ETests.Fakes;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.E2ETests.Infrastructure;

/// <summary>
/// Stands in for <c>CodingAgentWebUI.JobController</c>'s dispatch loop.
///
/// Before Spec 041 the monolith's SignalR work distributor pushed a job straight to a connected
/// agent, so a WorkItem went <c>Pending → Dispatched</c> inside <c>DistributeAsync</c>. Spec 041
/// deleted that mode and Spec 043 moved dispatch into a separate Job Controller process, which the
/// E2E harness does not run — so nothing moved a WorkItem out of <c>Pending</c> and every test
/// that waited for <c>Dispatched</c> timed out.
///
/// This class restores the missing link by doing what the real controller does, against the real
/// endpoints:
/// <list type="number">
///   <item>poll <c>GET /api/work-items/pending</c>;</item>
///   <item>pick an idle registered agent whose labels satisfy the item's selector;</item>
///   <item><c>POST /api/work-items/{id}/claim</c> with that agent as <c>AssignedAgentId</c>
///         — the transition to <c>Dispatched</c>;</item>
///   <item>bootstrap the agent the way a pod bootstraps: fetch the assignment over HTTP, then
///         re-register on the hub declaring its active job.</item>
/// </list>
///
/// Step 4 matters twice over. It is how a real work-item pod learns its job (there is no
/// <c>AssignJob</c> push in Kubernetes mode), and it is what sets <c>ActiveJobId</c> in the
/// registry — without which the now-active <c>AgentAuthorizationFilter</c> rejects every
/// <c>[RequiresActiveJob]</c> call the agent goes on to make.
/// </summary>
public sealed class FakeJobController : IAsyncDisposable
{
    private readonly IPipelineApiWorkItemClient _workItems;
    private readonly AgentRegistryService _registry;
    private readonly InMemoryConfigurationStore _configStore;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    /// <summary>Work item ids this controller has claimed, for test assertions.</summary>
    public List<Guid> ClaimedWorkItemIds { get; } = [];

    public FakeJobController(
        IPipelineApiWorkItemClient workItems,
        AgentRegistryService registry,
        InMemoryConfigurationStore configStore)
    {
        _workItems = workItems;
        _registry = registry;
        _configStore = configStore;
        _loop = Task.Run(() => PollAsync(_cts.Token));
    }

    private async Task PollAsync(CancellationToken ct)
    {
        // 250ms keeps a dispatch well inside the 10s waits the tests use without busy-spinning.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await DispatchOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // A poll failing is not fatal — the API host may still be starting, or a claim may
                // have raced another iteration. The next tick retries; a genuine failure surfaces
                // as the test's own timeout with its own diagnostic.
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Runs a single dispatch pass. Exposed so a test can drive dispatch deterministically
    /// instead of waiting for the poll interval.
    /// </summary>
    public async Task DispatchOnceAsync(CancellationToken ct = default)
    {
        await ReconcileOnceAsync(ct);

        var pending = await _workItems.GetPendingAsync(50, ct: ct);
        if (pending.Count == 0) return;

        foreach (var item in pending)
        {
            var agent = FindIdleAgentFor(item.AgentSelector);
            if (agent is null) continue; // no capacity — the item stays Pending, as in production

            var claim = await _workItems.ClaimAsync(
                item.Id,
                new ClaimWorkItemRequest
                {
                    AssignedAgentId = agent.AgentId.Value,
                    DispatchedAt = DateTimeOffset.UtcNow
                },
                ct);

            // null means another claimant won the race (409). Leave it alone.
            if (claim is null) continue;

            ClaimedWorkItemIds.Add(item.Id);
            _inFlight[item.Id] = agent.AgentId.Value;

            // The in-progress label, exactly as DispatchLoop posts it once the Job exists. It is
            // the only signal an operator has on the issue tracker that work has started, and the
            // terminal swap the agent posts later replaces it rather than appending — so a harness
            // that skips this step shows the issue going straight from untouched to done, and the
            // epic-decomposition tests that assert on the transition fail on the label they never
            // saw. Non-fatal here for the same reason it is there: the work is already running.
            try
            {
                await _workItems.PostLabelSwapAsync(item.Id, "agent:in-progress", ct);
            }
            catch
            {
                // Swallowed deliberately — see above.
            }

            if (FakeAgentClient.TryGetConnected(agent.AgentId.Value, out var fakeAgent))
                await fakeAgent.StartAssignedWorkItemAsync(item.Id, ct);
        }
    }

    /// <summary>
    /// Fails work items whose agent has gone away.
    ///
    /// This is the Kubernetes-mode liveness path. Before Spec 041, <c>HeartbeatMonitorService</c>
    /// swept the registry and failed the run when an agent stopped responding; that service is
    /// deliberately not registered in Kubernetes mode, where a dead agent shows up instead as a
    /// failed Job that <c>ReconciliationLoop</c> reconciles into a status post. The harness has no
    /// real Jobs, so a pod dying is modelled as its agent leaving the registry.
    ///
    /// Without this, a work item whose agent disconnects sits in <c>Running</c> forever and every
    /// test asserting the disconnect path times out.
    ///
    /// <para>
    /// A disconnect is not immediately fatal. <c>AgentDisconnectGracePeriod</c> is what lets a pod
    /// survive a dropped websocket and re-register with its job intact, so this waits it out before
    /// declaring the pod dead — reading the same configured value the product does. Failing on the
    /// first Disconnected sighting made the reconnection path untestable: the work item was already
    /// Failed by the time the agent came back, so there was no orphan left to restore.
    /// </para>
    /// </summary>
    private async Task ReconcileOnceAsync(CancellationToken ct)
    {
        if (_inFlight.IsEmpty) return;

        var gracePeriod = (await _configStore.LoadPipelineConfigAsync(ct)).AgentDisconnectGracePeriod;

        foreach (var (workItemId, agentId) in _inFlight.ToArray())
        {
            // A dropped agent is not removed from the registry — OnDisconnectedAsync transitions it
            // to Disconnected and leaves it there for the grace period. Treat both that state and a
            // missing entry as "the pod is gone".
            var entry = _registry.GetByAgentId(new AgentId(agentId));
            if (entry is not null && entry.Status != AgentStatus.Disconnected)
            {
                // Back, or never left. Any earlier absence no longer counts against it.
                _goneSince.TryRemove(workItemId, out _);
                continue;
            }

            var goneSince = _goneSince.GetOrAdd(workItemId, _ => DateTimeOffset.UtcNow);
            if (DateTimeOffset.UtcNow - goneSince < gracePeriod)
                continue; // inside the grace period — give it a chance to come back

            _goneSince.TryRemove(workItemId, out _);
            _inFlight.TryRemove(workItemId, out _);

            await _workItems.PostStatusAsync(
                workItemId,
                new WorkItemStatusUpdate
                {
                    Status = nameof(WorkItemStatus.Failed),
                    AgentId = agentId,
                    ErrorMessage = "Agent pod terminated before reporting completion.",
                    FailureReason = nameof(CodingAgentWebUI.Pipeline.Models.FailureReason.InfrastructureFailure)
                },
                ct);
        }
    }

    /// <summary>Claimed work items still believed to be running, keyed by work item id.</summary>
    private readonly ConcurrentDictionary<Guid, string> _inFlight = new();

    /// <summary>
    /// When each in-flight item's agent was first seen gone, so the disconnect grace period is
    /// measured from the disconnect rather than from the poll that noticed it.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _goneSince = new();

    /// <summary>Stops tracking a work item, so a completed job is not reconciled as a dead pod.</summary>
    internal void ForgetInFlight(Guid workItemId)
    {
        _inFlight.TryRemove(workItemId, out _);
        _goneSince.TryRemove(workItemId, out _);
    }

    /// <summary>Drops all in-flight tracking. Called between tests so a work item from a
    /// previous test cannot be reconciled into a failure during the next one.</summary>
    internal void ForgetAllInFlight()
    {
        _inFlight.Clear();
        _goneSince.Clear();
    }

    /// <summary>
    /// Picks an idle agent whose labels satisfy the selector. The real controller matches a
    /// selector to a job template and starts a pod; the harness has its pods already connected,
    /// so it matches against their labels instead. An empty selector matches any idle agent,
    /// mirroring how an unlabelled template is dispatchable anywhere.
    ///
    /// Two things the registry does not do for us:
    ///
    /// <c>GetIdleAgents</c> filters on <c>Status == Idle</c> only, so an agent an operator has
    /// disabled is still in the list. Selecting one would dispatch work to an agent that is
    /// meant to be drained, so it is excluded here.
    ///
    /// The same call returns <c>ConcurrentDictionary.Values</c>, whose order is unspecified.
    /// Picking the first would make selection depend on hash order — a coin flip that is stable
    /// within a run and different across runs, which is the worst kind of flake. Longest-idle
    /// first is deterministic and is the policy the lifecycle tests describe. Production has no
    /// equivalent choice to make: it starts one pod per work item rather than choosing among a
    /// standing pool.
    /// </summary>
    private AgentEntry? FindIdleAgentFor(string selector)
    {
        var idle = _registry.GetIdleAgents()
            .Where(a => !a.Disabled)
            .OrderBy(a => a.LastJobCompletedAt ?? a.RegisteredAt)
            .ToList();

        if (idle.Count == 0) return null;

        if (string.IsNullOrWhiteSpace(selector))
            return idle[0];

        var required = selector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return idle.FirstOrDefault(a =>
            required.All(r => a.Labels.Contains(r, StringComparer.OrdinalIgnoreCase)));
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await _loop;
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        _cts.Dispose();
    }
}
