# Concurrency Model — Orchestration Locking Strategy

This document describes the locking patterns used in the orchestration layer. It exists to
prevent well-intentioned "simplification" from introducing race conditions. If you are
modifying concurrency-related code in these services, read this document first.

## Overview

After Spec 045 the system runs as **five distinct processes** (Orchestrator, Pipeline API, Job Controller, Scheduler, Agent — the Scheduler was split out as a fifth process in Spec 047), each with its own in-memory state. The locking invariants below apply within a single process — they do not span process boundaries.

### Process Map

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  Orchestrator  (CodingAgent.Web)                                           │
│  ─────────────                                                              │
│  Blazor Server UI                                                           │
│  Polls /loop/status on Scheduler for UI state                               │
│  LeaderElection (Lease: caa-{release}-pipeline-loop-lock) — no loop svcs   │
│                                                                             │
│  No EF Core. No AgentHub. Connects to API via IPipelineApiConfigClient,    │
│  IPipelineApiRunHistoryClient, IAgentHubConnection (from Api.Client,       │
│  scoped per circuit).                                                       │
└─────────────────────────────────────────────────────────────────────────────┘
         │  REST calls (HTTP) + SignalR hub subscribe (IAgentHubConnection)
         ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│  Pipeline API  (CodingAgent.Api)                                       │
│  ──────────────                                                             │
│  AgentHub  — real-time hub for agent pods and Blazor circuits               │
│  AgentRegistryService  ─┐                                                  │
│  OrchestratorRunService ├── singleton in-memory state (locking applies)    │
│  AgentReservationService ─┘  (JobDeduplicationGuardService is an alias)    │
│  PipelineDbContext (EF Core)  — authoritative Postgres access               │
│  WorkItemEndpoints, ConfigEndpoints, PipelineRunEndpoints                  │
│  DatabaseMaintenanceService (triggered by Scheduler via HTTP)              │
│  ChatJobDispatcher                                                          │
│  No leader election lease                                                   │
└─────────────────────────────────────────────────────────────────────────────┘
         │  POST /api/work-items (claim)   ▲ hub: ReportOutputLines etc.
         ▼                                 │
┌──────────────────────┐     ┌─────────────────────────────────────────────┐
│  Job Controller      │     │  Agent Pod (CodingAgent.Agent)          │
│  (CodingAgent.       │     │  ─────────────                              │
│   JobController)     │     │  Ephemeral K8s Job                          │
│  ─────────────────── │     │  caa-agent-{11 hex} (impl/review/decomp)   │
│  K8s Job dispatch    │     │  caa-cons-{12 hex}  (consolidation legacy)  │
│  Lease: caa-{rel}-   │     │  Connects to API hub                        │
│    dispatch-lock     │     │  GET /api/work-items/{id}/assignment         │
│                      │     │  POST /api/work-items/{id}/status           │
└──────────────────────┘     └─────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────────────────────┐
│  Scheduler  (CodingAgent.Scheduler)                                    │
│  ─────────────                                                              │
│  PipelineLoopService  — dispatches impl/review/decomp runs                  │
│  OrphanedLabelRecoveryService                                               │
│  HousekeepingService                                                        │
│  WorkItemCountsPoller  — emits WorkDistributionTelemetry gauges             │
│  Lease: caa-{release}-scheduler-lock                                        │
│                                                                             │
│  No EF Core. All persistence via Pipeline API (HTTP).                       │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Where the Locking-Critical Singletons Live

The **authoritative** instances of the services described in this document run in the **Pipeline API** process (`CodingAgent.Api`). The Orchestrator registers read-model replicas of `AgentRegistryService` and `OrchestratorRunService` — backed by `DistributedAgentRegistryService` / `DistributedRunService` when Redis is configured, keeping the Blazor UI in sync without direct DB access. `AgentReservationService` (with `_selectionLock`) is also registered in the Orchestrator but dispatch decisions that actually reserve agents go through the API path.

