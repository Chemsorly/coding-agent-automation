# HTTP API Reference

The Pipeline API (`CodingAgent.Api`, port **8080**) exposes HTTP API endpoints for programmatic access to work item management, configuration, run history, and health probes.

## Availability

| Endpoint Group | Available |
|---------------|-----------|
| Work Items (`/api/work-items/`) | ✅ |
| Config Import/Export (`/api/config/`) | ✅ |
| Run Export (`/api/export/`) | ✅ |
| Health Probes (`/healthz`, `/readyz`) | ✅ |
| UI surface (`/api/ui/`) | ✅ |

> **Note:** The agents' SignalR hub (`/hubs/agent`) is a WebSocket transport endpoint, not a REST API, and is not covered in this reference. The UI hub (`/hubs/ui`) is described under [UI Surface Endpoints](#ui-hub-hubsui).

## Authentication

The API has two surfaces, and each accepts one kind of credential:

- **Service surface** — every path except `/api/ui/**`: the agent API keys below (`AgentApiKey`, `OperatorApiKey`).
- **UI surface** — `/api/ui/**`: a user token (`Authorization: Bearer <token>`) signed by the web host for the signed-in user. See [Authentication and Roles](authentication.md). API keys get `401` there, and user tokens get `401` on the service surface.

An endpoint without a declared policy is denied (`401` without credentials, `403` with an API key). Only `/healthz` and `/readyz` are anonymous.

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

The service surface supports two kinds of API key:

1. **Master key (legacy)** — Use the raw `AGENT_API_KEY` value directly as the Bearer token. This works when no `agentId` query parameter is provided.

2. **HMAC-derived key (recommended)** — Each agent derives a unique key from the master key and its agent ID:
   ```
   derived_key = HMAC-SHA256(master_key, agent_id)  →  lowercase hex string
   ```
   Pass the derived key as the Bearer token and include `?agentId=<agent_id>` as a query parameter.

The `AGENT_API_KEY` environment variable of the Pipeline API (and of the Scheduler and Job Controller, which call it) sets the master key. See [Configuration](configuration.md#pipeline-api) for details.

---

## Work Item Endpoints

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
| 403 | An agent-derived key was used for a work item that is not assigned to that agent (its `AssignedAgentId` or `K8sJobName` does not match the `agentId`), or the key carries no agent identity |
| 404 | Work item not found or has no payload |
| 410 | Work item is in a terminal state (`Succeeded`, `Failed`, or `Cancelled`) |
| 503 | The assignment could not be enriched with fresh configuration (no matching agent profile, deleted provider, transient database failure). The agent retries; the work item stays `Dispatched` |

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
  "failureReason": null,
  "branchName": null
}
```

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `status` | string | ✅ | Target status (see valid values below) |
| `agentId` | string | ❌ | Identifier of the reporting agent |
| `result` | string | ❌ | Result payload (e.g., serialized output on success) |
| `errorMessage` | string | ❌ | Human-readable error description (set on failure) |
| `failureReason` | string | ❌ | Machine-readable failure classification (see valid values below) |
| `branchName` | string | ❌ | Feature branch the run works on. Stored on the work item so the Scheduler's housekeeping guard can see which branches have an active run |

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
| `QualityGateExhausted` | All quality gate retries exhausted — run finalized as a draft PR |
| `GateRejected` | The analysis gate rejected the issue (needs refinement or won't do). Not an agent error |

Values are matched case-insensitively. If `status` is `Failed` and no `failureReason` is provided, or the value is unknown, it defaults to `AgentError`.

**Response codes:**

| Status | Description |
|--------|-------------|
| 200 | Transition accepted |
| 204 | The work item is already at the requested status, or is already terminal. The first terminal report wins and later ones are no-ops |
| 400 | Invalid status transition (e.g., `Succeeded` → `Running`) |
| 403 | An agent-derived key was used for a work item that is not assigned to that agent |
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

### POST /api/work-items/{id}/priority

Set the dispatch priority weight for a pending work item. Operators call this to reorder items within the same priority tier before they are claimed by the Job Controller.

**Authentication:** OperatorApiKey

**Request body:**

```json
{ "priorityWeight": 100 }
```

`priorityWeight` is required and must be in the range `[0, 1000]`. A work item starts at `100` when it was dispatched manually and at `0` otherwise. Higher values dispatch first within the same `RunType` tier.

**Responses:**

| Status | Description |
|--------|-------------|
| 200 | Priority updated |
| 400 | `priorityWeight` missing or outside `[0, 1000]` |
| 404 | Work item not found |
| 409 | Work item is not in `Pending` status, or a concurrent update conflict occurred (retry) |

---

### GET /api/work-items/active

List work items that are `Dispatched` or `Running` and were dispatched more than `olderThanSeconds` seconds ago. Items whose `DispatchedAt` is null are matched on `CreatedAt` instead. `Pending` and terminal items are never returned. Used by the Job Controller's reconciliation loop (timeout enforcement, the Dispatched sweep and orphan cleanup) and by the web Work page, which also passes `projectId`.

**Authentication:** OperatorApiKey

**Query parameters:**

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `olderThanSeconds` | integer | ✅ | Only items dispatched more than this many seconds ago are returned; `0` returns all active items |
| `projectId` | GUID | ❌ | Restrict the result to one project |

**Response:** `200` — JSON array of `ActiveWorkItemDto`

| Field | Type | Description |
|-------|------|-------------|
| `id` | GUID | Work item identifier (= `PipelineRun.RunId`) |
| `status` | string | `Dispatched` or `Running` |
| `dispatchedAt` | datetime? | When the item was dispatched (null when the dispatch time was never written; such items are matched on their creation time) |
| `agentSelector` | string | Label selector used to match an agent |
| `issueIdentifier` | string | Issue or PR identifier (e.g., `owner/repo#42`) |
| `issueTitle` | string? | Issue title extracted from payload; null for older items |
| `k8sJobName` | string? | K8s Job name assigned at dispatch; null if not yet dispatched |
| `timeoutSeconds` | int | Per-item agent timeout in seconds, set when the item was queued. Items with 0 or less (rows from before the field existed) are skipped by timeout enforcement; there is no fallback to the global `AgentTimeout` |
| `currentStep` | string? | Current `PipelineStep` value from the in-memory run state. `null` when no live run is tracked (e.g., after API restart without Redis). |
| `createdAt` | datetime? | When the item was queued |
| `issueUrl` | string? | Web URL of the issue; null when the payload has none |
| `initiatedBy` | string? | Dispatch source from the payload (for example `loop:issue`, `manual`) |
| `projectId` | GUID? | Owning project; null for work without a project |

