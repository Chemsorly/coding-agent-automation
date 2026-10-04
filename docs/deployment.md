# Deployment

## Architecture

The application runs on Kubernetes as five distinct processes:

- **Orchestrator** (`CodingAgent.Web`) — Blazor Server app. Hosts the web UI. No direct database access — all config and run history read from the Pipeline API via HTTP. `IAgentHubConnection` (defined in `CodingAgent.Api.Client`, scoped per Blazor circuit) subscribes to the API hub for live run streaming.
- **Pipeline API** (`CodingAgent.Api`) — HTTP and SignalR hub server. Authoritative database owner (EF Core + Postgres). Hosts `AgentHub`, `AgentRegistryService`, `OrchestratorRunService`, `DatabaseMaintenanceService`, `ChatJobDispatcher`, `ChatSessionWatcher`, and `ChatHeartbeatTracker`.
- **Job Controller** (`CodingAgent.JobController`) — Kubernetes Job dispatch. Receives dispatch requests from `KubernetesWorkDistributor` (in the Pipeline API) and creates K8s Jobs atomically via `POST /api/work-items/dispatch`. Leader-elected via a `caa-{release}-dispatch-lock` Lease (name configured via `jobController.leaderElection.dispatchLeaseName`). Stateless between dispatches; all state lives in Postgres via the API.
- **Scheduler** (`CodingAgent.Scheduler`) — Owns all scheduled/periodic background work: orphaned label recovery, housekeeping, work-item metrics polling, and periodic maintenance sweeps. No direct Postgres connection — all persistence goes through the Pipeline API. Leader-elected via `caa-{release}-scheduler-lock` Lease.
- **Agent Host** (`CodingAgent.Agent`) — Ephemeral K8s Job pod. Connects to the Pipeline API hub using `AGENT_API_KEY` as a Bearer token. Picks up assignments via `GET /api/work-items/{id}/assignment`, reports progress and terminal status via hub methods and `POST /api/work-items/{id}/status`. Two execution modes: _work-item pods_ (started with `--mode=workitem --work-item-id=<guid>`) and _chat pods_ (started with `--mode=chat`). Both flags are required for work-item mode; `--mode` alone is sufficient for chat mode.

Supporting libraries (shared, not deployed independently):

- **Orchestration** (`CodingAgent.Orchestration`) — Dispatch logic, agent registry, run lifecycle, telemetry. Linked into the Pipeline API, Scheduler, and Orchestrator. References `Infrastructure.Providers` directly; does not reference `Infrastructure.Persistence`.
- **Infrastructure.Persistence** (`CodingAgent.Infrastructure.Persistence`) — EF Core context, database migrations, config store. Directly referenced by `CodingAgent.Api` and `CodingAgent.AgentGateway`. The Scheduler and Orchestrator have no direct or transitive reference to Persistence.
- **Infrastructure.Providers** (`CodingAgent.Infrastructure.Providers`) — Provider implementations (GitHub, GitLab, filesystem), token vending. Linked into the Pipeline API, Agent, Scheduler, Job Controller, and Orchestration.
- **Pipeline** (`CodingAgent.Pipeline`) — Core pipeline model, step execution, `PipelineLoopService`, `HousekeepingService`, `DispatchScheduler`, interfaces, constants. Linked into the Scheduler (which registers and runs these services), the Pipeline API, and the Orchestrator (for pipeline model types and loop-status polling).
- **Hub** (`CodingAgent.AgentGateway`) — Full hub implementation: `AgentHub` (split across 8 partial classes), authentication handlers (`AgentApiKeyAuthHandler`), chat pod dispatch and session management (`ChatJobDispatcher` — dispatch and lifecycle, `ChatSessionWatcher` — per-session idle-kill and watcher loop, `ChatHeartbeatTracker` — Redis cross-replica heartbeat storage), job lifecycle services (`AgentJobLifecycleService`, `AgentOrphanRecoveryService`, `AgentTokenRefreshService`), `AgentHubFacade`, and DI wiring. Linked into the Pipeline API and Orchestrator.

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

