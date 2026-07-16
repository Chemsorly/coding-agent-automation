# HTTP API Reference

The orchestrator exposes HTTP API endpoints for programmatic access to work item management, configuration, run history, and health probes.

## Availability by Deployment Mode

| Endpoint Group | DB Mode | File-Based Mode |
|---------------|---------|-----------------|
| Work Items (`/api/work-items/`) | ✅ Available | ❌ Not registered |
| Config Import/Export (`/api/config/`) | ✅ Available | ❌ Not registered |
| Run Export (`/api/export/`) | ✅ Available | ✅ Available |
| Health Probes (`/healthz`, `/readyz`) | ✅ Available | ✅ Available |

Work item and config endpoints are **only registered when a database connection string is configured** (DB+SignalR or DB+Kubernetes modes). The run export and health probe endpoints are always available regardless of deployment mode.

> **Note:** The SignalR hub (`/hubs/agent`) is a WebSocket transport endpoint used for real-time agent communication. It is not a REST API and is not covered in this reference.

## Authentication

Endpoints requiring the `AgentApiKey` policy accept credentials via two methods:

### Bearer Token (HTTP Header)

```
Authorization: Bearer <token>
```

### Query Parameter (WebSocket fallback)

```
GET /api/work-items/{id}/assignment?access_token=<token>
```

### Token Derivation

The orchestrator supports two authentication modes:

1. **Master key (legacy)** — Use the raw `AGENT_API_KEY` value directly as the Bearer token. This works when no `agentId` query parameter is provided.

2. **HMAC-derived key (recommended)** — Each agent derives a unique key from the master key and its agent ID:
   ```
   derived_key = HMAC-SHA256(master_key, agent_id)  →  lowercase hex string
   ```
   Pass the derived key as the Bearer token and include `?agentId=<agent_id>` as a query parameter.

