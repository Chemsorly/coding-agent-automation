# Deployment

## Architecture

The application follows Clean Architecture with a multi-container deployment:

```mermaid
graph TB
    Browser[Web Browser]
    
    subgraph Orchestrator["Orchestrator Container"]
        WebUI[WebUI<br/>Blazor Server]
        Orch[Orchestration<br/>dispatch, tracking]
        Infra[Infrastructure<br/>providers, config store]
        Core[Pipeline Core<br/>interfaces, models]
    end
    
    subgraph Agents["Agent Containers"]
        A1[Kiro Agent .NET]
        A2[Kiro Agent Python]
        A3[Kiro Agent Java]
        A4[OpenCode Agent .NET]
        A5[OpenCode Agent Python]
        A6[OpenCode Agent Java]
    end
    
    Browser -->|HTTP/SignalR| WebUI
    WebUI --> Orch
    Orch --> Infra
    Orch --> Core
    Infra --> Core
    Orch -->|SignalR<br/>job dispatch| Agents
```

- **Orchestrator** — Web UI + pipeline orchestration + job dispatch. Single instance.
- **Agent Containers** — Worker containers connecting via SignalR. Two backends: Kiro CLI (process) and OpenCode (HTTP API). Scale by adding containers.

## Docker Compose

The `docker-compose.yml` defines 8 services: 1 orchestrator + 2 Kiro .NET agents + 1 Kiro Python agent + 1 Kiro Java agent + 1 OpenCode .NET agent + 1 OpenCode Python agent + 1 OpenCode Java agent.

To add more agents, copy a service definition with a new name and volume — don't use `--scale` (each agent needs its own named volume to avoid SQLite corruption).

## Volume Mounts

### Orchestrator

| Mount | Container Path | Purpose |
|-------|---------------|---------|
| Pipeline config | `/app/config/pipeline` | Provider configs, quality gates, profiles, run history (persists across restarts) |

### Agent Containers (Kiro CLI)

| Mount | Container Path | Purpose |
|-------|---------------|---------|
| Agent CLI auth | `/home/ubuntu/.local/share/kiro-cli` | Agent CLI login tokens |
| SSO cache | `/home/ubuntu/.aws` | SSO cache for agent CLI auth (mounted read-only) |

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
| `agents[]` | List of agent definitions (name, image, labels, providerType) |
| `agents[].providerType` | `kiro` or `opencode` — determines volume mount profile |
| `agents[].labels` | Comma-separated routing labels (e.g., `kiro,dotnet,dotnet10`) |
| `secrets.agentApiKey` | HMAC master key for agent auth |
| `secrets.otelHeaders` | OTLP auth headers |
| `secrets.opencodeConfigContent` | OpenCode config JSON (mounted as file for opencode agents) |
| `existingSecret` | Use a pre-existing K8s Secret instead of chart-managed one |
| `otel.endpoint` | OTLP collector endpoint |
| `orchestrator.ingress.enabled` | Enable Ingress for external access |

### Scaling Agents

Add entries to the `agents[]` list. Each entry produces a separate Deployment with dedicated storage:

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

### Graceful Shutdown

The chart supports zero-downtime rolling updates:
- Orchestrator uses `readinessDrainDelaySeconds` (default: 15s) to stop accepting traffic before terminating
- `pipelineLoopStartupDelaySeconds` (Helm default: 30s, application default: 90s) prevents dispatching to agents that are mid-termination — must be greater than agent `terminationGracePeriodSeconds`. The Helm value overrides the application's built-in default via the `PIPELINE_LOOP_STARTUP_DELAY_SECONDS` env var.
- Agent `terminationGracePeriodSeconds` defaults to 15s