The Job Controller and Agent pods do **not** hold these singletons. This is important: the guarantee that `_selectionLock` and `AgentEntry.SyncRoot` prevent races on the authoritative dispatch path holds only because all authoritative instances are in the same Pipeline API process.

If a future change splits any of these singletons across processes (e.g., separate API
replicas without Redis), the in-process lock guarantees no longer apply — distributed
coordination (e.g., Postgres advisory locks, Redis `SETNX`) would be required.

> **api.replicas must remain 1 without Redis.** `AgentRegistryService`, `OrchestratorRunService`, and `AgentReservationService` (formerly `JobDeduplicationGuardService`) are in-memory singletons. With Redis configured (`signalr.redis.connectionString`), `AgentRegistryService` and `OrchestratorRunService` automatically switch to distributed Redis-backed implementations — multi-replica is supported in that mode (Spec 046). Without Redis, do not scale the API beyond one replica.

---

## JobDeduplicationGuardService / AgentReservationService

**File:** `src/CodingAgent.Orchestration/Registry/AgentReservationService.cs`
**Authoritative instance hosted in:** `CodingAgent.Api` (Pipeline API) — all actual dispatch decisions go through this process. The Orchestrator also registers a local `AgentReservationService` instance for its own routing lookups, but it does not participate in the authoritative agent-reservation path.

> **Rename note (Spec 046):** `JobDeduplicationGuardService` was renamed to `AgentReservationService` (Spec 046). Both classes are defined in `AgentReservationService.cs`. All new code should reference `AgentReservationService` directly; `JobDeduplicationGuardService` is the legacy wrapper and IS marked `[Obsolete("Use AgentReservationService instead. Renamed in Spec 046.")]`.

> **T18 note (arch-audit 2026-08-22):** All in-memory queue methods (`EnqueueJob`, `DequeueForAgent`,
> `GetJobPriority`, `IsIssueQueued`, `GetQueuedJobs`, `ReEnqueue`, `RemoveFromQueue`, `RemoveJob`,
> `MarkIssueComplete`, `QueueLength`) were deleted. The deduplication queue was a dead no-op with no
> production writers. Only `SelectAgent` and `ResolveRequiredLabels` remain.

### Data structures

| Field | Type | Purpose |
|-------|------|---------|
| `_selectionLock` | `object` | Serializes agent selection to prevent double-selection |

### Lock: `_selectionLock`

Guards `SelectAgent()` to prevent two concurrent dispatch paths from selecting the same
agent. Inside this lock, the code:

1. Snapshots idle agents via `_registry.GetIdleAgents()`
2. Filters by label compatibility
3. For each candidate, acquires `candidate.SyncRoot` (nested lock — see Lock Ordering)
4. Verifies the agent is still Idle (double-check pattern)
5. Transitions to Busy atomically

### Lock-free operations

These use `ConcurrentDictionary` atomic APIs and do NOT acquire any lock:

- `ResolveRequiredLabels()` — pure static computation, no shared state

## AgentRegistryService

**File:** `src/CodingAgent.Orchestration/Registry/AgentRegistryService.cs`
**Hosted in:** `CodingAgent.Api`

### Data structures

| Field | Type | Purpose |
|-------|------|---------|
| `_agents` | `ConcurrentDictionary<string, AgentEntry>` | Primary agent store (keyed by AgentId) |
| `_connectionIndex` | `ConcurrentDictionary<string, AgentEntry>` | Reverse lookup by SignalR ConnectionId |

### Per-entry locking via `SyncRoot`

Each `AgentEntry` (defined in `src/CodingAgent.Contracts/Models/AgentEntry.cs`) has:

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
| `JobDeduplicationGuardService` | `Orchestration/Registry/AgentReservationService.cs` | `SelectAgent()` — nested inside `_selectionLock` |
| `RunLifecycleManager` | `Orchestration/RunLifecycleManager.cs` | `ActiveJobId` mutation on job assignment/completion |
| `AgentOrphanRecoveryService` | `Hub/AgentOrphanRecoveryService.cs` | Check-and-set `ActiveJobId` on reconnect; sets `OrphanRestoredAt` when no active job reported |
| `AgentEndpoints` | `Api/AgentEndpoints.cs` | Sets `ActiveChatSessionId` on chat-resume path |

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
- `AgentOrphanRecoveryService`, `RunLifecycleManager`, and `AgentEndpoints`
  all acquire `entry.SyncRoot` in isolation — they never hold `_selectionLock`
