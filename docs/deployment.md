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

OpenCode agents have no volume mounts — they receive configuration via the `OPENCODE_CONFIG_CONTENT` environment variable injected at startup.

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