---

## Triage Endpoints

The [triage](triage.md) endpoints, on the UI surface under `/api/ui/triages` (spec 051 2.7). They take only the signed-in user's token. Reading needs read on the triage's project; a list without `projectId` needs a global role. Starting, re-running, editing drafts, creating issues and dismissing need `operator` on the project. Every call that names a triage is checked against the project of the stored triage, so a triage the user cannot see answers `404`. The API records the acting user (`requestedBy`, `author`, `editedBy`, `createdBy`, `by`) from the token, whatever the body says. Bodies and responses use camelCase with snake_case enum values such as `need_you` or `cause_found`. A refusal answers with the reason as a JSON string.

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/ui/triages` | List: `projectId` (omit for all projects, global readers only), `tab` (`all`, `need_you`, `investigating`, `done`), `source` (`operator`, `issue`), `q` (title contains), `page`, `pageSize`. Returns `{ items, total }` |
| GET | `/api/ui/triages/similar` | Triages of `projectId` from the last 30 days whose titles share words with `title` (at most 3) |
| GET | `/api/ui/triages/preview` | What an operator triage of `projectId` would use, started in `startIn` (optional template id): the repositories (and which one runs the job), the MCP servers, tracker count, brain, steering, time limit, and why it cannot start, if so. Needs `operator` on the project |
| GET | `/api/ui/triages/by-run/{runId}` | `{ id }` of the triage a run belongs to, or `404` |
| GET | `/api/ui/triages/{id}` | The triage record with its status, the running attempt's WorkItem id and run facts per attempt |
| POST | `/api/ui/triages` | Start an operator triage: `{ projectId, request: { title, whatHappened, expected, from?, until?, environment?, where?, startInTemplateId?, version?, links?, alreadyTried? } }`. Writes the triage and its Pending WorkItem together |
| POST | `/api/ui/triages/{id}/rerun` | Start another attempt: `{ feedback?, answers?: [{ question, answer }] }` (the first 10 answers are kept; feedback and answers together have at most 40,000 characters). For a label triage it posts the feedback on the issue and sets `agent:triage`; the loop then starts the attempt |
| PUT | `/api/ui/triages/{id}/drafts/{draftId}` | Edit a draft: `{ kind, targetRepository, title, body }` (title at most 200, body at most 12,000 characters) |
| POST | `/api/ui/triages/{id}/drafts/{draftId}/reset` | Restore the agent's version of a draft |
| POST | `/api/ui/triages/{id}/create-issues` | Create issues from drafts: `{ draftIds, queue }` → `{ created, errors }`. Drafts already created are skipped |
| POST | `/api/ui/triages/{id}/dismiss` | Dismiss: `{ reason? }` |

**Status codes:** `400` invalid input (for example a repository or start-in template that is not an enabled template of the triage's project, or a project without an enabled template), `404` unknown triage, draft or project, `409` an attempt is running (re-run, create issues, dismiss), the draft's issue already exists, or the triage changed meanwhile, `502` a label triage's issue could not be updated for a re-run (no attempt was queued).

---

## Config Import/Export Endpoints

> ⚠️ **Warning:** The import endpoint is destructive — it clears ALL existing configuration before inserting the uploaded bundle. This operation is transactional (atomic commit or full rollback).

### GET /api/config/export

Download the full pipeline configuration as a JSON file.

| Property | Value |
|----------|-------|
| **Auth** | `OperatorApiKey` (required — agent-derived keys are rejected with 403) |
| **Response Content-Type** | `application/json` |
| **Response filename** | `pipeline-config-export.json` |

**Example request:**

```bash
curl -H "Authorization: Bearer $OPERATOR_API_KEY" \
  -o pipeline-config-export.json \
  http://localhost:8080/api/config/export
