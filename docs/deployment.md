# Deployment

## Architecture

The application runs on Kubernetes as five distinct processes:

- **Orchestrator** (`CodingAgentWebUI`) — Blazor Server app. Hosts the web UI. No direct database access — all config and run history read from the Pipeline API via HTTP. `IAgentHubConnection` (scoped per circuit) subscribes to the API hub for live run streaming.
- **Pipeline API** (`CodingAgentWebUI.Api`) — HTTP and SignalR hub server. Authoritative database owner (EF Core + Postgres). Hosts `AgentHub`, `AgentRegistryService`, `OrchestratorRunService`, `JobDeduplicationGuardService`, `ConsolidationWorkItemDispatchService`, `DatabaseMaintenanceService`, and `ChatJobDispatcher`.
- **Job Controller** (`CodingAgentWebUI.JobController`) — Kubernetes Job dispatch. Claims `WorkItem` rows from the API and creates K8s Jobs. Leader-elected via `caa-{release}-dispatch-lock` Lease. Stateless between dispatches; all state lives in Postgres via the API.
- **Scheduler** (`CodingAgentWebUI.Scheduler`) — Owns all scheduled/periodic background work: orphaned label recovery, housekeeping, work-item metrics polling, and periodic maintenance sweeps. No direct Postgres connection — all persistence goes through the Pipeline API. Leader-elected via `caa-{release}-scheduler-lock` Lease.
- **Agent Host** (`CodingAgentWebUI.Agent`) — Ephemeral K8s Job pod. Connects to the Pipeline API hub using `AGENT_API_KEY` as a Bearer token. Picks up assignments via `GET /api/work-items/{id}/assignment`, reports progress and terminal status via hub methods and `POST /api/work-items/{id}/status`. Two execution modes: _work-item pods_ (spawned with `--work-item-id`) and _chat pods_.

Supporting libraries (shared, not deployed independently):

- **Orchestration** (`CodingAgentWebUI.Orchestration`) — Dispatch logic, agent registry, run lifecycle, telemetry. Linked into the Pipeline API.
- **Infrastructure.Persistence** (`CodingAgentWebUI.Infrastructure.Persistence`) — EF Core context, database migrations, config store. Linked into the Pipeline API.
- **Infrastructure.Providers** (`CodingAgentWebUI.Infrastructure.Providers`) — Provider implementations (GitHub, GitLab, filesystem), token vending. Linked into the Pipeline API and Agent. The Blazor Orchestrator (`CodingAgentWebUI`) and Job Controller have no direct reference.
- **Pipeline** (`CodingAgentWebUI.Pipeline`) — Core pipeline model, step execution, `PipelineLoopService`, interfaces, constants. Linked into the Orchestrator and Pipeline API.
- **Hub** (`CodingAgentWebUI.Hub`) — Full hub implementation: `AgentHub` (split across partial classes), authentication handlers (`AgentApiKeyAuthHandler`), `ChatJobDispatcher` (ephemeral chat pod dispatch), job lifecycle services (`AgentJobLifecycleService`, `AgentOrphanRecoveryService`, `AgentTokenRefreshService`), completion strategies, `AgentHubFacade`, and DI wiring. Linked into the Pipeline API and Orchestrator.

### Agent API Keys

The orchestrator and agents authenticate using HMAC-derived keys. Set a shared master secret:

```bash
echo "AGENT_API_KEY=$(openssl rand -hex 32)" > .env
```

Each agent derives its own auth key via `HMAC(master_key, agent_id)`, enabling per-agent revocation without rotating the master key.

### Token Vending

The orchestrator generates short-lived GitHub installation tokens for agents on demand. Private keys never leave the orchestrator container — agents receive time-limited tokens for API calls.

### Per-Process Environment Variables

Secrets and environment variables injected for a pipeline run (via setup steps or project secrets) are scoped to the **child agent process only**. They are set on `ProcessStartInfo.Environment` before the process launches and do not affect the orchestrator process, any other running agent, or any subsequently spawned processes outside that child. This means there is no risk of secret leakage between concurrent runs or into the orchestrator's own environment.

---

## Helm Chart (Kubernetes)

For Kubernetes deployments, a Helm chart is provided at `helm/coding-agent-automation/`.

### Prerequisites

- kubectl ≥ 1.25
- Helm ≥ 3.12
- A running PostgreSQL instance accessible from the cluster
- (Optional) Redis for multi-replica SignalR backplane

