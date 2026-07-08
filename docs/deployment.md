# Deployment

## Architecture

The application follows Clean Architecture with a multi-container deployment:

```mermaid
graph TB
    Browser[Web Browser]
    
    subgraph Orchestrator["Orchestrator Container"]
        WebUI[WebUI<br/>Blazor Server + Pages]
        Orch[Orchestration<br/>dispatch, tracking, leader election]
        Infra[Infrastructure<br/>providers, config store, resilience]
        Pipeline[Pipeline<br/>orchestration logic, services, models]
        CodeReview[Pipeline.CodeReview<br/>review findings, inline comments]
        KiroLib[KiroCliLib<br/>CLI config parsing, shared utilities]
    end
    
    subgraph Agents["Agent Containers"]
        AgentHost[Agent Host<br/>connection, lifecycle, health]
        AgentKiro[Agent.KiroCli<br/>Kiro CLI process provider]
        AgentOC[Agent.OpenCode<br/>OpenCode HTTP provider]
        A1[Kiro Agent .NET ×2]
        A2[Kiro Agent Python]
        A3[Kiro Agent Java]
        A4[OpenCode Agent .NET]
        A5[OpenCode Agent Python]
        A6[OpenCode Agent Java]
    end
    
    Browser -->|HTTP/SignalR| WebUI
    WebUI --> Orch
    Orch --> Infra
    Orch --> Pipeline
    Infra --> Pipeline
    Pipeline --> CodeReview
    Pipeline --> KiroLib
    Orch -->|SignalR / K8s Jobs<br/>work distribution| AgentHost
    AgentHost --> AgentKiro
    AgentHost --> AgentOC
    AgentKiro --> KiroLib
```

- **WebUI** (`CodingAgentWebUI`) — Blazor Server app. Hosts the web UI, SignalR hub, API endpoints, background services (OrphanedLabelRecovery, LoopStatePersistence).
- **Orchestration** (`CodingAgentWebUI.Orchestration`) — Dispatch logic (`DispatchService`, `ReconciliationService`, `SignalRWorkDistributor`, `KubernetesWorkDistributor`), agent registry, run lifecycle management, leader election, telemetry.
- **Infrastructure** (`CodingAgentWebUI.Infrastructure`) — Provider implementations (GitHub, filesystem), config store, resilience pipelines, token vending.
- **Pipeline** (`CodingAgentWebUI.Pipeline`) — Core pipeline orchestration (`PipelineOrchestrationService`, facades, `PipelineLoopService`), step execution, models, interfaces, constants.
- **Pipeline.CodeReview** (`CodingAgentWebUI.Pipeline.CodeReview`) — Review findings parsing, inline comment selection, severity filtering.
- **KiroCliLib** — Shared library for Kiro CLI configuration parsing. Referenced by Pipeline, Agent.KiroCli, and Agent.OpenCode.
- **Agent Host** (`CodingAgentWebUI.Agent`) — Agent executable. Manages SignalR/HTTP connection to orchestrator, work item lifecycle, health endpoints, heartbeat, and reconnection logic.
- **Agent.KiroCli** (`CodingAgentWebUI.Agent.KiroCli`) — Kiro CLI agent provider. Spawns kiro-cli processes and translates pipeline operations into CLI invocations.
- **Agent.OpenCode** (`CodingAgentWebUI.Agent.OpenCode`) — OpenCode agent provider. Communicates with the OpenCode HTTP API for LLM-driven code generation.
- **Agent Containers** — Worker containers connecting via SignalR or HTTP. Two backends: Kiro CLI (process) and OpenCode (HTTP API). Scale by adding containers.

## Docker Compose

The `docker-compose.yml` defines 8 services: 1 orchestrator + 2 Kiro .NET agents + 1 Kiro Python agent + 1 Kiro Java agent + 1 OpenCode .NET agent + 1 OpenCode Python agent + 1 OpenCode Java agent.

To add more agents, copy a service definition with a new name and volume — don't use `--scale` (each agent needs its own named volume to avoid SQLite corruption).

### DB+SignalR Mode (Postgres)

For persistent state across container restarts, use the Postgres overlay:

```bash
docker compose -f docker-compose.yml -f docker-compose.postgres.yml up --build
```

This adds PostgreSQL (persistence) and Redis (SignalR backplane). The orchestrator stores all configuration, work items, and run history in Postgres instead of JSON files. The dispatch flow uses `SignalRWorkDistributor`: work items are tracked in DB rows while job delivery uses SignalR push to connected agents.

After first start, import your pipeline configuration via **Settings → Data Management** using a JSON bundle file.

**Key differences from JSON-file mode:**
- Configuration stored in Postgres (tables: `ProviderConfigs`, `AgentProfiles`, `QualityGateConfigs`, `PipelineJobTemplates`, `Projects`)
- Work items tracked in `WorkItems` table with lifecycle transitions (Pending → Dispatched → Running → Succeeded/Failed/Cancelled)
- Active runs enriched from in-memory state for real-time UI updates (step transitions, output streaming)
- Config changes take effect immediately (cache invalidated on import)

**Environment variables (Postgres overlay):**

| Variable | Description |
|----------|-------------|
| `Database__Host` | PostgreSQL hostname |
| `Database__Port` | PostgreSQL port (default: `5432`) |
| `Database__Username` | PostgreSQL username |
| `Database__Password` | PostgreSQL password |
| `Database__Name` | PostgreSQL database name |
| `Database__SslMode` | Npgsql SSL mode: `Disable`, `Prefer`, `Require`, `VerifyCA`, `VerifyFull` (default: `Disable` in local overlay) |
| `Database__MigrateOnStartup` | Apply EF Core migrations on startup (default: `true`) |
| `SignalR__Redis__ConnectionString` | Redis connection string for SignalR backplane (optional for single instance) |

## Volume Mounts

### Orchestrator

| Mount | Container Path | Purpose |
|-------|---------------|---------|
| Pipeline config | `/app/config/pipeline` | Provider configs, quality gates, profiles, run history (persists across restarts) |

### Agent Containers (Kiro CLI)

| Mount | Container Path | Purpose |
|-------|---------------|---------|
| Agent CLI auth | `/home/ubuntu/.local/share/kiro-cli` | Agent CLI login tokens |

### Agent Containers (OpenCode)

OpenCode agents have no volume mounts in Docker Compose — they receive configuration via the `OPENCODE_CONFIG_CONTENT` environment variable injected at startup. In Kubernetes (Helm), a read-only Secret-backed volume is mounted at `/app/config/opencode` containing the OpenCode configuration file.

Each agent container needs its own CLI data volume to avoid SQLite corruption from concurrent access. Workspaces are created inside the container at `/app/workspaces/` — no volume mount needed.

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

## Authentication

### Agent API Keys

The orchestrator and agents authenticate using HMAC-derived keys. Set a shared master secret:

```bash
echo "AGENT_API_KEY=$(openssl rand -hex 32)" > .env
```

Each agent derives its own auth key via `HMAC(master_key, agent_id)`, enabling per-agent revocation without rotating the master key.

### Token Vending

The orchestrator generates short-lived GitHub installation tokens for agents on demand. Private keys never leave the orchestrator container — agents receive time-limited tokens for API calls.

---

## Helm Chart (Kubernetes)

For Kubernetes deployments, a Helm chart is provided at `helm/coding-agent-automation/`.

### Install

```bash
helm install coding-agent ./helm/coding-agent-automation \
  --set secrets.agentApiKey="$(openssl rand -hex 32)" \
  --set orchestrator.image.tag=coding-agent-webui \
  --set otel.endpoint=http://otel-collector:4317
```

### Architecture

The chart deploys:
- **1 Orchestrator Deployment** — Blazor Server app with pipeline orchestration
- **N Agent Deployments** — One Deployment per agent entry in `values.yaml` (each gets its own PVC for CLI auth data)

### Key values.yaml Settings