The `AGENT_API_KEY` environment variable on the orchestrator sets the master key. See [Configuration](configuration.md#orchestrator) for details.

---

## Work Item Endpoints

> **DB-mode only** — These endpoints are not available in file-based mode.

### GET /api/work-items/{id}/assignment

Retrieve the job assignment details for a work item. Agents call this after being dispatched to fetch the full job payload.

| Property | Value |
|----------|-------|
| **Auth** | `AgentApiKey` (required) |
| **Path parameter** | `id` — Work item GUID |

**Response codes:**

| Status | Description |
|--------|-------------|
| 200 | Success — returns `JobAssignmentMessage` JSON |
| 404 | Work item not found or has no payload |
| 410 | Work item is in a terminal state (`Succeeded`, `Failed`, or `Cancelled`) |

**Example request:**

```bash
curl -H "Authorization: Bearer $AGENT_API_KEY" \
  http://localhost:8080/api/work-items/550e8400-e29b-41d4-a716-446655440000/assignment
```

**Example response (200):**

```json
{
  "jobId": "550e8400-e29b-41d4-a716-446655440000",
  "issueIdentifier": "owner/repo#42",
  "issueDetail": { "title": "Fix login bug", "body": "..." },
  "parsedIssue": { "title": "Fix login bug", "body": "..." },
  "issueComments": [],
  "repoProviderConfigId": "a1b2c3d4-...",
  "agentProviderConfigId": "e5f6g7h8-...",
  "providerConfigs": [],
  "pipelineConfiguration": { "maxRetries": 3, "agentTimeout": "00:30:00" },
  "initiatedBy": "manual",
  "qualityGateConfigs": [],
  "runType": "Implementation",
  "taskType": "Implementation"
}
```

---

### POST /api/work-items/{id}/status

Report a status transition for a work item. Agents call this to indicate progress (running), completion (succeeded), or failure.

| Property | Value |
|----------|-------|
| **Auth** | `AgentApiKey` (required) |
| **Path parameter** | `id` — Work item GUID |
| **Content-Type** | `application/json` |
| **Request size limit** | 1 MB |

**Request body:**

```json
{
  "status": "Running",
  "agentId": "agent-dotnet-1",
  "result": null,
  "errorMessage": null,
  "failureReason": null
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `status` | string | ✅ | Target status (see valid values below) |
| `agentId` | string | ❌ | Identifier of the reporting agent |
| `result` | string | ❌ | Result payload (e.g., serialized output on success) |
| `errorMessage` | string | ❌ | Human-readable error description (set on failure) |
| `failureReason` | string | ❌ | Machine-readable failure classification (see valid values below) |

**Valid `status` values:**

| Value | Description |
|-------|-------------|
| `Pending` | Awaiting dispatch |
| `Dispatched` | Assigned to an agent, awaiting execution |
| `Running` | Agent has started execution |
| `Succeeded` | Completed successfully |
| `Failed` | Failed (timeout, infrastructure, or agent error) |
| `Cancelled` | Cancelled by user or system |

**Valid `failureReason` values** (only applicable when `status` is `Failed`):

| Value | Description |
|-------|-------------|
| `Timeout` | Work item exceeded configured timeout |
| `InfrastructureFailure` | Infrastructure-level failure (pod scheduling, OOM) |
| `AgentError` | Agent reported an error during execution |
| `TokenRefreshFailure` | Token refresh failed, lost API access |
| `ExitCodeFailure` | Agent process exited with non-zero code |

If `status` is `Failed` and no `failureReason` is provided, defaults to `AgentError`.

**Response codes:**

| Status | Description |
|--------|-------------|
| 200 | Transition accepted |
| 400 | Invalid status transition (e.g., `Succeeded` → `Running`) |
| 404 | Work item not found |

**Example request:**

```bash
curl -X POST \
  -H "Authorization: Bearer $AGENT_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{"status": "Succeeded", "agentId": "agent-dotnet-1", "result": "{\"prUrl\": \"https://github.com/org/repo/pull/123\"}"}' \
  http://localhost:8080/api/work-items/550e8400-e29b-41d4-a716-446655440000/status
```

---

## Config Import/Export Endpoints

> **DB-mode only** — These endpoints are not available in file-based mode.
>
> ⚠️ **Warning:** The import endpoint is destructive — it clears ALL existing configuration before inserting the uploaded bundle. This operation is transactional (atomic commit or full rollback).

### GET /api/config/export

Download the full pipeline configuration as a JSON file.

| Property | Value |
|----------|-------|
| **Auth** | `AgentApiKey` (required) |
| **Response Content-Type** | `application/json` |
| **Response filename** | `pipeline-config-export.json` |

**Example request:**

```bash
curl -H "Authorization: Bearer $AGENT_API_KEY" \
  -o pipeline-config-export.json \
  http://localhost:8080/api/config/export
```

**Example response (200):**

```json
{
  "pipelineConfig": "{\"maxRetries\":3,\"agentTimeout\":\"00:30:00\",\"issuePageSize\":25}",
  "providerConfigs": [
    {
      "id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
      "kind": "Repository",
      "displayName": "GitHub — my-org",
      "providerType": "GitHubApp",
      "enabled": true,
      "configuration": "{\"appId\":\"12345\",\"installationId\":\"67890\",\"privateKey\":\"...\"}"
    }
  ],
  "agentProfiles": [
    {
      "id": "b2c3d4e5-f6a7-8901-bcde-f12345678901",
      "name": "dotnet-standard",
      "configuration": "{\"matchLabels\":[\"dotnet\"],\"agentImage\":\"coding-agent:latest\"}"
    }
  ],
  "qualityGateConfigs": [
    {
      "id": "c3d4e5f6-a7b8-9012-cdef-123456789012",
      "name": "dotnet-build-test",
      "configuration": "{\"buildCommand\":\"dotnet build\",\"testCommand\":\"dotnet test\"}"
    }
  ],
  "reviewerConfigs": [
    {
      "id": "d4e5f6a7-b8c9-0123-def0-234567890123",
      "name": "security-reviewer",
      "configuration": "{\"agentType\":\"KiroCli\",\"reviewFocus\":\"security\"}"
    }
  ],
  "projects": [
    {
      "id": "e5f6a7b8-c9d0-1234-ef01-345678901234",
      "name": "My Project",
      "enabled": true,
      "description": "Main product repository",
      "settings": "{\"maxRetries\":5}",
      "templateIds": ["f6a7b8c9-d0e1-2345-f012-456789012345"]
    }
  ],
  "jobTemplates": [
    {
      "id": "f6a7b8c9-d0e1-2345-f012-456789012345",
      "projectId": "e5f6a7b8-c9d0-1234-ef01-345678901234",
      "name": "dotnet-implementation",
      "configuration": "{\"repoProviderConfigId\":\"a1b2c3d4-...\",\"agentProviderConfigId\":\"...\"}"
    }
  ]
}
```

The `pipelineConfig`, `configuration`, and `settings` fields contain serialized JSON strings (double-encoded). This preserves the exact format used internally.

---

### POST /api/config/import

Upload a configuration bundle to replace all existing pipeline configuration.

| Property | Value |
|----------|-------|
| **Auth** | `AgentApiKey` (required) |
| **Content-Type** | `multipart/form-data` |
| **Form field** | `file` — The JSON bundle file |

**⚠️ This endpoint clears all existing configuration** (providers, profiles, quality gates, reviewers, projects, templates) before importing. Run history, work items, and consolidation data are preserved.

**Example request:**

```bash
curl -X POST \
  -H "Authorization: Bearer $AGENT_API_KEY" \
  -F "file=@pipeline-config-export.json" \
  http://localhost:8080/api/config/import
```

**Example response (200 — success):**

```json
{
  "success": true,
  "message": "Imported: 2 providers, 1 profiles, 1 quality gates, 1 reviewers, 1 projects, 1 templates"
}
```

**Example response (400 — validation error):**

```json
{
  "success": false,
  "message": "Invalid JSON: '<' is an invalid start of a value. Path: $ | LineNumber: 0"
}
```

**Error conditions (400):**

| Condition | Message |
|-----------|---------|
| No file uploaded | `"No file uploaded"` |
| Invalid JSON | `"Invalid JSON: {details}"` |
| Null/empty bundle | `"Empty or invalid bundle"` |

---

## Run Export Endpoint

> **Always available** — This endpoint is registered in all deployment modes (DB and file-based). No authentication required.

### GET /api/export/runs.json

Download pipeline run history as a JSON file.

| Property | Value |
|----------|-------|
| **Auth** | None (anonymous) |
| **Response Content-Type** | `application/json` |
| **Response filename** | `pipeline-runs-{date}.json` (e.g., `pipeline-runs-2026-07-15.json`) |

**Query parameters:**

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `feedbackOnly` | boolean | `false` | When `true`, only returns runs that have structured feedback attached |

**Example request:**

```bash
# Download all runs
curl -o runs.json http://localhost:8080/api/export/runs.json

# Download only runs with feedback
curl -o feedback-runs.json "http://localhost:8080/api/export/runs.json?feedbackOnly=true"
```

**Example response (200):**

```json
[
  {
    "runId": "run-abc123",
    "issueIdentifier": "my-org/my-repo#42",
    "issueTitle": "Fix login timeout",
    "finalStep": "PrCreated",
    "startedAtOffset": "2026-07-15T10:30:00+00:00",
    "completedAtOffset": "2026-07-15T10:45:22+00:00",
    "retryCount": 0,
    "pullRequestUrl": "https://github.com/my-org/my-repo/pull/123",
    "runType": "Implementation",
    "modelName": "claude-sonnet-4-20250514",
    "agentId": "agent-dotnet-1",
    "initiatedBy": "loop",
    "totalTokens": 125000,
    "totalCost": 0.45,
    "feedback": null
  }
]
```

The response is a JSON array of run summary objects. Each object includes run metadata, timing, token/cost usage, and optional feedback. The full schema has 25+ fields — see `PipelineRunSummary` in the source for the complete list.

---

## Health Probe Endpoints

> **Always available** — Registered in all deployment modes. No authentication required.

### GET /healthz

Kubernetes liveness probe. Returns 200 if the process is running. Never checks external dependencies — failure triggers pod restart.

**Example response (200):**

```json
{
  "status": "healthy",
  "timestamp": "2026-07-15T10:30:00Z"
}
```

### GET /readyz

Kubernetes readiness probe. Returns 200 if ready to accept traffic, 503 during graceful shutdown drain or database connectivity loss.

**Example response (200):**

```json
{
  "status": "ready",
  "timestamp": "2026-07-15T10:30:00Z"
}
```

**Example response (503 — draining):**

```json
{
  "status": "draining",
  "timestamp": "2026-07-15T10:30:00Z"
}
```

**Example response (503 — database unreachable):**

```json
{
  "status": "unhealthy",
  "reason": "database_unreachable",
  "timestamp": "2026-07-15T10:30:00Z"
}
```

---

## Endpoint Summary Table

| Method | Path | Auth | DB-Mode Only | Description |
|--------|------|------|:------------:|-------------|
| GET | `/api/work-items/{id}/assignment` | AgentApiKey | ✅ | Fetch job assignment |
| POST | `/api/work-items/{id}/status` | AgentApiKey | ✅ | Report status transition |
| GET | `/api/config/export` | AgentApiKey | ✅ | Download config bundle |
| POST | `/api/config/import` | AgentApiKey | ✅ | Upload config bundle (destructive) |
| GET | `/api/export/runs.json` | Anonymous | ❌ | Download run history |
| GET | `/healthz` | Anonymous | ❌ | Liveness probe |
| GET | `/readyz` | Anonymous | ❌ | Readiness probe |