```

**Example response (200):**

```json
{
  "pipelineConfig": "{\"maxRetries\":3,\"agentTimeout\":\"00:30:00\",\"brainReadOnly\":false}",
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
      "settings": "{\"maxRetries\":5}"
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

A project's templates are the `jobTemplates` whose `projectId` points to it. A `templateIds` list in a bundle from an older version is ignored on import.

---

### POST /api/config/import

Upload a configuration bundle to replace all existing pipeline configuration.

| Property | Value |
|----------|-------|
| **Auth** | `OperatorApiKey` (required — agent-derived keys are rejected with 403) |
| **Content-Type** | `multipart/form-data` |
| **Form field** | `file` — The JSON bundle file |

**⚠️ This endpoint clears all existing configuration** (providers, profiles, quality gates, reviewers, projects, templates) before importing. Run history, work items, and consolidation data are preserved.

**Example request:**

```bash
curl -X POST \
  -H "Authorization: Bearer $OPERATOR_API_KEY" \
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
| Settings out of range or unreadable | `"Invalid settings: {details}"` (the whole bundle is rejected; nothing is imported) |

---

## Run Export Endpoint

### GET /api/export/runs.json

Download pipeline run history as a JSON file.

| Property | Value |
|----------|-------|
| **Auth** | `OperatorApiKey` (required — agent-derived keys are rejected with 403) |
| **Response Content-Type** | `application/json` |
| **Response filename** | `pipeline-runs-{date}.json` (e.g., `pipeline-runs-2026-07-15.json`) |

**Query parameters:**

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `feedbackOnly` | boolean | `false` | When `true`, only returns runs that have structured feedback attached |
| `page` | integer | — | Page number (1-based, default 1 when `pageSize` is given). Providing `page` or `pageSize` returns a page slice instead of the full list |
| `pageSize` | integer | `50` | Page size, default 50. Providing it also turns paging on |

> **Note:** `feedbackOnly` is applied to the page slice when pagination is active — it does not filter across all pages before slicing. For full cross-run filtering, omit pagination and use `feedbackOnly` alone.

**Example request:**

```bash
# Download all runs
curl -H "Authorization: Bearer $OPERATOR_API_KEY" \
  -o runs.json http://localhost:8080/api/export/runs.json

# Download only runs with feedback
curl -H "Authorization: Bearer $OPERATOR_API_KEY" \
  -o feedback-runs.json "http://localhost:8080/api/export/runs.json?feedbackOnly=true"
```

**Example response (200):**

```json
[
  {
    "runId": "run-abc123",
    "issueIdentifier": "my-org/my-repo#42",
    "issueTitle": "Fix login timeout",
    "finalStep": "Completed",
    "startedAtOffset": "2026-07-15T10:30:00+00:00",
    "completedAtOffset": "2026-07-15T10:45:22+00:00",
    "retryCount": 0,
    "pullRequestUrl": "https://github.com/my-org/my-repo/pull/123",
    "runType": "Implementation",
    "modelName": "claude-sonnet-4-20250514",
    "agentId": "agent-dotnet-1",
    "initiatedBy": "loop:issue",
    "harnessVersion": "6f1c9a0e3b7d42a85c10e9f4d2b8a7136e5c0f94",
    "totalTokens": 125000,
    "totalCost": 0.45,
    "feedback": null
  }
]
```

The response is a JSON array of run summary objects. Each object includes run metadata, timing, token/cost usage, and optional feedback. The full schema has 25+ fields — see `PipelineRunSummary` in the source for the complete list.

**`initiatedBy` values:**

| Value | Meaning |
|-------|---------|
| `loop:issue` | Issue-implementation run dispatched by the polling loop |
| `loop:review` | PR review run dispatched by the polling loop |
| `loop:decomposition` | Epic decomposition run dispatched by the polling loop |
| `manual` | Run dispatched manually from the UI |
| `consolidation:manual` | Consolidation run triggered via the Consolidation page |
| `consolidation:auto` | Consolidation run triggered automatically |
| `rehydrated` | Run re-created from a restarted orchestrator (original source unknown) |

**`harnessVersion` field:**

Present on runs recorded after the version identity feature was introduced. Contains the git commit SHA of the agent image that executed the run (the `SERVICE_VERSION` value, for example `6f1c9a0e...`; `local` for locally built images). `null` for older runs predating this field. Set from the `SERVICE_VERSION` environment variable on the agent pod.

---

## UI Surface Endpoints

Every endpoint under `/api/ui` requires a user token whose grant has a role (global or on at least one project); a token without any role gets `403`. An endpoint can require more (for example a global role). Requests about a project the user cannot see answer `404`; requests the user can see but not change answer `403`.

### GET /api/ui/me

Returns the user and the roles their token grants, with project names resolved.

**Example response (200):**

```json
{
  "subject": "oidc:5f0c...",
  "username": "alice",
  "identitySource": "oidc",
  "globalRole": "readonly",
  "projectRoles": [
    { "projectId": "6f1c2a9e-0000-0000-0000-00000000000a", "projectName": "Alpha", "role": "operator", "known": true },
    { "projectId": "6f1c2a9e-0000-0000-0000-00000000000b", "projectName": null, "role": "readonly", "known": false }
  ]
}
```

`known: false` means no project has that ID; the binding grants nothing usable.

### GET /api/ui/health

Database and Redis state of the answering API replica, for the web UI's sidebar. Any role. It reads cached state only (the database monitor behind `/readyz`, the Redis connection flag). `redisConnected` is `null` when the API runs without Redis.

```json
{ "databaseConnected": true, "redisConnected": true }
```

### Configuration reads (any role)

These return slim views of the configuration for pages outside Settings. They never contain prompts, secrets or MCP servers.

| Path | Returns |
|---|---|
| `GET /api/ui/projects` | The projects the user can read, as `{ id, name, enabled }`, by name. A global reader gets all of them; a project-scoped user gets only its own. |
| `GET /api/ui/projects/{projectId}/templates` | The project's job templates as `{ id, name, enabled }`. `404` when the user cannot read the project or it does not exist. |
| `GET /api/ui/pipeline-settings` | `{ maxRetries, triageRetentionDays }`: global values, without project overrides. |
| `GET /api/ui/pipelines` | The Pipelines page: `{ projects, templates, issueProviders, repoProviders, pipelineProviders, qualityGateConfigs, reviewerConfigs, agentProfiles, defaultRequiredAgentLabels, maxRetries }`. Only the readable projects' templates (a template without a project only for global readers); projects carry only their id, name, enabled flag and template ids; providers and agent profiles come with secrets masked. A project-scoped user gets only the providers their templates use, and empty quality gate, reviewer and agent profile lists. |

### Runs and work items (any role)

The lists take an optional `projectId`:
- Without it, the user needs global read; a project-scoped user gets `403`.
- With it, the user needs read on that project; otherwise `404`.

The run detail is checked against the run's own project. A run the user cannot read is `404`, like a missing one.

| Path | Returns |
|---|---|
| `GET /api/ui/runs` | Run history, newest first. It has the same query parameters as `GET /api/pipeline-runs`: `page`, `pageSize`, `feedbackOnly`, `includeActive`, `finalStep`, `projectId`, `since` and `runType`. |
| `GET /api/ui/runs/{runId}` | `{ run, agentProviderName }`: the run, and the display name of the agent provider it ran with. |
| `GET /api/ui/work-items/pending?maxResults=&projectId=` | The queue, in dispatch order. |
| `GET /api/ui/work-items/active?olderThanSeconds=&projectId=` | Dispatched and running work items. |

The actions on one work item need the `operator` role on the work item's stored project. A user who can only read that project gets `403`; a user who cannot see it gets `404`. A run's ID is its work item's ID, so cancelling a run uses the same endpoint.

| Path | Does |
|---|---|
| `POST /api/ui/work-items/{id}/cancel` | Cancels a queued item or an in-flight run. Returns `204` when it had already ended. |
| `PUT /api/ui/work-items/{id}/priority` | Sets the dispatch weight of a queued item from `{ priorityWeight }` (0–1000). Returns `400` outside that range and `409` once the item has left the queue. |

Re-dispatch needs the `operator` role on the run's stored project, with the same `403` and `404` rules.

| Path | Does |
|---|---|
| `POST /api/ui/runs/{runId}/redispatch` | Queues a new implementation run for the issue of a failed, cancelled or conflicted implementation run, with the run's providers and project. Returns `200` with the message to show, as a JSON string. Any other run, or an issue that already has queued or running work, is refused with `409` and the reason. Blocking labels such as `agent:error` are not cleared. |

### Backlog (any role)

`GET /api/ui/backlog?projectId=` returns the open issues of the enabled templates' issue providers, as `{ issues, isTruncated }`. Each issue says whether it is ready to dispatch. It is not ready when it has open dependencies (`blockedBy`, `blockedByUrls`) or carries a lifecycle label such as `agent:in-progress` or `backlog`. The API calls the providers with the stored credentials. `isTruncated` is set when a provider had more than 200 open issues. The `projectId` rule is the same as for the lists above.

### Template browse (any role)

The dispatch drawers browse a template's providers through `/api/ui/templates/{templateId}/…`. A template needs read on the project that owns it, and a template of no project needs global read. A template the user cannot read is `404`, like a missing one. When the template's provider is gone or fails, the API answers `502` with the message to show, as a JSON string. Pages hold 15 issues or pull requests. `labels` can be repeated and is passed to the provider's label filter.

| Path | Returns |
|---|---|
| `GET …/issues?page=&labels=` | A page of the template tracker's open issues, as `{ items, page, pageSize, hasMore }`. |
| `GET …/issues/readiness?page=&labels=` | The dependency readiness of the same page's issues, by identifier. The checks are best-effort: when one fails, the issues checked so far are returned. |
| `GET …/labels` | The labels of the template's issue tracker. |
| `GET …/pull-requests?page=&labels=` | A page of the template repository's open pull requests. |
| `GET …/epics?page=&labels=` | The new and the approved epics (8 of each per page), merged. They come from the project's epic tracker when it has one, otherwise from the template's tracker. |
| `GET …/epic-labels` | The epic tracker's labels, without the epic workflow labels. |
| `GET …/active-issues` | The issues and pull requests of the template's trackers and repository that already have work, as `[{ issueIdentifier, issueProviderConfigId, status }]`. `Pending` means queued. `Running` covers dispatched and running work, and work that ended within the dedup cooldown. |

### Manual dispatch (operator)

The dispatch drawers enqueue work with `POST /api/ui/templates/{templateId}/…/dispatch`. The request names only the template and the issue or pull request. The API reads the item from the template's providers, with the stored credentials. The caller needs operator on the template's project, and a template of no project needs global operator. A template the user cannot read is `404`; one they can read but not operate is `403`.

The answer is `200` with the message to show, as a JSON string. The work item is queued as `Pending`, and the Scheduler's dispatch loop starts it. A refusal is `409`, and a provider that is gone or fails is `502`; both carry the reason as a JSON string.

| Path | Dispatches |
|---|---|
| `POST …/issues/{issueIdentifier}/dispatch` | An issue of the template's tracker, for implementation. An issue with open dependencies, or one awaiting epic review, is refused. A dispatch-ineligible label such as `agent:error` or `agent:done` is replaced with `agent:next` first. |
| `POST …/pull-requests/{number}/dispatch` | An open pull request of the template's repository, for review. |
| `POST …/epics/{issueIdentifier}/dispatch` | An epic of the epic tracker (the project's, otherwise the template's own): for analysis, or for decomposition once it carries `agent:epic-approved`. |

### Agents

| Path | Returns |
|---|---|
| `GET /api/ui/agents` | The agent registry, in the same shape as `GET /api/agents`. A global reader gets every agent. A project-scoped user gets only the agents running work of their projects, so idle agents and other projects' runs are not shown to them. |
| `GET /api/ui/agents/credential-pool` | `{ total, available, claimed }` for the Kiro credential pool. `total` is `0` when pooling is not configured. Needs a global role. |

### Agent Chat (operator)

Every path needs an operator role. A chat runs with the MCP servers, secrets and steering of the project it is launched for, so launching it needs operator on that project, and a chat without a project needs global operator. The API records who launched each chat. Only that user can prompt it, keep it alive or end it, as long as they can still operate on its project; an admin can use any chat. Anyone else gets `404`, as for a chat that does not exist, and so does any agent that was not launched here: pipeline agents cannot be prompted. Refusals carry the reason as a JSON string: `409` for a request that cannot be served, and `502` when the pod cannot be started.

| Path | Does |
|---|---|
| `GET /api/ui/chat/options` | Returns `{ profiles, connectTimeoutSeconds }`. `profiles` lists the enabled agent profiles whose labels name a job template, as `{ id, displayName, matchLabels }`. |
| `POST /api/ui/chat` | Launches a chat pod from `{ profileId, projectId }` with the profile's selector and its provider's model and effort, then waits until the pod connects (up to `connectTimeoutSeconds`). Returns `{ agentId, model }`, where `model` is `auto` when the provider sets none. |
| `POST /api/ui/chat/{agentId}/prompt` | Sends `{ sessionId, text }`. The API adds the profile's and project's MCP servers, and on the first prompt also the project's secrets and steering. The reply streams to the chat's group on the [UI hub](#ui-hub-hubsui), tagged with the `sessionId`. Returns `204`. |
| `POST /api/ui/chat/{agentId}/keepalive` | Resets the idle clock of the chat's pod. Returns `204`. |
| `POST /api/ui/chat/{agentId}/terminate` | Ends the chat and stops its pod. Returns `204`. |

### Consolidation (global)

The Consolidation page's API. Several templates and projects can share one brain, so a project role is not enough: reading needs a global role and triggering the global `operator` role. The page lists consolidation runs with `GET /api/ui/runs?runType=Consolidation` and cancels a queued one with `POST /api/ui/work-items/{id}/cancel`.

| Path | Returns |
|---|---|
| `GET /api/ui/consolidation` | `{ templates, brainProviderByTemplate, harnessSuggestions }`. `templates` lists the enabled templates of enabled projects, by project name, as `{ id, name, supportsBrainConsolidation, supportsRefactoringDetection, brainReadOnly }`. `brainProviderByTemplate` maps every template that has a brain to its brain provider. `harnessSuggestions` is `null` until suggestions have been generated. |
| `GET /api/ui/consolidation/templates/{templateId}/refactoring-scan` | `{ maxProposals, maxProposalsIsProjectOverride, hotspotLookbackDays, reviewEnabled, reviewEnabledIsProjectOverride }`: the values a refactoring scan of the template runs with, which are the global settings with the template's project overrides. |
| `POST /api/ui/consolidation/runs` | Queues a run from `{ type, templateId, autoDispatch }`. `type` is `BrainConsolidation`, `RefactoringDetection` or `HarnessSuggestions`. `templateId` is `null` for harness suggestions. `autoDispatch` labels a refactoring scan's issues `agent:next`. Returns `200` with the message to show, as a JSON string. A run of the same scope that is already queued or running, a configuration error or an unknown template is refused with `409` and the reason. |

### Pipeline loop

Loop control is desired state. Start, Stop and Resume only record the request in the pipeline configuration and return `202`. The Scheduler leader checks every 5 seconds: it starts the loop if it should run and doesn't, stops it if it runs and shouldn't, and resumes it after a newer resume request. It then pushes the loop status to the API (`PUT /api/scheduler/loop-status`, operator key) when the status changes, and at least every 30 seconds. A Start the Scheduler refuses appears in the status's `validationErrors` a few seconds later. A Start while the Scheduler is down is applied when it comes back.

| Path | Who | Returns |
|---|---|---|
| `GET /api/ui/loop/status` | Any role | `{ isLoopActive, runRequested, statusMessage, currentIssueIdentifier, processedCount, failedCount, queueCount, isCircuitBroken, lastPollError, currentCycleTemplateIndex, currentCycleTemplateCount, validationErrors, templateStatuses, reportedAt }`. `runRequested` is whether Start was the last command; while it differs from `isLoopActive`, a Start or Stop is still being applied. `reportedAt` is `null` when the Scheduler has not reported in the last 2 minutes; the status then says it is waiting for the Scheduler. A project-scoped user gets only their projects' entries in `templateStatuses`, and no messages, errors or current issue, which can name other projects' templates and issues. |
| `POST /api/ui/loop/start` | Global `operator` | `202`. Asks for the loop to run. |
| `POST /api/ui/loop/stop` | Global `operator` | `202`. Asks for the loop to stop after the current cycle. |
| `POST /api/ui/loop/resume` | Global `operator` | `202`. Asks for a loop that its circuit breaker paused to resume. |

Saving the pipeline settings through `PUT /api/ui/settings/pipeline` keeps the stored loop request, so a settings page loaded before a Start or Stop does not undo it.

### UI hub (`/hubs/ui`)

The SignalR hub of the signed-in UI: live output of runs and replies of chats. It takes the user token (any role) over WebSockets, JSON protocol only; API keys and anonymous callers are refused. The token lasts the session, and the hub closes the connection when it expires. A connection joins the groups of what it shows, and the hub has no other methods:

| Method | Allowed when | Then receives |
|---|---|---|
| `SubscribeToRun(runId)` / `UnsubscribeFromRun(runId)` | The user can read the project of the run's work item. | The output so far and, while the run is active, a `RunStateSnapshot`; then `OnOutputLines`, `OnStepTransition`, `OnChatEntry`, `OnQualityGateResult`, `OnBrainSyncResult` and `OnRunCompleted`. |
| `SubscribeToChat(agentId)` / `UnsubscribeFromChat(agentId)` | The user launched the chat (or is a global `admin`) and can still operate on its project, as for the chat endpoints. | `OnChatResponse` and `OnChatCompleted` for every prompt of the chat; each carries the prompt's `sessionId`. |

A refused subscription fails with a `HubException` that reads the same whether the run or chat does not exist or belongs to someone else. `/hubs/agent` is for agent pods only: a connection without an `agentId` is closed, and user tokens are refused.

### Settings (admin)

`/api/ui/settings/**` is the Settings page's API and requires the global `admin` role. It covers the pipeline configuration, agent profiles, quality gate configs, reviewer configs, projects, provider configs, templates (`PUT /projects/{projectId}/templates`, `DELETE /projects/{projectId}/templates/{templateId}`, `POST /templates/move`), config import and export, and the first-run banner (`GET /first-run`, `POST /first-run/dismiss`). Validation is the same as on the service surface.

Secrets never leave the API here, not even for an admin:
- These values come back as `****`:
  - project secrets;
  - every MCP server env value and header (in projects and agent profiles);
  - provider secrets;
  - the provider settings `privateKeyBase64`, `token` and `accessToken`.
- Saving `****` keeps the stored value under the same key (MCP servers are matched by name). A key that is left out is removed.
- A `****` with no stored value behind it is refused with `400` and the key named. This happens, for example, after renaming an MCP server or for a new provider. Enter the value again.
- A stored provider credential is only used with its stored URL (`apiUrl`, `baseUrl`). A provider whose URL changed while a credential is still `****` is refused with `400`. Enter the credential again.
- The only exception is `GET /api/ui/settings/export`: the backup file contains live credentials, so treat it as a secret.

The provider operations of the Settings page run in the API:

| Path | Does |
|---|---|
| `GET /provider-configs?kind=` | The provider configs of a kind, masked. |
| `PUT /provider-configs` | Saves the config. With `?copyFrom={id}&copyFromKind={kind}`, a new config takes its `****` credentials from that stored provider. Settings creates a repository's related issue, repository and pipeline providers this way. |
| `DELETE /provider-configs/{id}?kind=` | Deletes the config. |
| `POST /provider-configs/check` | Checks the GitHub App or GitLab credentials in the posted form, and the repository or project when one is named. Returns `{ success, message }`. |
| `POST /provider-configs/actions-access` | Whether the GitHub App can read the repository's workflow runs, as `{ success, message }`. |
| `POST /provider-configs/repositories` | The repositories the GitHub App installation can access, as `[{ fullName, owner, name }]`. Returns `400` with the reason when the credentials fail. |
| `POST /provider-configs/initialize` | Checks access and creates the agent labels, as `{ success, message }`. `success` is `false` when some labels could not be created. Returns `400` with the reason when access fails. |
| `GET`, `POST /provider-configs/{id}/agent-labels` | Whether the stored issue provider's repository has the agent labels, or creates them. Returns `true` or `false`. |
| `GET /models` | The models the agent CLI offers. Returns `502` with the reason as a JSON string when the fetch fails. |

Every operation takes the form as posted. Its `****` values are filled in from the stored config with the same ID and kind, under the URL rule above.

---

## Health Probe Endpoints

> No authentication required.

### GET /healthz

Kubernetes liveness probe. Returns 200 if the process is running. Never checks external dependencies — failure triggers pod restart.

**Example response (200):**

```json
{
  "status": "ok"
}
```

### GET /readyz

Kubernetes readiness probe. Returns 200 if ready to accept traffic, 503 during graceful shutdown drain, database connectivity loss, or Redis backplane disconnection.

**Example response (200):**

```json
{
  "status": "ready"
}
```

**Example response (503 — database unreachable):**

```json
{
  "status": "unhealthy",
  "reason": "database_unreachable"
}
```

**Example response (503 — Redis backplane disconnected):**

```json
{
  "status": "unhealthy",
  "reason": "redis_backplane_disconnected"
}
```

---

## Endpoint Summary Table

| Method | Path | Auth | Description |
|--------|------|------|-------------|
| GET | `/api/work-items/{id}/assignment` | AgentApiKey | Fetch job assignment |
| POST | `/api/work-items/{id}/status` | AgentApiKey | Report status transition |
| POST | `/api/work-items/{id}/priority` | OperatorApiKey | Set dispatch priority weight (Pending items only) |
| GET | `/api/work-items/active` | OperatorApiKey | List Dispatched and Running work items older than a threshold |
| GET, POST, PUT | `/api/ui/triages/…` | User token; read or operator on the triage's project | [Triage endpoints](#triage-endpoints): list, read, preview, start, re-run, edit drafts, create issues, dismiss |
| GET | `/api/config/export` | OperatorApiKey | Download config bundle |
| POST | `/api/config/import` | OperatorApiKey | Upload config bundle (destructive) |
| GET | `/api/export/runs.json` | OperatorApiKey | Download run history |
| GET | `/api/ui/me` | User token | The signed-in user and their roles |
| GET | `/api/ui/health` | User token | Database and Redis state for the sidebar |
| GET | `/api/ui/projects` | User token | Projects the user can read |
| GET | `/api/ui/projects/{projectId}/templates` | User token | Templates of a readable project |
| GET | `/api/ui/pipeline-settings` | User token | Global settings pages outside Settings show |
| GET | `/api/ui/pipelines` | User token | The Pipelines page: readable projects' templates and the configuration the template table shows (secrets masked) |
| GET | `/api/ui/runs`, `/api/ui/runs/{runId}` | User token | Run history and run detail (per project) |
| GET | `/api/ui/work-items/pending`, `/api/ui/work-items/active` | User token | Queue and in-flight work items (per project) |
| POST, PUT | `/api/ui/work-items/{id}/cancel`, `/api/ui/work-items/{id}/priority` | User token, operator of the item's project | Cancel a run or queued item; set queue priority |
| POST | `/api/ui/runs/{runId}/redispatch` | User token, operator of the run's project | Queue a failed run's issue again |
| GET | `/api/ui/backlog` | User token | Open provider issues with their dispatch readiness (per project) |
| GET | `/api/ui/templates/{templateId}/…` | User token, read on the template's project | The drawers' issues, readiness, labels, pull requests, epics and active work |
| POST | `/api/ui/templates/{templateId}/…/dispatch` | User token, operator of the template's project | Queue an issue, pull request or epic from the drawers |
| GET | `/api/ui/agents` | User token | Agents (all for global readers; those running their projects' work for scoped users) |
| GET | `/api/ui/agents/credential-pool` | User token, global read | Kiro credential pool usage |
| GET, POST | `/api/ui/chat/options`, `/api/ui/chat` | User token, operator of the chosen project (global operator without one) | Launchable chat profiles; launch a chat pod |
| POST | `/api/ui/chat/{agentId}/prompt`, `/keepalive`, `/terminate` | User token, the chat's owner or an admin | Prompt, keep alive or end a chat |
| GET | `/api/ui/consolidation`, `/api/ui/consolidation/templates/{templateId}/refactoring-scan` | User token, global read | The Consolidation page's cards, suggestions and refactoring scan settings |
| POST | `/api/ui/consolidation/runs` | User token, global operator | Queue a brain consolidation, refactoring scan or harness suggestions run |
| GET | `/api/ui/loop/status` | User token | Pipeline loop status (template entries filtered for scoped users) |
| POST | `/api/ui/loop/start`, `/stop`, `/resume` | User token, global operator | Ask the Scheduler leader to start, stop or resume the loop |
| PUT | `/api/scheduler/loop-status` | OperatorApiKey | The Scheduler leader pushes the loop status |
| GET/PUT/POST/DELETE | `/api/ui/settings/**` | User token, admin | Settings (secrets masked; export unredacted) |
| GET | `/healthz` | Anonymous | Liveness probe |
| GET | `/readyz` | Anonymous | Readiness probe |
