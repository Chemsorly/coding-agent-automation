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

The **authoritative** instances of the services described in this document run in the **Pipeline API** process (`CodingAgent.Api`). The Orchestrator registers read-model replicas of `AgentRegistryService` and `OrchestratorRunService` — backed by `DistributedAgentRegistryService` / `DistributedRunService` when Redis is configured, keeping the Blazor UI in sync without direct DB access.

The Job Controller and Agent pods do **not** hold these singletons. This is important: the guarantee that `AgentEntry.SyncRoot` prevents races on the authoritative dispatch path holds only because all authoritative instances are in the same Pipeline API process.

If a future change splits any of these singletons across processes (e.g., separate API
replicas without Redis), the in-process lock guarantees no longer apply — distributed
coordination (e.g., Postgres advisory locks, Redis `SETNX`) would be required.

> **api.replicas must remain 1 without Redis.** `AgentRegistryService` and `OrchestratorRunService` are in-memory singletons. With Redis configured (`signalr.redis.connectionString`), both switch to distributed Redis-backed implementations — multi-replica is supported in that mode (Spec 046). Without Redis, do not scale the API beyond one replica. See [Multi-replica double-booking prevention](#multi-replica-double-booking-prevention) for what protects against double-booking in the distributed path.

---

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
| `RunLifecycleManager` | `Orchestration/RunLifecycleManager.cs` | `ActiveJobId` mutation on job assignment/completion |
| `AgentOrphanRecoveryService` | `Hub/AgentOrphanRecoveryService.cs` | Check-and-set `ActiveJobId` on reconnect; sets `OrphanRestoredAt` when no active job reported |
| `AgentEndpoints` | `Api/AgentEndpoints.cs` | Sets `ActiveChatSessionId` on chat-resume path |

### Key invariant

All consumers acquire `SyncRoot` in isolation — never nested inside another lock. This
guarantees deadlock freedom: there is no lock ordering to violate because no code path
holds two locks simultaneously.

## Lock Ordering

Only one in-process lock is active in this codebase: `entry.SyncRoot`. All authorized
consumers acquire it in isolation, with no nesting.

### Why this is deadlock-free

- No consumer acquires `SyncRoot` while already holding another lock
- No consumer acquires any other lock while holding `SyncRoot`
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

## Multi-replica double-booking prevention

When `api.replicas > 1` (Redis configured), `AgentRegistryService` is replaced by
`DistributedAgentRegistryService`, which stores agent state in Redis. There is **no
per-agent distributed selection lock** in this path — `TransitionStatusAsync` writes
status to Redis without acquiring any lock. Double-booking is prevented by a layered set
of guards instead:

### Defense 1 — DB partial unique index (strongest)

`WorkItems` has a partial unique index on `(IssueIdentifier, IssueProviderConfigId)` that
excludes terminal statuses (Succeeded=3, Failed=4, Cancelled=5). Any attempt to insert a
second active `WorkItem` for the same issue is rejected by Postgres at the constraint
level, regardless of which API replica makes the insert.

> This is the primary backstop. The guards below reduce the probability of hitting it.

### Defense 2 — `IsIssueBeingProcessed` in-process check

`PipelineRunLifecycleService.IsIssueBeingProcessed()` checks whether the current API
replica is already tracking a run for this issue. Under multi-replica,
`DistributedRunService.IsIssueBeingProcessed()` delegates to `IsIssueDistributedAsync` —
a Postgres query that returns true if a non-terminal `WorkItem` already exists — so this
check is cross-replica even though the method name sounds in-memory.

### Defense 3 — `IsIssueDistributedAsync` from the Scheduler

<!-- TODO: This description overstates the breadth of Defense 3. In practice,
`KubernetesWorkDistributor.IsIssueDistributedAsync()` is called from
`OrphanedLabelRecoveryService` (orphan-recovery path), not on every primary dispatch
decision. The guard fires only during orphan label recovery, not on every new-run
dispatch. Update this section to accurately describe when and where this check fires. -->
`KubernetesWorkDistributor.IsIssueDistributedAsync()` calls the Pipeline API before
dispatching. The Scheduler uses this to avoid re-dispatching an issue that a different
replica already picked up.

### Known gap — no per-agent selection lock

`DistributedAgentRegistryService.TransitionStatusAsync` writes agent status to Redis
without a distributed lock (`lock:agent:{id}` was never implemented). This creates a
small race window: if an agent disconnects while a dispatch is in flight, the agent's
status could be written as Disconnected after the dispatcher has already snapshotted it as
Idle but before the `WorkItem` insert. `ReconciliationService` detects and recovers from
this within its reconciliation interval. Implementing a per-agent Redis lock is tracked as
a separate issue.

---

<!-- TODO: "four" is stale — the system has five distinct processes (Orchestrator, Pipeline API,
Job Controller, Scheduler, Agent), as correctly stated in the Overview. Update this heading. -->
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

### ❌ Don't merge the release-then-reacquire into one lock scope

The pattern of locking `SyncRoot`, mutating a property, releasing, then calling
`TransitionStatus()` (which re-acquires `SyncRoot`) is deliberate. Merging creates
unnecessary coupling and extended lock hold times.

### ❌ Don't add new `SyncRoot` consumers without updating this document

If a new service needs to acquire `AgentEntry.SyncRoot`, add it to the "Authorized
consumers" table above and verify it doesn't introduce lock nesting that violates the
isolation rule.

### ❌ Don't scale the API beyond one replica without Redis

`AgentRegistryService` and `OrchestratorRunService` are process-local singletons without
Redis. With Redis configured, both switch to distributed implementations automatically:
`AgentRegistryService` → `DistributedAgentRegistryService`;
`OrchestratorRunService` uses Redis-backed state.