| Path | Description |
|------|-------------|
| `orchestrator.image.repository/tag` | Orchestrator container image |
| `orchestrator.persistence.type` | Storage backend: `pvc` (default), `hostPath`, or `emptyDir` |
| `orchestrator.persistence.mountMode` | Config volume mount mode: `readWrite` (default), `readOnly` (migration source when database enabled), `disabled` (after migration complete) |
| `agents[]` | List of agent definitions for SignalR mode (name, image, labels, providerType). Creates Deployments. |
| `agents[].providerType` | `kiro` or `opencode` — determines volume mount profile |
| `agents[].labels` | Comma-separated routing labels (e.g., `kiro,dotnet,dotnet10`) |
| `jobTemplates[]` | List of K8s Job templates for Kubernetes mode. Defines pod spec per label set. Falls back to `agents[]` if empty. |
| `secrets.agentApiKey` | HMAC master key for agent auth |
| `secrets.otelHeaders` | OTLP auth headers |
| `secrets.opencodeConfigContent` | OpenCode config JSON (mounted as file for opencode agents) |
| `existingSecret` | Use a pre-existing K8s Secret instead of chart-managed one |
| `otel.endpoint` | OTLP collector endpoint |
| `orchestrator.ingress.enabled` | Enable Ingress for external access |
| `database.enabled` | Enable PostgreSQL-backed persistence (default: `false`, uses JSON file store when disabled) |
| `database.host` | PostgreSQL hostname (required when database enabled) |
| `database.port` | PostgreSQL port (default: `5432`) |
| `database.auth.existingSecret` | K8s Secret containing database credentials (keys: `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`) |
| `database.migrateOnStartup` | Apply EF Core migrations on orchestrator startup (default: `true`) |
| `database.sslMode` | Npgsql SSL mode: `Disable`, `Prefer`, `Require`, `VerifyCA`, `VerifyFull`. Defaults to `Require` in production if not set. Use `Disable` for in-cluster Postgres without TLS |
| `workDistribution.mode` | Dispatch mode: `SignalR` (default, docker-compose compatible) or `Kubernetes` (K8s Job dispatch) |
| `workDistribution.dispatch.intervalSeconds` | Seconds between dispatch cycles in Kubernetes mode (default: `10`) |
| `workDistribution.dispatch.rateLimitPerSecond` | Max dispatches per second in Kubernetes mode (default: `10`) |
| `workDistribution.reconciliation.intervalSeconds` | Seconds between reconciliation cycles in Kubernetes mode (default: `30`) |
| `workDistribution.reconciliation.timeoutEnforcementEnabled` | Whether to enforce agent timeouts via reconciliation (default: `true`) |
| `workDistribution.reconciliation.staleRetentionDays` | Days to retain stale work items before cleanup (default: `7`) |
| `credentialPools.kiro` | List of PVC names for kiro agent credential data (DispatchService claims one per Job) |
| `signalr.redis.enabled` | Enable Redis backplane for multi-replica orchestrator SignalR (default: `false`) |
| `signalr.redis.connectionString` | Redis connection string (deploy Redis independently) |
| `monitoring.prometheusRules.enabled` | Create PrometheusRule resources for alerting (requires Prometheus Operator) |

In Kubernetes mode, Job pod specs (image, imagePullPolicy, resources, nodeSelector, tolerations, initContainers, podSecurityContext, maxConcurrent) are configured in the `jobTemplates[]` list and rendered into a ConfigMap consumed by `DispatchService`. If `jobTemplates` is empty, the chart falls back to deriving templates from the `agents[]` list for backward compatibility.

### Scaling Agents

**SignalR mode** — add entries to `agents[]`. Each entry produces a separate Deployment with dedicated storage:

```yaml
agents:
  - name: agent-kiro-dotnet-1
    enabled: true
    image:
      repository: chemsorly/coding-agent
      tag: coding-agent-kiro-dotnet10
    providerType: kiro
    labels: "kiro,dotnet,dotnet10"
  - name: agent-kiro-dotnet-2
    enabled: true
    image:
      repository: chemsorly/coding-agent
      tag: coding-agent-kiro-dotnet10
    providerType: kiro
    labels: "kiro,dotnet,dotnet10"
```