### Install

```bash
# 1. Install the chart
helm install coding-agent ./helm/coding-agent-automation \
  --set secrets.agentApiKey="$(openssl rand -hex 32)" \
  --set database.host=<postgres-host> \
  --set database.auth.existingSecret=<k8s-secret-name> \
  --set api.enabled=true \
  --set jobController.enabled=true
```

### Architecture

The chart deploys:
- **1 Orchestrator Deployment** — Blazor Server app (`CodingAgentWebUI`). Connects to the API for all data access; no direct database connection.
- **1 Pipeline API Deployment** — `CodingAgentWebUI.Api`. Authoritative database owner, agent hub, and config/run-history server.
- **1 Job Controller Deployment** — `CodingAgentWebUI.JobController`. Claims WorkItems and dispatches K8s Jobs. Leader-elected.
- **1 Scheduler Deployment** — `CodingAgentWebUI.Scheduler`. Owns all periodic background work (orphaned label recovery, housekeeping, metrics polling). No direct Postgres connection. Leader-elected.
- **No persistent agent Deployments** — All agents are ephemeral K8s Jobs dispatched on demand by the Job Controller.

### Key values.yaml Settings

| Path | Description |
|------|-------------|
| `orchestrator.image.repository/tag` | Orchestrator container image |
| `jobTemplates[]` | List of K8s Job templates defining pod specs per label set. Each entry controls which image, resources, securityContext, initContainers, and `maxConcurrent` to use when dispatching work-item pods. |
| `secrets.agentApiKey` | HMAC master key for agent auth |
| `secrets.otelHeaders` | OTLP auth headers |
| `secrets.opencodeConfigContent` | OpenCode config JSON (mounted as file for opencode agents) |
| `existingSecret` | Use a pre-existing K8s Secret instead of chart-managed one |
| `otel.endpoint` | OTLP collector endpoint |
| `orchestrator.ingress.enabled` | Enable Ingress for external access |
| `database.host` | PostgreSQL hostname (required) |
| `database.port` | PostgreSQL port (default: `5432`) |
| `database.auth.existingSecret` | K8s Secret containing database credentials (keys: `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`) |
| `database.migrateOnStartup` | Apply EF Core migrations on Pipeline API startup (default: `true`). Set `false` only for blue/green deployments where you apply migrations manually via `kubectl exec` into the API pod before cutover. The Orchestrator always performs a fast-fail schema check and will refuse to start against an unmigrated schema. |
| `database.sslMode` | Npgsql SSL mode: `Disable`, `Prefer`, `Require`, `VerifyCA`, `VerifyFull`. Defaults to `Require` in production if not set. Use `Disable` for in-cluster Postgres without TLS. |
| `workDistribution.dispatch.intervalSeconds` | Seconds between dispatch cycles (default: `10`) |
| `workDistribution.dispatch.rateLimitPerSecond` | Max dispatches per second (default: `10`) |
| `workDistribution.dispatch.chatSessionMaxDurationSeconds` | Max lifetime (seconds) of a chat pod K8s Job (default: `7200`). Sets `activeDeadlineSeconds`. |
| `workDistribution.dispatch.chatPodConnectTimeoutSeconds` | Max seconds to wait for a chat pod to connect to the hub after Job creation (default: `120`). |
| `workDistribution.dispatch.chatTerminationGracePeriodSeconds` | `terminationGracePeriodSeconds` on chat pod spec (default: `120`). |
| `workDistribution.reconciliation.intervalSeconds` | Seconds between reconciliation cycles (default: `30`) |
| `workDistribution.reconciliation.staleRetentionDays` | Days to retain stale work items before cleanup (default: `7`) |
| `credentialPools.kiro` | List of PVC names for Kiro agent credential data. PVCs **must** use `ReadWriteOnce` or `ReadWriteOncePod` to prevent concurrent access from multiple agent Jobs. `DispatchService` claims one PVC per Job at dispatch time. |
| `signalr.redis.enabled` | Documents intent to enable Redis backplane (default: `false`). Note: the Helm templates only check `signalr.redis.connectionString` — setting `enabled: true` without a non-empty `connectionString` has no effect. To activate the backplane, set `signalr.redis.connectionString` to a non-empty value. |
| `signalr.redis.connectionString` | Redis connection string (deploy Redis independently) |
| `monitoring.prometheusRules.enabled` | Create PrometheusRule resources for alerting (requires Prometheus Operator) |

