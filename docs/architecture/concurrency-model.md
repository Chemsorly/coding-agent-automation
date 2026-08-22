# Concurrency Model — Orchestration Locking Strategy

This document describes the locking patterns used in the orchestration layer. It exists to
prevent well-intentioned "simplification" from introducing race conditions. If you are
modifying concurrency-related code in these services, read this document first.

## Overview

After Spec 045 the system runs as **four distinct processes**, each with its own in-memory
state. The locking invariants below apply within a single process — they do not span process
boundaries.

### Process Map

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  Orchestrator  (CodingAgentWebUI)                                           │
│  ─────────────                                                              │
│  Blazor Server UI                                                           │
│  PipelineLoopService  — polls API for config, dispatches via               │
│                         DispatchOrchestrationService                        │
│  OrphanedLabelRecoveryService                                               │
│  LeaderElection (Lease: caa-{release}-pipeline-loop-lock)                  │
│                                                                             │
│  No EF Core. No AgentHub. Connects to API via IPipelineApiConfigClient,    │
│  IPipelineApiRunHistoryClient, IAgentHubConnection (scoped per circuit).   │
└─────────────────────────────────────────────────────────────────────────────┘
         │  REST calls (HTTP) + SignalR hub subscribe (IAgentHubConnection)
         ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│  Pipeline API  (CodingAgentWebUI.Api)                                       │
│  ──────────────                                                             │
│  AgentHub  — real-time hub for agent pods and Blazor circuits               │
│  AgentRegistryService  ─┐                                                  │
│  OrchestratorRunService ├── singleton in-memory state (locking applies)    │
│  JobDeduplicationGuardService ─┘                                           │
│  PipelineDbContext (EF Core)  — authoritative Postgres access               │
│  WorkItemEndpoints, ConfigEndpoints, PipelineRunEndpoints                  │
│  DatabaseMaintenanceService, WorkItemMetricsBackgroundService              │
│  ConsolidationWorkItemDispatchService                                       │
│  LeaderElection (Lease: caa-{release}-api-lock)                            │
└─────────────────────────────────────────────────────────────────────────────┘
         │  POST /api/work-items (claim)   ▲ hub: ReportOutputLines etc.
         ▼                                 │