- Therefore, no circular wait is possible

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

### ❌ Don't remove `_selectionLock`

Without it, two concurrent dispatch paths could both snapshot the same idle agent,
both verify it's idle, and both transition it to Busy — double-booking.

### ❌ Don't acquire `_selectionLock` while holding `entry.SyncRoot`

The current lock ordering is `_selectionLock` → `entry.SyncRoot`. Reversing this (holding
`SyncRoot` first and then acquiring `_selectionLock`) creates a potential circular wait.

### ❌ Don't merge the release-then-reacquire into one lock scope

The pattern of locking `SyncRoot`, mutating a property, releasing, then calling
`TransitionStatus()` (which re-acquires `SyncRoot`) is deliberate. Merging creates
unnecessary coupling and extended lock hold times.

### ❌ Don't add new `SyncRoot` consumers without updating this document

If a new service needs to acquire `AgentEntry.SyncRoot`, add it to the "Authorized
consumers" table above and verify it doesn't introduce lock nesting that violates the
ordering rules.

### ❌ Don't scale the API beyond one replica without Redis

`AgentRegistryService`, `OrchestratorRunService`, and `AgentReservationService` are
process-local singletons without Redis. With Redis configured, all three switch to
distributed implementations automatically: `AgentRegistryService` -> `DistributedAgentRegistryService`;
`OrchestratorRunService` uses Redis-backed state; `AgentReservationService` uses per-agent
Redis locks (`lock:agent:{id}`, 5-second TTL) instead of the in-process `_selectionLock`,
enabling safe agent selection across API replicas.



---

## PVC Dispatch Race in Multi-Replica Deployments

**Affected endpoint:** `POST /api/work-items/dispatch` (`DispatchWorkItem` in `WorkItemEndpoints.cs`)  
**Affected task type:** Consolidation only (Implementation, Review, and Decomposition use the `Pending` enqueue path — see below)

### Background

`DispatchLifecycleService` owns two PVC-related methods:

- **`QueryAvailablePvcsAsync`** (static) — queries the database for in-flight `ClaimedPvcName` values across `Pending`, `Dispatched`, and `Running` WorkItems, then computes the available subset of the configured PVC pool.
- **`SelectPvcAsync`** (private, instance) — acquires `_pvcSelectLock` (`SemaphoreSlim(1, 1)`) and dequeues the first entry from the caller's in-memory `availablePvcs` list.

`DispatchWorkItem` calls `QueryAvailablePvcsAsync` as a fast availability pre-check **outside** the lock, then passes the resulting in-memory list into `ExecuteDispatchLifecycleAsync`, which calls `SelectPvcAsync` under `_pvcSelectLock`.

### Why `_pvcSelectLock` Does Not Help Across Replicas

`_pvcSelectLock` is an in-process `SemaphoreSlim` — it serialises concurrent requests within a single API pod. In a multi-replica deployment each pod holds its own independent `DispatchLifecycleService` instance with its own `_pvcSelectLock` and its own in-memory PVC list. The lock has no visibility across pod boundaries.

> **Important:** Redis (configured via `signalr.redis.connectionString`) enables safe agent selection and run tracking across replicas by switching `AgentRegistryService`, `OrchestratorRunService`, and `AgentReservationService` to distributed implementations. **Redis does not fix the PVC dispatch race.** The two mechanisms address different singletons. Eliminating the PVC race requires a separate Postgres advisory lock (see "Known Improvement Path" below).

### Race Sequence