### Defining Agent Pod Templates

All agent pod specs are defined in `jobTemplates[]`. Each entry produces a K8s Job spec rendered into a ConfigMap consumed by `DispatchService`. `maxConcurrent` controls parallelism per label set:

```yaml
jobTemplates:
  - labels: "kiro,dotnet,dotnet10"
    image: "chemsorly/coding-agent:kiro-dotnet10"
    providerType: kiro
    maxConcurrent: 3
    resources:
      requests:
        cpu: "100m"
        memory: "256Mi"
      limits:
        cpu: "4"
        memory: "8Gi"
    podSecurityContext:
      runAsUser: 1000
      runAsGroup: 1000
      fsGroup: 1000
    nodeSelector:
      kubernetes.io/hostname: k8s-worker-1
    initContainers:
      - name: fix-perms
        image: busybox:latest
        command: ["sh", "-c", "chown -R 1000:1000 /home/ubuntu/.local/share/kiro-cli"]
    tolerations:
      - key: agents
        operator: Exists
        effect: NoSchedule
```

### Leader Election in Non-Kubernetes (SignalR) Mode

When deployed without Kubernetes (e.g., local dev or SignalR-only mode), `ILeaderElectionService` is not registered in the DI container. `PipelineLoopService` null-checks the leader gate: when the gate is `null`, the loop runs unconditionally without any leadership check. This is the intended behavior — non-K8s deployments are typically single-instance and need no leader gate.

The previous `AlwaysLeaderElectionService` stub (an explicit always-leader no-op) was removed in the arch-audit; the null-check achieves the same behavior without polluting the DI container.

> **Note on `AgentReservationService` and Redis:** When Redis is configured (`signalr.redis.connectionString` is set), `AgentReservationService` switches to distributed per-agent Redis locks (`lock:agent:{id}`, 5-second TTL) instead of the in-process `_selectionLock`. This enables safe agent selection across multiple API replicas. The in-process lock is used only when Redis is absent (local dev or single-replica deployments).

### Graceful Shutdown

The chart supports zero-downtime rolling updates:
- Orchestrator uses `readinessDrainDelaySeconds` (default: 15s) to stop accepting traffic before terminating
- `pipelineLoopStartupDelaySeconds` (default: **0**, range: 0–300) delays `PipelineLoopService` startup after the process is ready. The Helm default is 0 — the API now owns `IOrchestratorRunService` and rehydrates on its own startup, so the Orchestrator no longer needs a startup delay before dispatching (Spec 044)
- Let in-flight agent Jobs finish before upgrading — no drain hook exists

### Leader Election

Dispatch and reconciliation are leader-elected across the system. Each process has its own `LeaderElectionService` instance with a distinct Kubernetes Lease, preventing duplicate dispatches and conflicting reconciliation across replicas.

#### How It Works

`LeaderElectionService` is a singleton `IHostedService` that performs Lease-based leader election using the `k8s.LeaderElection` library. It exposes:

- **`IsLeader`** — `true` when this instance holds the lease
- **`LeaderToken`** — a `CancellationToken` that is cancelled when leadership is lost, enabling dependent services to stop immediately

#### Leader-Dependent Services

Two independent leases are used — one per process:

**Job Controller** (`caa-{release}-dispatch-lock` lease):

| Service | Behavior When Leader | Behavior When Non-Leader |
|---------|---------------------|--------------------------|
| `DispatchService` | Polls for pending WorkItems and dispatches K8s Jobs | Waits (linked `LeaderToken` is cancelled, re-checks on leadership change) |
| `ReconciliationService` | Runs startup reconciliation, watches K8s Jobs, enforces timeouts | Waits (linked `LeaderToken` is cancelled, re-checks on leadership change) |

**Pipeline API** (`caa-{release}-api-lock` lease):

| Service | Behavior When Leader | Behavior When Non-Leader |
|---------|---------------------|--------------------------|
| `ConsolidationWorkItemDispatchService` | Dispatches consolidation K8s Jobs | Waits |
| `DatabaseMaintenanceService` | Runs retention sweep | Waits |

**Orchestrator** (`caa-{release}-pipeline-loop-lock` lease):

| Service | Behavior When Leader | Behavior When Non-Leader |
|---------|---------------------|--------------------------|
| `PipelineLoopService` | Dispatches pipeline runs | Pauses (leader gate blocks loop entry) |

