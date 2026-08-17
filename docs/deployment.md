# Deployment

<!-- TODO(Spec 045): rewrite for the API / JobController / agent-pod topology. -->

## Architecture

The application runs on Kubernetes. The orchestrator is a Blazor Server application managing all
pipeline orchestration, the SignalR hub, and the web UI. Agent pods are ephemeral Kubernetes Jobs
spawned on demand.

<!-- TODO(Spec 045): rewrite the Architecture section for the API / JobController / agent-pod topology. -->

- **WebUI** (`CodingAgentWebUI`) — Blazor Server app. Hosts the web UI, SignalR hub, API endpoints, background services (OrphanedLabelRecovery, LoopStatePersistence, LeaderElection).
- **Orchestration** (`CodingAgentWebUI.Orchestration`) — Dispatch logic (`DispatchService`, `ReconciliationService`, `KubernetesWorkDistributor`), agent registry, run lifecycle management, leader election, telemetry.
- **Infrastructure** (`CodingAgentWebUI.Infrastructure`) — Provider implementations (GitHub, filesystem), config store, resilience pipelines, token vending.
- **Pipeline** (`CodingAgentWebUI.Pipeline`) — Core pipeline orchestration (`PipelineOrchestrationService`, facades, `PipelineLoopService`), step execution, models, interfaces, constants.
- **Agent Host** (`CodingAgentWebUI.Agent`) — Agent executable. Manages SignalR/HTTP connection to orchestrator, work item lifecycle, health endpoints, heartbeat, and reconnection logic. Two execution modes: _work-item pods_ (spawned with `--work-item-id`) and _chat pods_ (spawned without, for interactive chat sessions).

## Authentication

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
# TODO(Spec 045): finalize --set values once api.* and jobController.* are added in Specs 042–043.
helm install coding-agent ./helm/coding-agent-automation \
  --set secrets.agentApiKey="$(openssl rand -hex 32)" \
  --set database.host=postgres.coding-agent.svc.cluster.local \
  --set database.auth.existingSecret=postgres-secret \
  --set orchestrator.image.tag=coding-agent-webui
```

### Architecture

The chart deploys:
- **1 Orchestrator Deployment** — Blazor Server app with pipeline orchestration
- **No persistent agent Deployments** — All agents are ephemeral K8s Jobs dispatched on demand by `DispatchService`

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
| `database.migrateOnStartup` | Apply EF Core migrations on orchestrator startup (default: `true`). Safe for single-replica; keep one orchestrator replica during rolling upgrades to avoid concurrent migration races (EF Core #13569). Set `false` only if running migrations externally via `kubectl exec` — startup aborts if pending migrations are detected. |
| `database.sslMode` | Npgsql SSL mode: `Disable`, `Prefer`, `Require`, `VerifyCA`, `VerifyFull`. Defaults to `Require` in production if not set. Use `Disable` for in-cluster Postgres without TLS. |
| `workDistribution.dispatch.intervalSeconds` | Seconds between dispatch cycles (default: `10`) |
| `workDistribution.dispatch.rateLimitPerSecond` | Max dispatches per second (default: `10`) |
| `workDistribution.reconciliation.intervalSeconds` | Seconds between reconciliation cycles (default: `30`) |
| `workDistribution.reconciliation.timeoutEnforcementEnabled` | Whether to enforce agent timeouts via reconciliation (default: `true`) |
| `workDistribution.reconciliation.staleRetentionDays` | Days to retain stale work items before cleanup (default: `7`) |
| `credentialPools.kiro` | List of PVC names for Kiro agent credential data. PVCs **must** use `ReadWriteOnce` or `ReadWriteOncePod` to prevent concurrent access from multiple agent Jobs. `DispatchService` claims one PVC per Job at dispatch time. |
| `signalr.redis.enabled` | Enable Redis backplane for multi-replica orchestrator SignalR (default: `false`) |
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

### Graceful Shutdown

The chart supports zero-downtime rolling updates:
- Orchestrator uses `readinessDrainDelaySeconds` (default: 15s) to stop accepting traffic before terminating
- `pipelineLoopStartupDelaySeconds` (Helm default: 30s, application default: 90s) prevents dispatching to agents that are mid-termination — must be greater than agent `terminationGracePeriodSeconds`
- Let in-flight agent Jobs finish before upgrading — no drain hook exists

### Leader Election

In multi-replica deployments, the orchestrator uses Kubernetes Lease-based leader election to ensure only one replica runs leader-dependent services. This prevents duplicate dispatches and conflicting reconciliation actions.

#### How It Works

`LeaderElectionService` is a singleton `IHostedService` that performs Lease-based leader election using the `k8s.LeaderElection` library. It exposes:

- **`IsLeader`** — `true` when this instance holds the lease
- **`LeaderToken`** — a `CancellationToken` that is cancelled when leadership is lost, enabling dependent services to stop immediately

#### Leader-Dependent Services

| Service | Behavior When Leader | Behavior When Non-Leader |
|---------|---------------------|--------------------------|
| `DispatchService` | Polls for pending WorkItems and dispatches K8s Jobs | Waits (linked `LeaderToken` is cancelled, re-checks on leadership change) |
| `ReconciliationService` | Runs startup reconciliation, watches K8s Jobs, enforces timeouts | Waits (linked `LeaderToken` is cancelled, re-checks on leadership change) |

#### Configuration

Bound from the `LeaderElection` configuration section:

| Setting | Default | Description |
|---------|---------|-------------|
| `LeaseName` | `caa-leader` | Name of the Kubernetes Lease resource |
| `Namespace` | *(auto-detected)* | Namespace for the Lease. Auto-reads from `POD_NAMESPACE` env var or mounted service account namespace file |
| `LeaseDuration` | 15s | Duration non-leaders wait before attempting acquisition |
| `RenewDeadline` | 10s | Deadline for the leader to renew before the lease expires. Must be less than `LeaseDuration` |
| `RetryPeriod` | 2s | Interval between acquisition/renewal attempts |
| `Identity` | *(auto-detected)* | Pod identity. Auto-reads from `POD_NAME` → `HOSTNAME` → `MachineName` |
| `FailOnNonKubernetesEnvironment` | false | If true, startup fails outside K8s. If false, logs a warning and remains non-leader (graceful degradation for local dev) |

#### RBAC Requirements

The orchestrator ServiceAccount requires Lease permissions plus Job creation rights. The Helm chart creates these automatically:

```yaml
rules:
  - apiGroups: ["coordination.k8s.io"]
    resources: ["leases"]
    verbs: ["create", "get", "update"]
  - apiGroups: ["batch"]
    resources: ["jobs"]
    verbs: ["create", "get", "list", "watch", "delete"]
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

For the agent project:
```bash
dotnet run --project src/CodingAgentWebUI.Agent/ -- --orchestrator-url http://localhost:8080 --agent-id local-agent-1
```

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