1. Two API replicas (A and B) both receive a `POST /api/work-items/dispatch` request for a Consolidation issue at approximately the same time.
2. Both replicas independently call `QueryAvailablePvcsAsync`. Because neither has committed a `Dispatched` WorkItem row yet, both see the same DB state and both compute a non-empty `availablePvcs` list (e.g., `["kiro-pvc-1"]`).
3. Both replicas commit their `Dispatched` WorkItem rows to the database (with distinct `WorkItemId` values, or with the same `RunId`-derived ID — the second insert triggers the idempotency-retry path).
4. Both enter `ExecuteDispatchLifecycleAsync` with non-empty in-memory lists.
5. Each replica calls `SelectPvcAsync` under its own in-process `_pvcSelectLock`. Because the lock is per-pod, **both replicas successfully dequeue the same PVC name** from their respective in-memory lists.
6. Both replicas proceed to create a K8s Job.

### How the Race Is Detected and Cleaned Up

After K8s Job creation, `HandleOrphanedJobIfRaceDetectedAsync` clears the EF change tracker and reloads the WorkItem from the database. One of two outcomes occurs:

**Path A — status mismatch:** The winning replica's `FinalizeDispatchAsync` already transitioned the WorkItem to `Dispatched` (or to a further status). The losing replica finds the WorkItem in an unexpected state, releases the PVC back to its in-memory list, deletes its orphaned K8s Job (best-effort), and returns early without dispatching.

**Path B — concurrency conflict:** Both replicas reach `FinalizeDispatchAsync` simultaneously. The EF concurrency token causes one `SaveChangesAsync` call to throw `DbUpdateConcurrencyException`. The winner's row is preserved; the loser's save is discarded. The K8s Job created by the losing replica is orphaned in Kubernetes and will be cleaned up by the ReconciliationService on its next cycle.

In both paths, `DispatchWorkItem` detects `dispatched == false` and:
1. Calls `SafelyCancelOrphanedDispatchedWorkItemAsync` → `FailWorkItemAsync`, which transitions the orphaned `Dispatched` WorkItem row to `Failed` (`FailureReason.InfrastructureFailure`).
2. Returns HTTP `503 Service Unavailable`.

### Why the 503 Is Self-Healing

`KubernetesWorkDistributor.DispatchSynchronouslyAsync` (in the Orchestrator / Scheduler process) calls the dispatch endpoint and catches `ServiceUnavailable`, returning `DistributionResult(Success: false, ...)`.

`DispatchOrchestrationService.DistributeAndFinalizeAsync` receives the failed result and calls `RevertFailedDistributionAsync`, which calls `SwapLabelAsync(..., AgentLabels.Next, ...)`.

**Crucially**, on the 503 path `ConfirmDistributionLabelAsync` was never reached — that method swaps the label to `agent:in-progress` only after a successful dispatch. The issue label was still `agent:next` when `RevertFailedDistributionAsync` runs; re-applying `agent:next` is a defensive no-op. The issue naturally stays `agent:next` and the Scheduler re-picks it on its next poll cycle (default: 60 seconds). **No data is lost; the race adds at most one poll-interval of latency.**

### Scope of Impact

This race applies **only to the Consolidation task type**. Implementation, Review, and Decomposition work items are created as `Pending` via `EnqueueAsPendingAsync` (`POST /api/work-items`). That path does not perform a PVC pre-check in `DispatchWorkItem` — PVC allocation happens later inside `WorkItemDispatchService`, which runs on a single elected leader and is not subject to the cross-replica race described here.

### Known Improvement Path

Moving `QueryAvailablePvcsAsync` inside `_pvcSelectLock` would reduce the race window but would increase lock hold time on every request. The definitive fix is a **Postgres advisory lock** on the PVC slot ID (`SELECT pg_try_advisory_xact_lock(hash_of_pvc_name)`) taken at the start of `DispatchWorkItem`, ensuring only one replica enters the PVC-claim path at a time. This is tracked as a known improvement and is out of scope for the current change.