**Scheduler** (`caa-{release}-scheduler-lock` lease):

| Service | Behavior When Leader | Behavior When Non-Leader |
|---------|---------------------|--------------------------|
| `OrphanedLabelRecoveryService` | Sweeps for issues with stale `agent:in-progress` labels | Waits |
| `HousekeepingService` | Manages `agent:done` PRs, branch updates, and stale branch cleanup | Waits |
| `WorkItemCountsPoller` | Emits work-item count metrics to `CodingAgent.WorkDistribution` | Waits |

#### Configuration

Bound from the `LeaderElection` configuration section:

| Setting | Default | Description |
|---------|---------|-------------|
| `LeaseName` | `caa-leader` | Base name of the Kubernetes Lease resource. Overridden per-process by Helm (see above) |
| `Namespace` | *(auto-detected)* | Namespace for the Lease. Auto-reads from `POD_NAMESPACE` env var or mounted service account namespace file |
| `LeaseDuration` | 15s | Duration non-leaders wait before attempting acquisition |
| `RenewDeadline` | 10s | Deadline for the leader to renew before the lease expires. Must be less than `LeaseDuration` |
| `RetryPeriod` | 2s | Interval between acquisition/renewal attempts |
| `Identity` | *(auto-detected)* | Pod identity. Auto-reads from `POD_NAME` → `HOSTNAME` → `MachineName` |
| `FailOnNonKubernetesEnvironment` | false | If true, startup fails outside K8s. If false, logs a warning and remains non-leader (graceful degradation for local dev) |

Helm sets the lease name via `jobController.leaderElection.dispatchLeaseName` (Job Controller, defaults to `caa-{release}-dispatch-lock`), `orchestrator.leaderElection.pipelineLoopLeaseName` (Orchestrator, defaults to `caa-{release}-pipeline-loop-lock`), `scheduler.leaderElection.leaseName` (Scheduler, defaults to `caa-{release}-scheduler-lock`), and the API lease (`caa-{release}-api-lock`) which is hardcoded in the API Helm template with no `values.yaml` override. The Orchestrator and API leases use different names to prevent competition.

#### RBAC Requirements

The Helm chart creates ServiceAccounts and ClusterRoleBindings (or RoleBindings) automatically for each process.

**Orchestrator** (`CodingAgentWebUI`) ServiceAccount:

```yaml
rules:
  - apiGroups: ["coordination.k8s.io"]
    resources: ["leases"]
    verbs: ["create", "get", "update"]
```

The Orchestrator only needs leader-election Lease access. It has no direct K8s Job dispatch — `ChatJobDispatcher` and work-item dispatch both run in the Pipeline API and Job Controller respectively.

**Pipeline API** (`CodingAgentWebUI.Api`) ServiceAccount:

```yaml
rules:
  - apiGroups: ["batch"]
    resources: ["jobs"]
    verbs: ["create", "get", "list", "watch", "delete"]
  - apiGroups: ["coordination.k8s.io"]
    resources: ["leases"]
    verbs: ["get", "create", "update"]
  - apiGroups: [""]
    resources: ["secrets"]
    verbs: ["create", "delete"]   # per-Job derived-key Secrets (GC'd via ownerReference)
  - apiGroups: [""]
    resources: ["pods", "configmaps"]
    verbs: ["get", "list"]
```

**Job Controller** (`CodingAgentWebUI.JobController`) ServiceAccount:

```yaml
rules:
  - apiGroups: ["batch"]
    resources: ["jobs"]
    verbs: ["create", "get", "list", "watch", "delete"]
  - apiGroups: ["coordination.k8s.io"]
    resources: ["leases"]
    verbs: ["create", "get", "update"]
  - apiGroups: [""]
    resources: ["pods"]
    verbs: ["get", "list"]
  - apiGroups: [""]
    resources: ["secrets"]
    verbs: ["create", "delete"]   # per-Job derived-key Secrets (GC'd via ownerReference)
```

### Credential Pool Initialization

Kiro agents require CLI authentication tokens stored on persistent volumes. In Kubernetes mode, `DispatchService` claims a PVC from the credential pool for each spawned Job pod, mounting it at `/home/ubuntu/.local/share/kiro-cli`. Before the first dispatch, each PVC must contain valid tokens.

PVCs **must** use `ReadWriteOnce` or `ReadWriteOncePod` to prevent concurrent access from multiple agent Jobs.

