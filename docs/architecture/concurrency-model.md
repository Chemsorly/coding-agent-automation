# Concurrency Model — Orchestration Locking Strategy

This document describes the locking patterns used in the orchestration layer. It exists to
prevent well-intentioned "simplification" from introducing race conditions. If you are
modifying concurrency-related code in these services, read this document first.

## Overview

The orchestrator runs as a single process with several singleton services that receive
concurrent access from:

- **SignalR hub callbacks** — agent registration, heartbeats, job completion reports
- **Dispatch timer** — periodic job dispatch loop (`DispatchLoopService`)
- **Heartbeat monitor** — periodic health checks (`HeartbeatMonitorService`)
- **API endpoints** — UI-triggered enqueue, cancel, and status queries
- **Run lifecycle events** — job assignment and completion (`RunLifecycleManager`)

All services listed below are registered as singletons in DI and share mutable state
through `ConcurrentDictionary` and `ConcurrentQueue` collections, with additional locks
for compound operations.

## JobDispatcherService

**File:** `src/CodingAgentWebUI.Orchestration/Dispatch/JobDispatcherService.cs`

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

### Authorized consumers

| Service | File | Usage |
|---------|------|-------|
| `AgentRegistryService` | `Orchestration/Registry/AgentRegistryService.cs` | `Register()`, `UpdateHeartbeat()`, `TransitionStatus()` |
| `JobDispatcherService` | `Orchestration/Dispatch/JobDispatcherService.cs` | `SelectAgent()` — nested inside `_selectionLock` |
| `HeartbeatMonitorService` | `Orchestration/Registry/HeartbeatMonitorService.cs` | Status transitions, orphan detection and cleanup |
| `RunLifecycleManager` | `Orchestration/RunLifecycleManager.cs` | `ActiveJobId` mutation on job assignment/completion |
| `SignalRWorkDistributorAgentResolver` | `Orchestration/Dispatch/SignalRWorkDistributorAgentResolver.cs` | `AssignJob()`, `ReleaseAgent()` |

### Key invariant

Only `JobDispatcherService.SelectAgent()` nests `SyncRoot` inside another lock
(`_selectionLock`). All other consumers acquire `SyncRoot` in isolation — never nested
inside another lock. This is critical for deadlock freedom (see Lock Ordering below).

## Lock Ordering

The established lock ordering is:

```
_selectionLock → entry.SyncRoot
```

This ordering is enforced in `JobDispatcherService.SelectAgent()`, which is the **only**
code path that holds two locks simultaneously. The code comment at the nesting site reads:

> Lock ordering: _selectionLock (already held) → entry.SyncRoot (no deadlock risk).

### Why this prevents deadlocks

- `_selectionLock` is only acquired in `SelectAgent()`
- Inside `_selectionLock`, the code acquires `entry.SyncRoot` (inner lock)
- No other code path acquires `_selectionLock` while holding `entry.SyncRoot`
- `HeartbeatMonitorService`, `RunLifecycleManager`, and `SignalRWorkDistributorAgentResolver`
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