┌──────────────────────┐     ┌─────────────────────────────────────────────┐
│  Job Controller      │     │  Agent Pod (CodingAgentWebUI.Agent)          │
│  (CodingAgentWebUI.  │     │  ─────────────                              │
│   JobController)     │     │  Ephemeral K8s Job  (caa-agent-{11 hex})    │
│  ─────────────────── │     │  Connects to API hub as Bearer AGENT_API_KEY│
│  K8s Job dispatch    │     │  GET /api/work-items/{id}/assignment         │
│  Lease: caa-{rel}-   │     │  POST /api/work-items/{id}/status           │
│    dispatch-lock     │     │  hub: ReportStepTransition, etc.            │
└──────────────────────┘     └─────────────────────────────────────────────┘
```

### Where the Locking-Critical Singletons Live

All services described in this document now run exclusively in the **Pipeline API** process
(`CodingAgentWebUI.Api`). They are **not present** in the Orchestrator, Job Controller, or
Agent pods. This is important: the guarantee that `_selectionLock`, `_queueLock`, and
`AgentEntry.SyncRoot` prevent races holds only because all three are in the same process.

If a future change splits any of these singletons across processes (e.g., separate API
replicas), the in-process lock guarantees no longer apply — distributed coordination
(e.g., Postgres advisory locks, Redis `SETNX`) would be required.

> **api.replicas must remain 1.** The SignalR Redis backplane enables hub message delivery
> across replicas but does NOT make multiple API replicas safe. `AgentRegistryService`,
> `OrchestratorRunService`, and `JobDeduplicationGuardService` are in-memory singletons.
> Do not scale the API beyond one replica without replacing these with distributed equivalents.

---

## JobDeduplicationGuardService

**File:** `src/CodingAgentWebUI.Orchestration/Dispatch/JobDeduplicationGuardService.cs`
**Hosted in:** `CodingAgentWebUI.Api`

### Data structures

| Field | Type | Purpose |
|-------|------|---------|
| `_jobQueue` | `ConcurrentQueue<PendingJob>` | FIFO job queue |
| `_processingIssues` | `ConcurrentDictionary<string, bool>` | Deduplication — prevents same issue from being queued twice |
| `_queueLock` | `object` | Guards compound queue operations (scan-and-re-enqueue) |
| `_selectionLock` | `object` | Serializes agent selection to prevent double-selection |

### Lock: `_queueLock`

Guards methods that perform **compound operations** on `_jobQueue`:

- **`DequeueForAgent()`** — Dequeue items one by one, check compatibility, re-enqueue non-matches
- **`RemoveFromQueue()`** — Dequeue all items, skip the target, re-enqueue the rest
- **`Reset()`** — Drain the entire queue (test helper)

These operations are not atomic on `ConcurrentQueue` because they involve multiple
`TryDequeue`/`Enqueue` calls that must appear as a single operation.

### Lock: `_selectionLock`

Guards `SelectAgent()` to prevent two concurrent dispatch paths from selecting the same
agent. Inside this lock, the code:

1. Snapshots idle agents via `_registry.GetIdleAgents()`
2. Filters by label compatibility
3. For each candidate, acquires `candidate.SyncRoot` (nested lock — see Lock Ordering)
4. Verifies the agent is still Idle (double-check pattern)
5. Transitions to Busy atomically

### Lock-free operations

These use `ConcurrentQueue`/`ConcurrentDictionary` atomic APIs and do NOT acquire any lock:

- `EnqueueJob()` — `_processingIssues.TryAdd()` + `_jobQueue.Enqueue()`
- `IsIssueQueued()` — `_processingIssues.ContainsKey()`
- `MarkIssueComplete()` — `_processingIssues.TryRemove()`
- `ReEnqueue()` — `_jobQueue.Enqueue()`
- `QueueLength` — `_jobQueue.Count`
- `GetQueuedJobs()` — `_jobQueue.ToArray()`

## AgentRegistryService

**File:** `src/CodingAgentWebUI.Orchestration/Registry/AgentRegistryService.cs`
**Hosted in:** `CodingAgentWebUI.Api`

### Data structures

| Field | Type | Purpose |
|-------|------|---------|
| `_agents` | `ConcurrentDictionary<string, AgentEntry>` | Primary agent store (keyed by AgentId) |
| `_connectionIndex` | `ConcurrentDictionary<string, AgentEntry>` | Reverse lookup by SignalR ConnectionId |

### Per-entry locking via `SyncRoot`

Each `AgentEntry` (defined in `src/CodingAgentWebUI.Pipeline/Models/AgentEntry.cs`) has:

```csharp
public object SyncRoot => _syncRoot;
```

This provides **fine-grained per-entry locking** for mutable property mutations (Status,
ConnectionId, ActiveJobId, LastHeartbeatAt, etc.). The `ConcurrentDictionary` guarantees
dictionary-level safety (add/remove/lookup), but entry-level mutations need their own lock
because multiple properties must change atomically (e.g., status + timestamp).

### Methods that acquire `entry.SyncRoot`

- **`Register()`** (update factory) — reconnection: updates ConnectionId, resets status
- **`UpdateHeartbeat()`** — updates `LastHeartbeatAt`
- **`TransitionStatus()`** — validates and applies status transitions

## SyncRoot Consumers

The `AgentEntry.SyncRoot` lock is public and acquired by multiple services. This is an
intentional design tradeoff — the alternative (routing all mutations through
`AgentRegistryService`) would bloat its API with dozens of specialized mutation methods.

All authorized consumers run in the **Pipeline API** process. The lock is meaningless
across process boundaries.

### Authorized consumers

| Service | File | Usage |
|---------|------|-------|
| `AgentRegistryService` | `Orchestration/Registry/AgentRegistryService.cs` | `Register()`, `UpdateHeartbeat()`, `TransitionStatus()` |
| `JobDeduplicationGuardService` | `Orchestration/Dispatch/JobDeduplicationGuardService.cs` | `SelectAgent()` — nested inside `_selectionLock` |
| `HeartbeatMonitorService` | `Orchestration/Registry/HeartbeatMonitorService.cs` | Delegates status transitions and orphan cleanup to `IRunLifecycleManager` sweep phases, which acquire `SyncRoot` internally |
| `RunLifecycleManager` | `Orchestration/RunLifecycleManager.cs` | `ActiveJobId` mutation on job assignment/completion |

### Key invariant

Only `JobDeduplicationGuardService.SelectAgent()` nests `SyncRoot` inside another lock
(`_selectionLock`). All other consumers acquire `SyncRoot` in isolation — never nested
inside another lock. This is critical for deadlock freedom (see Lock Ordering below).

## Lock Ordering

The established lock ordering is:

```
_selectionLock → entry.SyncRoot
```

This ordering is enforced in `JobDeduplicationGuardService.SelectAgent()`, which is the **only**
code path that holds two locks simultaneously. The code comment at the nesting site reads:

> Lock ordering: _selectionLock (already held) → entry.SyncRoot (no deadlock risk).

### Why this prevents deadlocks

- `_selectionLock` is only acquired in `SelectAgent()`
- Inside `_selectionLock`, the code acquires `entry.SyncRoot` (inner lock)
- No other code path acquires `_selectionLock` while holding `entry.SyncRoot`
- `HeartbeatMonitorService` and `RunLifecycleManager`
  all acquire `entry.SyncRoot` in isolation — they never hold `_selectionLock`
- Therefore, no circular wait is possible

### Additional rule: `_queueLock` and `_selectionLock` are independent

The current code never nests these two locks. They guard independent concerns:

- `_queueLock` → queue scan operations
- `_selectionLock` → agent selection

Do not introduce nesting between them. If a future change requires both, establish and
document the ordering here before implementing.

## Why ConcurrentQueue + lock

At first glance, wrapping a `ConcurrentQueue` with a lock appears redundant. It is not.

### The compound operation problem

`ConcurrentQueue<T>` guarantees atomic `Enqueue` and `TryDequeue`, but does NOT support:

> "Dequeue the first item matching a predicate, and re-enqueue all non-matches."

`DequeueForAgent()` performs exactly this:

```csharp
lock (_queueLock)
{
    var count = _jobQueue.Count;
    for (var i = 0; i < count; i++)
    {
        if (!_jobQueue.TryDequeue(out var job))
            break;

        if (LabelMatchHelper.IsLabelMatch(agent.Labels, job.RequiredLabels))
            return job;   // Found a match — don't re-enqueue

        _jobQueue.Enqueue(job);  // Not compatible — put it back
    }
}
```

Without `_queueLock`, two concurrent callers could both dequeue the same job, or items
could be duplicated/lost during the scan-and-re-enqueue cycle.

### Why not just use `Queue<T>` + lock everywhere?

Because `QueueLength` and `GetQueuedJobs()` are called from the UI without acquiring
`_queueLock`:

```csharp
public int QueueLength => _jobQueue.Count;
public IReadOnlyList<PendingJob> GetQueuedJobs() => _jobQueue.ToArray().ToList().AsReadOnly();
```

`ConcurrentQueue.Count` and `ConcurrentQueue.ToArray()` are thread-safe **without** a
lock. If we replaced `ConcurrentQueue` with plain `Queue<T>`, these read-only operations
would need to acquire `_queueLock` too, increasing contention on a hot read path.

### Summary

| Operation | Lock required? | Why |
|-----------|---------------|-----|
| `Enqueue` | No | Single atomic operation |
| `TryDequeue` | No | Single atomic operation |
| `Count` | No | Thread-safe snapshot |
| `ToArray()` | No | Thread-safe snapshot |
| Scan-and-re-enqueue | **Yes** (`_queueLock`) | Compound multi-step operation |

## The Release-Then-Reacquire Pattern

Several services follow this pattern when mutating agent state:

```csharp
lock (agent.SyncRoot)
{
    agent.ActiveJobId = null;
}
// Lock released here