**Kubernetes mode** — define `jobTemplates[]` with K8s Job-specific pod spec. `maxConcurrent` controls parallelism per label set:

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
- `pipelineLoopStartupDelaySeconds` (Helm default: 30s, application default: 90s) prevents dispatching to agents that are mid-termination — must be greater than agent `terminationGracePeriodSeconds`. The Helm value overrides the application's built-in default via the `PIPELINE_LOOP_STARTUP_DELAY_SECONDS` env var.
- Agent `terminationGracePeriodSeconds` defaults to 15s

### Leader Election (Kubernetes Mode)

In DB+Kubernetes mode, the orchestrator uses Kubernetes Lease-based leader election to ensure only one replica runs leader-dependent services. This prevents duplicate dispatches and conflicting reconciliation actions when running multiple orchestrator replicas.

#### How It Works

`LeaderElectionService` is a singleton `IHostedService` that performs Lease-based leader election using the `k8s.LeaderElection` library. It exposes:

- **`IsLeader`** — `true` when this instance holds the lease
- **`LeaderToken`** — a `CancellationToken` that is cancelled when leadership is lost, enabling dependent services to stop immediately

#### Leader-Dependent Services

| Service | Behavior When Leader | Behavior When Non-Leader |
|---------|---------------------|--------------------------|
| `DispatchService` | Polls for pending WorkItems and dispatches K8s Jobs | Waits (polls `IsLeader` every 2s) |
| `ReconciliationService` | Runs startup reconciliation, watches K8s Jobs, enforces timeouts | Waits (polls `IsLeader` every 2s) |

Both services create a linked `CancellationTokenSource` combining the host `stoppingToken` and `LeaderToken`. This ensures immediate stop on either graceful shutdown OR leadership loss — no stale work continues after failover.

#### Leadership Lifecycle

```
Replica starts → polls for Lease → acquires → IsLeader=true, LeaderToken valid
  → DispatchService + ReconciliationService enter work loops
  → ...leadership lost (Lease expires, network partition, pod preempt)...
  → LeaderToken cancelled → services exit work loops → re-enter wait state
  → re-acquires Lease → fresh LeaderToken → services resume
```

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

The orchestrator ServiceAccount needs Lease permissions. The Helm chart creates these automatically when `database.enabled=true` and `workDistribution.mode=Kubernetes`:

```yaml
rules:
  - apiGroups: ["coordination.k8s.io"]
    resources: ["leases"]
    verbs: ["create", "get", "update"]
```

#### Non-Kubernetes Environments

When `IKubernetes` client is not available (local dev, Docker Compose):
- `IsLeader` remains `false`
- `LeaderToken` starts cancelled
- Leader-dependent services (DispatchService, ReconciliationService) never enter their work loops
- This is correct for non-K8s environments where `PipelineLoopService` handles dispatch via the legacy or DB+SignalR path instead

### Credential Pool Initialization (Kubernetes Mode)

Kiro agents require CLI authentication tokens stored on persistent volumes. In Kubernetes mode, the `DispatchService` claims a PVC from the credential pool for each spawned Job pod, mounting it at `/home/ubuntu/.local/share/kiro-cli`. Before the first dispatch, each PVC must contain valid tokens.

#### One-Time Setup Per PVC

Each PVC in `credentialPools.kiro` must be authenticated once. Since agent Jobs are ephemeral (terminate on completion or failure), use a temporary long-running pod for the interactive login flow:

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
- Regular pipeline runs keep the refresh token active automatically — each Job mounts the PVC and the CLI refreshes the token as needed
- If a PVC's token expires (e.g., the pool was not used for an extended period), re-run the auth pod workflow for that PVC
- Token validity can be verified without running a full pipeline: `kubectl exec ... -- kiro-cli auth status`

#### Troubleshooting

| Symptom | Cause | Fix |
|---------|-------|-----|
| Job pod fails immediately with auth error | PVC has no tokens or tokens expired | Re-run auth pod workflow |
| Job pod hangs during CLI startup | Token refresh failing (network/IdP issue) | Check pod logs, verify IdP connectivity |
| DispatchService logs "no PVC available" | All PVCs claimed by running Jobs | Wait for Jobs to complete, or add more PVCs to the pool |
| Auth pod can't mount PVC | PVC bound to a different node | Ensure nodeSelector matches the PV's node affinity |