#### One-Time Setup Per PVC

**1. Create a temporary auth pod mounting the target PVC:**

```bash
kubectl run kiro-auth-1 -n coding-agent \
  --image=chemsorly/coding-agent:coding-agent-kiro-dotnet10-latest \
  --restart=Never \
  --overrides='{
    "spec": {
      "nodeSelector": {"kubernetes.io/hostname": "YOUR-NODE"},
      "securityContext": {"runAsUser": 1000, "fsGroup": 1000},
      "containers": [{
        "name": "kiro-auth-1",
        "image": "chemsorly/coding-agent:coding-agent-kiro-dotnet10-latest",
        "command": ["sleep", "3600"],
        "volumeMounts": [{"name": "creds", "mountPath": "/home/ubuntu/.local/share/kiro-cli"}]
      }],
      "volumes": [{"name": "creds", "persistentVolumeClaim": {"claimName": "kiro-creds-pvc-1"}}]
    }
  }'
```

Replace `YOUR-NODE` with the node hosting the PVC's underlying storage (required for hostPath-backed PVs with node affinity).

**2. Exec into the pod and authenticate:**

```bash
kubectl exec -it kiro-auth-1 -n coding-agent -- kiro-cli login
```

Follow the device code URL printed to the terminal — open it in a browser and complete the OAuth flow.

**3. Delete the auth pod:**

```bash
kubectl delete pod kiro-auth-1 -n coding-agent
```

**4. Repeat for each PVC** in the pool (`kiro-creds-pvc-2`, `kiro-creds-pvc-3`, etc.).

#### Token Lifecycle

- Tokens include a refresh token with long expiry (weeks to months depending on the identity provider)
- Regular pipeline runs keep the refresh token active automatically
- If a PVC's token expires, re-run the auth pod workflow for that PVC
- Token validity can be verified: `kubectl exec ... -- kiro-cli auth status`

#### Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| Job pod fails immediately with auth error | PVC has no tokens or tokens expired | Re-run auth pod workflow |
| Job pod hangs during CLI startup | Token refresh failing (network/IdP issue) | Check pod logs, verify IdP connectivity |
| DispatchService logs "no PVC available" | All PVCs claimed by running Jobs | Wait for Jobs to complete, or add more PVCs to the pool |
| Auth pod can't mount PVC | PVC bound to a different node | Ensure nodeSelector matches the PV's node affinity |

---

## Local Development

For local development, use [Rancher Desktop](https://rancherdesktop.io/) or [Docker Desktop](https://www.docker.com/products/docker-desktop/) with Kubernetes enabled.

Set up a `~/.kube/config` pointing at your local cluster. The Kubernetes client uses in-cluster config when running inside a pod and falls back to `~/.kube/config` when running locally (`dotnet run`).

```bash
# Run the orchestrator locally against your local cluster
dotnet run --project src/CodingAgentWebUI/
```

> `Database__Host` must be set — the orchestrator requires PostgreSQL on startup.

For the agent project (work-item mode, connecting to the Pipeline API hub on port 8090):
```bash
ORCHESTRATOR_URL=http://localhost:8090 AGENT_ID=local-agent-1 AGENT_API_KEY=<key> \
  dotnet run --project src/CodingAgentWebUI.Agent/ -- --mode=workitem --work-item-id=<guid>
```

For chat mode (no `--work-item-id`):
```bash
ORCHESTRATOR_URL=http://localhost:8090 AGENT_ID=local-chat-1 AGENT_API_KEY=<key> \
  dotnet run --project src/CodingAgentWebUI.Agent/ -- --mode=chat
```

`ORCHESTRATOR_URL`, `AGENT_ID`, and `AGENT_API_KEY` are environment variables, not CLI arguments. `--mode` (workitem or chat) is required.

---

## Provider Configuration

The pipeline supports multiple provider backends. Each provider type requires specific settings.

### GitHub

```json
{
  "providerType": "GitHub",
  "settings": {
    "owner": "my-org",
    "repo": "my-repo",
    "appId": "123456",
    "privateKeyBase64": "base64-encoded-pem-key",
    "installationId": "78901234"
  }
}
```

### GitLab

```json
{
  "providerType": "GitLab",
  "settings": {
    "apiUrl": "https://gitlab.com",
    "accessToken": "glpat-xxxxxxxxxxxxxxxxxxxx",
    "projectId": "12345",
    "baseBranch": "main"
  }
}
```