_registry.TransitionStatus(agentId, AgentStatus.Idle);
// TransitionStatus() acquires agent.SyncRoot internally
```

This is **intentional**, not an optimization opportunity. `TransitionStatus()` acquires
`SyncRoot` internally. If the caller already held `SyncRoot`, C#'s reentrant `lock`
would allow it, but:

1. It obscures the locking discipline — callers shouldn't need to know that
   `TransitionStatus()` also locks
2. It increases the lock hold duration unnecessarily
3. It creates coupling between the caller's lock scope and the callee's implementation

Do **not** "optimize" this into a single lock scope.

## Cross-Process Communication

The four processes communicate strictly via defined interfaces:

| From | To | Mechanism |
|------|----|-----------|
| Orchestrator | Pipeline API | REST (HTTP via `IPipelineApiConfigClient`, `IPipelineApiWorkItemClient`, `IPipelineApiRunHistoryClient`) |
| Orchestrator | Pipeline API | SignalR hub subscribe (`IAgentHubConnection`, scoped per Blazor circuit) |
| Job Controller | Pipeline API | REST — `POST /api/work-items` claim, workitem status updates |
| Agent pod | Pipeline API | REST — `GET /api/work-items/{id}/assignment`, `POST /api/work-items/{id}/status` |
| Agent pod | Pipeline API | SignalR hub — `ReportOutputLines`, `ReportStepTransition`, `ReportJobCompleted`, etc. |
| Pipeline API | Agent pod | SignalR hub push — token vending, cancellation signals |

There is no direct process-to-process communication between the Orchestrator and
Job Controller, or between the Orchestrator and Agent pods.

## Anti-patterns — Don't Do This

### ❌ Don't replace `ConcurrentQueue` with `Queue<T>`

This breaks `QueueLength` and `GetQueuedJobs()`, which are called without `_queueLock`
from the UI layer. You would need to add locking to every read, increasing contention.

### ❌ Don't remove `_queueLock`

This breaks `DequeueForAgent()` and `RemoveFromQueue()`. The scan-and-re-enqueue pattern
requires mutual exclusion — without it, concurrent scans would corrupt the queue.

### ❌ Don't remove `_selectionLock`

Without it, two concurrent dispatch paths could both snapshot the same idle agent,
both verify it's idle, and both transition it to Busy — double-booking.

### ❌ Don't acquire `_selectionLock` inside `_queueLock`

The current code never nests these. Introducing nesting without establishing ordering
risks deadlock if another path acquires them in reverse order.

### ❌ Don't acquire `_queueLock` inside `_selectionLock`

Same reason. Keep these locks independent.

### ❌ Don't merge the release-then-reacquire into one lock scope

The pattern of locking `SyncRoot`, mutating a property, releasing, then calling
`TransitionStatus()` (which re-acquires `SyncRoot`) is deliberate. Merging creates
unnecessary coupling and extended lock hold times.

### ❌ Don't add new `SyncRoot` consumers without updating this document

If a new service needs to acquire `AgentEntry.SyncRoot`, add it to the "Authorized
consumers" table above and verify it doesn't introduce lock nesting that violates the
ordering rules.

### ❌ Don't scale the API beyond one replica without replacing in-memory singletons

`AgentRegistryService`, `OrchestratorRunService`, and `JobDeduplicationGuardService` are
process-local singletons. A second API replica would have a separate, divergent copy of
each. Horizontal scaling requires replacing these with distributed state (e.g., Postgres
tables, Redis structures) and removing the in-process lock guarantees entirely.