> **Upgrading to the release with sign-in (Spec 049):** every page of the web UI now requires a signed-in user. Serve the UI at the root of its own host (`web.ingress`, or `kubectl port-forward` for the local admin) before you upgrade; path-prefix proxies such as the Rancher service proxy stop working. Read the generated admin password as shown in [Authentication](authentication.md#local-admin-password), then configure OIDC and role bindings.

```bash
# 1. Install the chart
helm install coding-agent ./helm/coding-agent-automation \
  --set secrets.agentApiKey="$(openssl rand -hex 32)" \
  --set database.host=<postgres-host> \
  --set database.auth.existingSecret=<k8s-secret-name> \
  --set api.enabled=true \
  --set jobController.enabled=true
```

> **Docker build args:** All service images (`api.Dockerfile`, `web.Dockerfile`, `jobcontroller.Dockerfile`, `scheduler.Dockerfile`) accept a `BUILD_COMMIT_SHA` build arg at image build time. This arg is exposed as the `SERVICE_VERSION` runtime environment variable and is reported in `build-info.json` inside the container for version identification. Pass it via `--build-arg BUILD_COMMIT_SHA=$(git rev-parse HEAD)` in CI. Omitting it defaults to `"local"` (safe for dev builds).

### Verify Images

CI signs every image it pushes to `docker.io/chemsorly/coding-agent` with [cosign](https://github.com/sigstore/cosign) keyless signing: the multi-arch manifest list behind each `-<sha7>`, `-latest` and `-<version>` tag, its platform manifests, and the per-arch `-amd64`/`-arm64` tags. Pushes from `main` and from `vX.Y.Z` release tags are signed; pull requests do not push and are not signed. The manifest list is signed on its immutable `-<sha7>` tag before `-latest` and `-<version>` are moved to it, so those tags never point at an unsigned image. There is no signing key: the certificate is issued by Sigstore's Fulcio for the CI workflow's GitHub OIDC identity, and every signature is recorded in the public Rekor transparency log.

Verify an image before you deploy it (cosign v3 or later):

```bash
cosign verify \
  --certificate-identity-regexp '^https://github\.com/Chemsorly/coding-agent-automation/\.github/workflows/ci\.yml@refs/(heads/main|tags/v[0-9]+\.[0-9]+\.[0-9]+)$' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  --certificate-github-workflow-repository Chemsorly/coding-agent-automation \
  docker.io/chemsorly/coding-agent:coding-agent-web-latest
```

A tag is resolved to its digest at verification time, so verifying a mutable tag such as `-latest` checks the image the tag points at now. Signatures are stored as cosign v3 Sigstore bundles attached to the image digest as OCI 1.1 referrers, not as extra tags; cosign v2 without `--new-bundle-format` reports no signatures. The chart does not enforce signatures at admission. If you add a policy engine (for example Kyverno `verifyImages` or the Sigstore policy-controller), use the identity above and first confirm that your version verifies Sigstore bundles stored as OCI referrers.

### Architecture

The chart deploys:
- **1 Orchestrator Deployment** — Blazor Server app (`CodingAgent.Web`). Connects to the API for all data access; no direct database connection.
- **1 Pipeline API Deployment** — `CodingAgent.Api`. Authoritative database owner, agent hub, and config/run-history server.
- **1 Job Controller Deployment** — `CodingAgent.JobController`. Claims WorkItems and dispatches K8s Jobs. Leader-elected.
- **1 Scheduler Deployment** — `CodingAgent.Scheduler`. Owns all periodic background work (orphaned label recovery, housekeeping, metrics polling). No direct Postgres connection. Leader-elected.
- **No persistent agent Deployments** — All agents are ephemeral K8s Jobs dispatched on demand by the Job Controller.

### Key values.yaml Settings

| Path | Description |
|------|-------------|
| `web.image.repository/tag` | Web container image |
| `web.replicas` | Number of web replicas (default: `2`). Values > 1 require `signalr.redis.connectionString` to be set for correct chat keepalive behavior (see Redis note below), and sticky sessions at the ingress (see `web.service.annotations`). |
| `web.service.annotations` | Annotations on the web Service. With Traefik and more than one web replica, enable sticky sessions here (`traefik.ingress.kubernetes.io/service.sticky.cookie: "true"`): a Blazor Server circuit lives in one pod, and its connection and reconnects must reach that pod. See [Authentication](authentication.md#exposing-the-ui). |
| `api.replicas` | Number of Pipeline API replicas (default: `2`). Values > 1 require `signalr.redis.connectionString` to be set — the chart fails at render time otherwise, since without Redis in-memory state cannot be shared across replicas. |
| `jobTemplates[]` | List of K8s Job templates defining pod specs per label set. Each entry controls which image, resources, securityContext, initContainers, and `maxConcurrent` to use when dispatching work-item pods. |
| `secrets.agentApiKey` | HMAC master key for agent auth |
| `secrets.otelHeaders` | OTLP auth headers |
| `secrets.opencodeConfigContent` | OpenCode config JSON (mounted as file for opencode agents) |
| `secrets.claudeApiKey` | Anthropic API key for claude agents (Secret key `claude-api-key`; optional) |
| `secrets.claudeOauthToken` | Subscription token from `claude setup-token` for claude agents (Secret key `claude-oauth-token`; optional, one-year lifetime) |
| `existingSecret` | Use a pre-existing K8s Secret instead of chart-managed one. Optional keys `opencode-config-content`, `claude-api-key` and `claude-oauth-token` are read from it too (e.g. synced by external-secrets). |
| `auth.admin.enabled` / `auth.admin.existingSecret` | Local `admin` login (default: enabled). The admin password comes from `<release>-coding-agent-automation-admin`, generated by the secret-init hook, or from an existing Secret. See [Authentication](authentication.md). |
| `auth.oidc.*` | The OIDC identity provider: `enabled`, `name`, `issuer`, `clientId`, `clientSecret.existingSecret`/`key`, `scopes`, `usernameClaim`, `groupsClaim`. See [Authentication](authentication.md). |
| `auth.rbac.defaultRole` / `auth.rbac.bindings` | Roles (`readonly`, `operator`, `admin`) bound to OIDC groups or users, globally or per project. See [Authentication](authentication.md#roles). |
| `auth.sessionDuration` / `auth.loginRateLimitPerMinute` | Session lifetime (default `12h`) and admin login attempts per client IP per minute (default `5`). |
| `otel.endpoint` | OTLP collector endpoint |
| `otel.webServiceName` | `OTEL_SERVICE_NAME` for the web service (default: `coding-agent-web`). The web service's service name is hardcoded at compile time via `AddService(serviceName:...)` in `OpenTelemetryRegistration.cs` — it is not configurable via the `OTEL_SERVICE_NAME` env var the way the other processes are. API, Job Controller, and Scheduler read `OTEL_SERVICE_NAME` at startup with fixed-name fallbacks (`coding-agent-api`, `coding-agent-jobcontroller`, `coding-agent-scheduler`). |
| `otel.apiServiceName` | `OTEL_SERVICE_NAME` for the Pipeline API process (default: `coding-agent-api`). Separates API spans and metrics from the Blazor web process in Tempo and Prometheus. ⚠️ If upgrading from a release where this defaulted to `coding-agent-web`, update any Grafana dashboards or alerts that filter on `service.name="coding-agent-web"` for API traffic. |
| `web.env.faroCollectorUrl` | Grafana Faro collector URL for frontend RUM monitoring. Leave empty to disable (default: `""`). See [Faro configuration](configuration.md#frontend-observability-grafana-faro) for details. |
| `web.env.basePath` | Base path for the Blazor web UI, injected as the `<base href>` in the HTML shell (default: `"./"`, the app root). With the default or another relative value, the web host renders a relative base that climbs from the requested route back to the app root (`./` on `/overview`, `../` on `/runs/{id}`), so directly loaded nested routes find their assets both at a root-path deployment and behind a reverse proxy that strips its prefix (e.g., Rancher's service proxy). An absolute path (e.g., Rancher: `"/k8s/clusters/c-xxxxx/proxy/"`) pins the base and is used as is; a trailing `/` is appended if missing. **Not supported together with sign-in (Spec 049):** login redirects need the UI at the root of its host; use an Ingress instead. |
| `database.host` | PostgreSQL hostname (required) |
| `database.port` | PostgreSQL port (default: `5432`) |
| `database.auth.existingSecret` | K8s Secret containing database credentials (keys: `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`) |
| `database.migrateOnStartup` | Apply EF Core migrations on Pipeline API startup (default: `true`). Set `false` only for blue/green deployments where you apply migrations manually via `kubectl exec` into the API pod before cutover. |
| `database.sslMode` | Npgsql SSL mode: `Disable`, `Prefer`, `Require`, `VerifyCA`, `VerifyFull`. Defaults to `Require` in production if not set. Use `Disable` for in-cluster Postgres without TLS. |
| `workDistribution.dispatch.intervalSeconds` | Seconds between dispatch cycles (default: `10`) |
| `workDistribution.dispatch.rateLimitPerSecond` | Max dispatches per second (default: `10`) |
| `workDistribution.dispatch.chatJobMaxDurationSeconds` | Max lifetime (seconds) of a **chat session** K8s Job pod (default: `7200`). Sets `activeDeadlineSeconds` on chat pods only. Work-item agent jobs and consolidation jobs derive their deadline from `PipelineConfiguration.AgentTimeout` (per-project overridable, default 30 min). |
| `workDistribution.dispatch.chatPodConnectTimeoutSeconds` | Max seconds to wait for a chat pod to connect to the hub after Job creation (default: `120`). |
| `workDistribution.dispatch.chatTerminationGracePeriodSeconds` | `terminationGracePeriodSeconds` on chat pod spec (default: `120`). |
| `workDistribution.dispatch.chatIdleTimeoutSeconds` | Seconds a chat pod may remain idle (no client keepalive) before automatic termination (default: `90`). Minimum: `10`. |
| `workDistribution.reconciliation.intervalSeconds` | Seconds between reconciliation cycles (default: `30`) |
| `workDistribution.reconciliation.staleRetentionDays` | Days to retain stale work items before cleanup (default: `7`) |
| `credentialPools.kiro` | List of PVC names for Kiro agent credential data. PVCs **must** use `ReadWriteOnce` or `ReadWriteOncePod` to prevent concurrent access from multiple agent Jobs. The pipeline API claims one PVC per dispatched Job. |
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

### Leader Election Without Kubernetes

When the Kubernetes client is unavailable (e.g., local `dotnet run`), `ILeaderElectionService` logs a warning and remains non-leader for the lifetime of the process. `PipelineLoopService` null-checks the leader gate: when the gate is `null`, the loop runs unconditionally — no leader gate needed for single-instance local dev.

> **Note on Redis and multi-replica API:** When Redis is configured (`signalr.redis.connectionString` is set), `AgentRegistryService` and `OrchestratorRunService` switch to distributed Redis-backed implementations (`DistributedAgentRegistryService` / `DistributedRunService`). This enables safe agent selection and run tracking across multiple API replicas. The in-process implementations are used only when Redis is absent (local dev or single-replica deployments).
>
> **Note:** Redis does **not** eliminate the PVC dispatch race on the Consolidation synchronous dispatch path — that race is separate and requires a Postgres advisory lock. Occasional `503` responses from `POST /api/work-items/dispatch` in multi-replica deployments are expected and self-healing: the Scheduler re-queues the issue on its next poll cycle. See [Concurrency Model — PVC Dispatch Race in Multi-Replica Deployments](architecture/concurrency-model.md#pvc-dispatch-race-in-multi-replica-deployments) for details.

> **Note on Data Protection and Redis:** The Orchestrator (Blazor Server) uses ASP.NET Core Data Protection to encrypt antiforgery tokens for Blazor circuits. In a multi-replica Orchestrator deployment each pod generates its own ephemeral key ring by default. If the load balancer routes the initial page request to replica A (token encrypted with A's key) but the Blazor WebSocket to replica B, circuit initialization fails with a `CryptographicException`. When `signalr.redis.connectionString` is set, the Orchestrator persists Data Protection keys to Redis under `caa:data-protection-keys` so all replicas share one ring. This is the same connection string used for the SignalR backplane — no additional config key is needed.

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

Three independent leases are used — one per relevant process (the Pipeline API has no leader election):

**Job Controller** (`caa-{release}-dispatch-lock` lease):

| Service | Behavior When Leader | Behavior When Non-Leader |
|---------|---------------------|--------------------------|
| `ReconciliationService` | Runs startup reconciliation, watches K8s Jobs, enforces timeouts | Waits (linked `LeaderToken` is cancelled, re-checks on leadership change) |

**Pipeline API** — No leader election. All API replicas handle requests concurrently. `DatabaseMaintenanceService` is a singleton triggered by the Scheduler via HTTP (`POST /api/scheduler/maintenance/retention-sweep`); `ChatJobDispatcher` uses K8s double-dispatch guards instead of a lease. Set `signalr.redis.connectionString` when running more than one API replica.

**Orchestrator** (`caa-{release}-pipeline-loop-lock` lease) — Deprecated: `PipelineLoopService` moved to the Scheduler in Spec 047. The Orchestrator now only polls `/loop/status` and dispatches individual runs via HTTP. The lease still exists in the Helm chart but governs no background services in the Orchestrator process. It can be ignored for operational purposes.

**Scheduler** (`caa-{release}-scheduler-lock` lease):

| Service | Behavior When Leader | Behavior When Non-Leader |
|---------|---------------------|--------------------------|
| `PipelineLoopService` | Dispatches pipeline runs | Pauses (leader gate blocks loop entry) |
| `OrphanedLabelRecoveryService` | Sweeps for issues with stale `agent:in-progress` labels | Waits |
| `HousekeepingService` | Manages `agent:done` PRs, branch updates, and stale branch cleanup | Waits |
| `WorkItemCountsService` | Emits work-item count metrics to `CodingAgent.WorkDistribution` | Waits |

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

Helm sets the lease name via `jobController.leaderElection.dispatchLeaseName` (Job Controller, defaults to `caa-{release}-dispatch-lock`), `web.leaderElection.pipelineLoopLeaseName` (web, defaults to `caa-{release}-pipeline-loop-lock`), and `scheduler.leaderElection.leaseName` (Scheduler, defaults to `caa-{release}-scheduler-lock`). The Pipeline API has no leader election lease.

#### RBAC Requirements

The Helm chart creates ServiceAccounts and ClusterRoleBindings (or RoleBindings) automatically for each process.

**Orchestrator** (`CodingAgent.Web`) ServiceAccount:

```yaml
rules:
  - apiGroups: ["coordination.k8s.io"]
    resources: ["leases"]
    verbs: ["create", "get", "update"]
```

The Orchestrator only needs leader-election Lease access. It has no direct K8s Job dispatch — `ChatJobDispatcher` and work-item dispatch both run in the Pipeline API and Job Controller respectively.

**Pipeline API** (`CodingAgent.Api`) ServiceAccount:

```yaml
rules:
  - apiGroups: ["batch"]
    resources: ["jobs"]
    verbs: ["create", "get", "list", "watch", "delete"]
  - apiGroups: [""]
    resources: ["secrets"]
    verbs: ["create", "delete"]   # per-Job derived-key Secrets (GC'd via ownerReference)
  - apiGroups: [""]
    resources: ["pods", "configmaps"]
    verbs: ["get", "list"]
```
The Pipeline API has no leader election, so it does not need a `coordination.k8s.io/leases` rule.

**Job Controller** (`CodingAgent.JobController`) ServiceAccount:

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

### Health Probe Endpoints

All deployments expose `/healthz` (liveness) and `/readyz` (readiness) endpoints on port 8080. No authentication required.

> **Note:** The Scheduler Dockerfile uses `/health` in its `HEALTHCHECK` instruction rather than `/healthz`. Both paths return the same response — the difference only affects the Docker-level health check, not Kubernetes probes (which read from `values.yaml`).

| Process | `/healthz` behavior | `/readyz` behavior |
|---------|--------------------|--------------------|
| Pipeline API | `200 ok` unless Redis ping fails (when Redis is configured, returns `503 redis_ping_failed`) | `503` when DB is unreachable or Redis backplane is configured and disconnected |
| Orchestrator | `200 ok` | `503` during graceful drain (`readinessDrainDelaySeconds` window) or when DB/infrastructure is unreachable |
| Job Controller | `200 ok` | `200 ok` (non-leader is considered ready; availability is controlled by leader election) |
| Scheduler | `200 ok` | `200 ok` (same rationale as Job Controller) |

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
dotnet run --project src/CodingAgent.Web/
```

> `PipelineApi__BaseUrl` must be set — the Orchestrator has no direct database connection and will fail to start without the Pipeline API URL. `Database__Host` is required by the **Pipeline API** (`src/CodingAgent.Api/`), not the Orchestrator.

For the agent project (work-item mode, connecting to the Pipeline API hub on port 8080):
```bash
ORCHESTRATOR_URL=http://localhost:8080 AGENT_ID=local-agent-1 AGENT_API_KEY=<key> \
  dotnet run --project src/CodingAgent.Agent/ -- --mode=workitem --work-item-id=<guid>
```

For chat mode (no `--work-item-id`):
```bash
ORCHESTRATOR_URL=http://localhost:8080 AGENT_ID=local-chat-1 AGENT_API_KEY=<key> \
  dotnet run --project src/CodingAgent.Agent/ -- --mode=chat
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
